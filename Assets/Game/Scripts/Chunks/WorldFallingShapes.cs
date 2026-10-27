using System;
using System.Collections;
using System.Collections.Generic;
using Mirror;
using UnityEngine;

/// <summary>
/// Server-owned detached terrain pieces. The owner World already has a NetworkIdentity,
/// so pieces use reliable, chunked data RPCs + lightweight unreliable pose RPCs.
/// No new prefab, spawnable asset, or client-authoritative rigidbody is required.
/// </summary>
public partial class World
{
    [Header("Shape-preserving falling terrain")]
    public bool preserveDisconnectedShapes = true;
    [Tooltip("A disconnected component remains one visual mesh and Rigidbody, even when detailed or irregular.")]
    [Min(1)] public int shapeMeshCellsPerFrame = 4096;
    [Tooltip("Compound-box collision budget. Visual geometry remains full resolution.")]
    [UnityEngine.Range(8,128)] public int shapeCollisionBoxes = 48;
    [UnityEngine.Range(1,32)] public int maximumCeilingColumnsPerChunk = 12;
    [Min(1f)] public float shapeMaterialDensityKgPerM3 = 1000f;
    [Tooltip("Seconds before a ceiling fragment disappears. 0 keeps ceiling pieces for this session.")]
    [Min(0f)] public float ceilingShapeLifetime = 10f;
    [Tooltip("Seconds before a fully disconnected piece disappears. 0 keeps sculptures permanently and saves them. Positive values keep the shape while it falls, then despawn it (and do not persist active pieces between sessions).")]
    [Min(0f)] public float detachedShapeLifetime = 20f;
    [Tooltip("For temporary chunks only: remove them this many seconds after their Rigidbody comes to rest. Zero disables early removal; the maximum lifetime still applies.")]
    [Min(0f)] public float settledShapeLifetime = 5f;
    [UnityEngine.Range(4,128)] public int maximumDynamicShapeBodies = 24;
    [UnityEngine.Range(1,16)] public int shapePacketsPerFrame = 4;
    [Min(0.05f)] public float shapePoseInterval = 0.12f;

    [Header("Falling chunk impacts")]
    [Min(0.1f)] public float minimumShapeImpactSpeed = 2f;
    [Min(0f)] public float damagePerShapeSizeAndSpeed = 8f;
    [Min(1f)] public float maximumShapeImpactDamage = 90f;
    [Min(0f)] public float knockbackPerShapeSizeAndSpeed = 1.7f;
    [Min(0f)] public float maximumShapeKnockbackSpeed = 14f;
    [Min(0.05f)] public float shapeImpactCooldown = 0.75f;

    public int ActiveFallingShapes => _shapes.Count;
    public int PendingFallingShapes => _shapePending.Count + (_buildingShape != null ? 1 : 0);
    /// <summary>0 seconds means intentionally permanent; otherwise age is since the physics object spawned.</summary>
    public static bool ShouldExpireShape(bool ceiling, float age, float ceilingLifetime, float detachedLifetime)
    {
        float lifetime = ceiling ? ceilingLifetime : detachedLifetime;
        return lifetime > 0f && age >= lifetime;
    }
    public static bool ShouldExpireRestingShape(bool ceiling, float restingAge,
        float ceilingLifetime, float detachedLifetime, float restingLifetime)
    {
        float maximum = ceiling ? ceilingLifetime : detachedLifetime;
        return maximum > 0f && restingLifetime > 0f && restingAge >= restingLifetime;
    }
    [ContextMenu("Log Falling Shape Counters")]
    public void LogFallingShapeCounters()
    {
        int dynamicCount = 0;
        foreach (TerrainShapeChunk piece in _shapes.Values)
            if (piece != null && piece.Body != null && !piece.Body.isKinematic) dynamicCount++;
        Debug.Log($"[Gobmine Shapes] Active={ActiveFallingShapes}, dynamic={dynamicCount}, pending={PendingFallingShapes}, " +
            $"preparing={_preparingMeshes.Count}, networkQueued={_shapeSend.Count}, streamedPaused={_streamingSuspendedShapes.Count}, " +
            $"restingFrozen={_budgetFrozenShapes.Count}, structuralPending={HasPendingStructuralWork}, " +
            $"pendingMeshes={PendingMeshRebuilds}, activeTerrainChunks={ActiveChunkCount}, " +
            $"ceilingLifetime={ceilingShapeLifetime:0.#}, detachedLifetime={detachedShapeLifetime:0.#}, " +
            $"restingLifetime={settledShapeLifetime:0.#}, isServer={isServer}",this);
        int shown = 0;
        foreach (var pair in _shapes)
        {
            if (shown++ >= 12) { Debug.Log("[Gobmine Shapes] Further pieces omitted from this log.",this); break; }
            TerrainShapeChunk piece = pair.Value;
            if (piece == null || piece.Body == null) continue;
            string why;
            if (!_shapeWaitReasons.TryGetValue(pair.Key, out why)) why = "none";
            Debug.Log($"[Gobmine Shape {pair.Key}] ceiling={piece.IsCeiling}, age={Time.time-piece.BornAt:0.0}s, " +
                $"kinematic={piece.Body.isKinematic}, sleeping={piece.Body.IsSleeping()}, " +
                $"velocity={piece.GetVelocity()}, nearPlayer={Mathf.Sqrt(NearestPlayerDistance(piece.transform.position)):0.0}m, " +
                $"streamPaused={_streamingSuspendedShapes.Contains(pair.Key)}, budgetFrozen={_budgetFrozenShapes.Contains(pair.Key)}, " +
                $"colliderCount={piece.Colliders.Count}, reason={why}",piece);
        }
        if (_buildingShape != null) Debug.Log($"[Gobmine Shapes] Building shape {_buildingShape.Id}: " +
            $"{(_buildingMesh != null ? _buildingMesh.BuiltCells : 0)} mesh cells built; " +
            "waiting for removal mesh fence / materialization.",this);
    }
    private readonly Dictionary<int,TerrainShapeChunk> _shapes = new Dictionary<int,TerrainShapeChunk>();
    private readonly Dictionary<int,TerrainShapeRecord> _shapeRecords = new Dictionary<int,TerrainShapeRecord>();
    private readonly Queue<TerrainShapeRecord> _shapePending = new Queue<TerrainShapeRecord>();
    private TerrainShapeRecord _buildingShape;
    private TerrainShapeMeshJob _buildingMesh;
    // Build the visual surface while the existing removal queue clears terrain.
    private readonly Dictionary<int,TerrainShapeMeshJob> _preparingMeshes = new Dictionary<int,TerrainShapeMeshJob>();
    private readonly Queue<int> _preparingOrder = new Queue<int>();
    private readonly Dictionary<int,ShapeDraft> _shapeDrafts = new Dictionary<int,ShapeDraft>();
    private readonly Dictionary<long,float> _shapeHitCooldown = new Dictionary<long,float>();
    private readonly List<int> _shapeRemove = new List<int>();
    private readonly Dictionary<int,float> _shapeSleepingSince = new Dictionary<int,float>();
    private readonly HashSet<int> _streamingSuspendedShapes = new HashSet<int>();
    private readonly HashSet<int> _budgetFrozenShapes = new HashSet<int>();
    // Capture required mesh revisions at removal completion. Later mining must not
    // postpone an earlier detached object's appearance indefinitely.
    private readonly Dictionary<int,Dictionary<Vector3Int,long>> _shapeColliderVersions =
        new Dictionary<int,Dictionary<Vector3Int,long>>();
    private readonly Dictionary<int,string> _shapeWaitReasons = new Dictionary<int,string>();
    private readonly HashSet<Vector3Int> _shapeProbeChunks = new HashSet<Vector3Int>();
    private readonly Queue<ShapeSend> _shapeSend = new Queue<ShapeSend>();
    private readonly HashSet<int> _liveShapeSending = new HashSet<int>();
    private readonly HashSet<int> _removedClientShapes = new HashSet<int>();
    private readonly HashSet<int> _queuedClientShapes = new HashSet<int>();
    private readonly Dictionary<int,float> _nextCollisionReadinessCheck = new Dictionary<int,float>();
    private readonly Dictionary<int,bool> _cachedCollisionReady = new Dictionary<int,bool>();
    private int _nextShapeId = 1;
    private float _nextShapePose;
    private bool _loadedShapesActivated;
    private List<TerrainShapeRecord> _loadedShapeRecords;
    private sealed class ShapeDraft
    {
        public TerrainShapeRecord Record;
        public int Expected;
    }
    private sealed class ShapeSend
    {
        public TerrainShapeRecord Record;
        public int Cursor = -1;
    }

    private TerrainShapeRecord CaptureDetachedShape(List<Vector3Int> grid, bool ceiling)
    {
        if (!preserveDisconnectedShapes || grid == null || grid.Count == 0) return null;
        var record = new TerrainShapeRecord { Id = _nextShapeId++, Ceiling = ceiling };
        Vector3Int lo = new Vector3Int(int.MaxValue,int.MaxValue,int.MaxValue);
        Vector3Int hi = new Vector3Int(int.MinValue,int.MinValue,int.MinValue);
        var distinct = new HashSet<Vector3Int>();
        var solid = new List<Vector3Int>();
        foreach (Vector3Int p in grid)
        {
            float d = _terrain.Get(p);
            if (d >= 0f || !distinct.Add(p)) continue;
            record.Samples.Add(new TerrainShapeSample(p,d)); solid.Add(p);
            lo = Vector3Int.Min(lo,p); hi = Vector3Int.Max(hi,p);
        }
        if (solid.Count == 0) return null;
        // Preserve the original interpolated cut profile. A mesh extracted using
        // arbitrary +1 air densities would alter surface vertices near each cut.
        foreach (Vector3Int p in solid)
            foreach (Vector3Int direction in TerrainStructuralSolver.Directions)
            {
                Vector3Int neighbour = p+direction;
                if (!distinct.Add(neighbour)) continue;
                float d = _terrain.Get(neighbour);
                if (d >= 0f) record.Samples.Add(new TerrainShapeSample(neighbour,d));
            }
        record.Position = ((Vector3)lo + (Vector3)hi)*(voxelSize*0.5f);
        _preparingMeshes[record.Id] = new TerrainShapeMeshJob(record,voxelSize,GetSolidMaterialId);
        _preparingOrder.Enqueue(record.Id);
        return record;
    }

    private List<Vector3Int> GatherCeilingChunkSamples(Queue<List<Vector3Int>> queue)
    {
        var output = queue.Dequeue();
        if (output.Count == 0) return output;
        Vector3Int origin = output[0];
        // Each scanner result is a vertical column. Group a handful of adjacent
        // columns into small, varied roof slabs, NOT one invisible falling patch.
        int variation = (origin.x * 73856093) ^ (origin.z * 19349663);
        int limit = Mathf.Clamp(5 + (variation & 7),1,maximumCeilingColumnsPerChunk);
        for (int i = 1; i < limit && queue.Count > 0; i++)
        {
            List<Vector3Int> next = queue.Peek();
            if (next.Count == 0) { queue.Dequeue(); continue; }
            Vector3Int p = next[0];
            if (Mathf.Abs(p.x-origin.x) > 4 || Mathf.Abs(p.z-origin.z) > 4 ||
                Mathf.Abs(p.y-origin.y) > 3) break;
            output.AddRange(queue.Dequeue());
        }
        return output;
    }

    private void QueueShapeAfterRemoval(TerrainShapeRecord record)
    {
        if (record == null || record.Samples.Count == 0) return;
        CaptureShapeMeshFence(record);
        _shapePending.Enqueue(record);
        // A later mining edit is allowed to dirty the same region again without
        // changing the version needed by THIS shape.
    }

    private void UpdateFallingShapes()
    {
        if (!IsReady || _terrain == null) return;
        if (isServer && !_loadedShapesActivated)
        {
            _loadedShapesActivated = true;
            if (_loadedShapeRecords != null)
            {
                foreach (TerrainShapeRecord record in _loadedShapeRecords)
                {
                    _nextShapeId = Mathf.Max(_nextShapeId,record.Id+1);
                    _shapePending.Enqueue(record);
                }
                _loadedShapeRecords = null;
            }
        }
        if (isServer && _preparingOrder.Count > 0)
        {
            int jobs = Mathf.Min(2,_preparingOrder.Count);
            for (int j=0;j<jobs;j++)
            {
                int id = _preparingOrder.Dequeue();
                TerrainShapeMeshJob job;
                if (!_preparingMeshes.TryGetValue(id,out job)) continue;
                if (!job.Complete) job.Step(Mathf.Max(64,shapeMeshCellsPerFrame/jobs));
                if (!job.Complete) _preparingOrder.Enqueue(id);
            }
        }
        if (_buildingShape == null && _shapePending.Count > 0)
        {
            _buildingShape = _shapePending.Dequeue();
            _queuedClientShapes.Remove(_buildingShape.Id);
            if (_removedClientShapes.Contains(_buildingShape.Id)) { _buildingShape = null; return; }
            if (!_preparingMeshes.TryGetValue(_buildingShape.Id,out _buildingMesh))
                _buildingMesh = new TerrainShapeMeshJob(_buildingShape,voxelSize,GetSolidMaterialId);
        }
        if (_buildingShape != null)
        {
            // Prebuilt concurrently with removal on server; clients build after
            // their reliable shape snapshot completes. Never spawn before collider edits.
            if (!_buildingMesh.Complete) _buildingMesh.Step(Mathf.Max(1,shapeMeshCellsPerFrame));
            if (_buildingMesh.Complete && ShapeTerrainViewsReady(_buildingShape))
            {
                TerrainShapeRecord record = _buildingShape;
                _preparingMeshes.Remove(record.Id);
                TerrainShapeChunk obj = CreateShapeChunk(record,_buildingMesh,isServer);
                if (obj != null && isServer)
                {
                    _shapeRecords[record.Id] = record;
                    _shapeSend.Enqueue(new ShapeSend { Record = record });
                    _liveShapeSending.Add(record.Id);
                }
                _buildingShape = null; _buildingMesh = null;
            }
        }
        if (!isServer) return;
        SendLiveShapePackets();
        if (Time.time >= _nextShapePose)
        {
            _nextShapePose = Time.time + Mathf.Max(0.05f,shapePoseInterval);
            SendShapePoses();
        }
        UpdateShapePhysicsStreaming();
        _shapeRemove.Clear();
        foreach (var pair in _shapes)
        {
            TerrainShapeChunk piece = pair.Value;
            if (piece == null) { _shapeRemove.Add(pair.Key); continue; }
            float restingAt;
            bool resting = piece.Body != null && !piece.Body.isKinematic && piece.Body.IsSleeping();
            if (resting)
            {
                if (!_shapeSleepingSince.TryGetValue(pair.Key,out restingAt))
                { restingAt = Time.time; _shapeSleepingSince[pair.Key] = restingAt; }
            }
            else { _shapeSleepingSince.Remove(pair.Key); restingAt = Time.time; }
            if (ShouldExpireShape(piece.IsCeiling, Time.time - piece.BornAt,
                    ceilingShapeLifetime, detachedShapeLifetime) ||
                (resting && ShouldExpireRestingShape(piece.IsCeiling,Time.time - restingAt,
                    ceilingShapeLifetime,detachedShapeLifetime,settledShapeLifetime)))
                _shapeRemove.Add(pair.Key);
        }
        foreach (int id in _shapeRemove) RemoveFallingShape(id);
        FreezeOlderShapesIfNeeded();
    }

    private void CaptureShapeMeshFence(TerrainShapeRecord record)
    {
        if (_shapeColliderVersions.ContainsKey(record.Id)) return;
        var versions = new Dictionary<Vector3Int,long>();
        foreach (TerrainShapeSample sample in record.Samples)
        {
            if (sample.Density >= 0f) continue;
            foreach (Vector3Int chunk in _terrain.ChunksSharingSample(sample.Grid))
            {
                if (!versions.ContainsKey(chunk)) versions.Add(chunk,_meshFence.Capture(chunk));
            }
        }
        _shapeColliderVersions.Add(record.Id,versions);
    }

    private bool ShapeTerrainViewsReady(TerrainShapeRecord record)
    {
        // The fence is captured AFTER THIS shape's density removal, not during
        // an arbitrary later Update. A continuous mining stream cannot starve it.
        CaptureShapeMeshFence(record);
        foreach (var needed in _shapeColliderVersions[record.Id])
        {
            // Sleeping/unloaded terrain is rebuilt by streaming on demand.
            if (_activeChunks.ContainsKey(needed.Key) && !_meshFence.IsSatisfied(needed.Key,needed.Value))
                return false;
        }
        _shapeColliderVersions.Remove(record.Id);
        return true;
    }

    private TerrainShapeChunk CreateShapeChunk(TerrainShapeRecord record,TerrainShapeMeshJob mesh,bool server)
    {
        if (mesh.SampleCount == 0) return null;
        var obj = new GameObject("Gobmine_FallingShape");
        var shape = obj.AddComponent<TerrainShapeChunk>();
        shape.Configure(record,mesh,this,server,shapeCollisionBoxes,shapeMaterialDensityKgPerM3);
        _shapes[record.Id] = shape;
        if (server && record.Samples.Count > 8192)
            Debug.LogWarning("[Gobmine Shapes] Large shape ("+record.Samples.Count+
                " samples) is preserved. Its first mesh build and replication may take multiple frames.");
        return shape;
    }

    private void UpdateShapePhysicsStreaming()
    {
        // Streaming can still pause pieces when their collision world is unavailable.
        // Unlike the previous version, "no registered player within 20m" is NOT
        // proof that a piece lacks a floor. Ready colliders allow it to fall.
        foreach (var pair in _shapes)
        {
            TerrainShapeChunk piece = pair.Value;
            if (piece == null || piece.Body == null || _budgetFrozenShapes.Contains(pair.Key)) continue;
            float checkAt;
            bool ready;
            string reason;
            if (_nextCollisionReadinessCheck.TryGetValue(pair.Key,out checkAt) && Time.time < checkAt)
            {
                if (!_cachedCollisionReady.TryGetValue(pair.Key,out ready)) ready = false;
            }
            else
            {
                ready = ShapePhysicsCollisionReady(piece,out reason);
                if (ready) _shapeWaitReasons.Remove(pair.Key);
                else _shapeWaitReasons[pair.Key] = reason;
                _cachedCollisionReady[pair.Key] = ready;
                _nextCollisionReadinessCheck[pair.Key] = Time.time + (piece.Body.IsSleeping() ? 0.35f : 0.12f);
            }
            if (!ready && !piece.Body.isKinematic)
            {
                TerrainShapeRecord record;
                if (_shapeRecords.TryGetValue(pair.Key,out record))
                { record.Velocity = piece.GetVelocity(); record.AngularVelocity = piece.Body.angularVelocity; }
                DebrisPool.SetLinearVelocity(piece.Body,Vector3.zero);
                piece.Body.angularVelocity = Vector3.zero;
                piece.Body.isKinematic = true;
                _streamingSuspendedShapes.Add(pair.Key);
            }
            else if (ready && _streamingSuspendedShapes.Remove(pair.Key))
            {
                TerrainShapeRecord record;
                piece.Body.isKinematic = false;
                if (_shapeRecords.TryGetValue(pair.Key,out record))
                { DebrisPool.SetLinearVelocity(piece.Body,record.Velocity); piece.Body.angularVelocity = record.AngularVelocity; }
                piece.Body.WakeUp();
            }
        }
    }

    private bool ShapePhysicsCollisionReady(TerrainShapeChunk piece, out string reason)
    {
        reason = null;
        if (piece.Colliders.Count == 0) { reason = "no collision boxes generated"; return false; }
        // Probe just below the shape so we don't simulate against unloaded floors.
        // Avoid the old strict 20m gate: loaded terrain colliders are sufficient
        // even when a player registration is delayed by a Mirror scene transition.
        Bounds bounds = piece.Colliders[0].bounds;
        foreach (BoxCollider box in piece.Colliders) if (box != null) bounds.Encapsulate(box.bounds);
        float y = bounds.min.y - 0.7f;
        float rx = Mathf.Min(bounds.extents.x * 0.7f,3f), rz = Mathf.Min(bounds.extents.z * 0.7f,3f);
        bool nearPlayer = NearestPlayerDistance(piece.transform.position) <= 30f * 30f;
        _shapeProbeChunks.Clear();
        for (int k=0;k<5;k++)
        {
            Vector3 p = new Vector3(bounds.center.x + (k==1 || k==3 ? rx : k==2 || k==4 ? -rx : 0f),
                y,bounds.center.z + (k==1 || k==4 ? rz : k==2 || k==3 ? -rz : 0f));
            Vector3Int coord = GetChunkCoordFromPosition(p);
            // The world generator intentionally creates no terrain above maxYChunk.
            // Do not ask the PLAYER collision guard to construct forbidden chunks,
            // or a roof near the top of the world will remain kinematic forever.
            if (coord.y > maxYChunk || !_shapeProbeChunks.Add(coord)) continue;
            Chunk active;
            if (_activeChunks.TryGetValue(coord,out active) && active != null && active.CollisionReady)
                continue;
            if (!nearPlayer)
            {
                reason = "required collision chunk " + coord + " not active; no nearby player";
                return false;
            }
            string waiting;
            if (!TryPreparePlayerCollision(new Bounds(p,Vector3.one * Mathf.Max(0.2f,voxelSize)),out waiting))
            {
                reason = waiting ?? ("collision chunk " + coord + " not ready");
                return false;
            }
        }
        return true;
    }

    private void FreezeOlderShapesIfNeeded()
    {
        int active = 0;
        foreach (var shape in _shapes.Values)
            if (shape != null && shape.Body != null && !shape.Body.isKinematic) active++;
        int limit = Mathf.Max(4,maximumDynamicShapeBodies);
        if (active <= limit) return;
        var candidates = new List<TerrainShapeChunk>(_shapes.Values);
        candidates.Sort((a,b) => a == null ? (b == null ? 0 : 1) :
            b == null ? -1 : a.BornAt.CompareTo(b.BornAt));
        foreach (TerrainShapeChunk piece in candidates)
        {
            if (active <= limit) break;
            if (piece == null || piece.Body == null || piece.Body.isKinematic || !piece.Body.IsSleeping()) continue;
            TerrainShapeRecord rec;
            if (_shapeRecords.TryGetValue(piece.ShapeId,out rec))
            { rec.Position = piece.transform.position; rec.Rotation = piece.transform.rotation;
                rec.Velocity = Vector3.zero; rec.AngularVelocity = Vector3.zero; }
            DebrisPool.SetLinearVelocity(piece.Body,Vector3.zero);
            piece.Body.angularVelocity = Vector3.zero;
            piece.Body.isKinematic = true; // Keep sculpted shape and colliders; only freeze physics.
            _budgetFrozenShapes.Add(piece.ShapeId);
            active--;
        }
    }

    private void RemoveFallingShape(int id)
    {
        TerrainShapeChunk obj;
        if (_shapes.TryGetValue(id,out obj))
        {
            _shapes.Remove(id);
            if (obj != null) Destroy(obj.gameObject);
        }
        _shapeRecords.Remove(id); _shapeColliderVersions.Remove(id);
        _shapeWaitReasons.Remove(id); _shapeSleepingSince.Remove(id);
        var oldHits = new List<long>();
        foreach (long key in _shapeHitCooldown.Keys) if ((int)(key >> 32) == id) oldHits.Add(key);
        foreach (long key in oldHits) _shapeHitCooldown.Remove(key);
        _budgetFrozenShapes.Remove(id); _streamingSuspendedShapes.Remove(id);
        _nextCollisionReadinessCheck.Remove(id); _cachedCollisionReady.Remove(id);
        if (isServer) RpcRemoveShape(id);
    }

    private void SendLiveShapePackets()
    {
        int allowance = Mathf.Max(1,shapePacketsPerFrame);
        while (allowance-- > 0 && _shapeSend.Count > 0)
        {
            ShapeSend send = _shapeSend.Peek();
            TerrainShapeRecord r = send.Record;
            if (!_shapes.ContainsKey(r.Id))
            { _shapeSend.Dequeue(); _liveShapeSending.Remove(r.Id); continue; }
            if (send.Cursor < 0)
            {
                RpcBeginShape(r.Id,r.Ceiling,r.Position,r.Rotation,r.Samples.Count);
                send.Cursor = 0;
            }
            else if (send.Cursor < r.Samples.Count)
            {
                GetSamplePart(r,send.Cursor,out Vector3Int[] coords,out float[] density);
                RpcShapePart(r.Id,coords,density);
                send.Cursor += coords.Length;
            }
            else
            {
                RpcEndShape(r.Id); _shapeSend.Dequeue(); _liveShapeSending.Remove(r.Id);
            }
        }
    }
    private static void GetSamplePart(TerrainShapeRecord record,int start,out Vector3Int[] coords,out float[] density)
    {
        int length = Mathf.Min(48,record.Samples.Count-start);
        coords = new Vector3Int[length]; density = new float[length];
        for (int i = 0; i < length; i++) { TerrainShapeSample s = record.Samples[start+i];
            coords[i] = s.Grid; density[i] = s.Density; }
    }
    [ClientRpc]
    private void RpcBeginShape(int id,bool ceiling,Vector3 position,Quaternion rotation,int count)
    { if (!isServer) BeginReceivingShape(id,ceiling,position,rotation,count); }
    [ClientRpc]
    private void RpcShapePart(int id,Vector3Int[] coords,float[] density)
    { if (!isServer) ReceiveShapeSamples(id,coords,density); }
    [ClientRpc]
    private void RpcEndShape(int id) { if (!isServer) EndReceivingShape(id); }
    [ClientRpc]
    private void RpcRemoveShape(int id)
    {
        if (isServer) return;
        _removedClientShapes.Add(id);
        _shapeDrafts.Remove(id);
        TerrainShapeChunk shape;
        if (_shapes.TryGetValue(id,out shape)) { _shapes.Remove(id); if (shape != null) Destroy(shape.gameObject); }
    }
    [ClientRpc(channel = Channels.Unreliable)]
    private void RpcShapePoses(int[] ids,Vector3[] positions,Quaternion[] rotations)
    {
        if (isServer) return;
        for (int i = 0; i < ids.Length; i++)
        {
            TerrainShapeChunk shape;
            if (_shapes.TryGetValue(ids[i],out shape) && shape != null) shape.SetPose(positions[i],rotations[i]);
        }
    }
    private void SendShapePoses()
    {
        // Keep unreliable messages well below a typical transport packet.
        var ids = new List<int>(20); var positions = new List<Vector3>(20); var rotations = new List<Quaternion>(20);
        foreach (var item in _shapes)
        {
            if (item.Value == null) continue;
            ids.Add(item.Key); positions.Add(item.Value.transform.position); rotations.Add(item.Value.transform.rotation);
            if (ids.Count == 20) { RpcShapePoses(ids.ToArray(),positions.ToArray(),rotations.ToArray());
                ids.Clear(); positions.Clear(); rotations.Clear(); }
        }
        if (ids.Count > 0) RpcShapePoses(ids.ToArray(),positions.ToArray(),rotations.ToArray());
    }
    private void BeginReceivingShape(int id,bool ceiling,Vector3 position,Quaternion rotation,int count)
    {
        if (_removedClientShapes.Contains(id)) return;
        if (count <= 0 || count > 2000000) { Debug.LogError("[Gobmine Shapes] Invalid shape size"); return; }
        // Duplicate live/snapshot streams are idempotent, but do not discard an
        // already-constructed object on a repeated reliable begin message.
        if (_shapes.ContainsKey(id) || _shapeDrafts.ContainsKey(id) || _queuedClientShapes.Contains(id) ||
            (_buildingShape != null && _buildingShape.Id == id)) return;
        _shapeDrafts.Add(id,new ShapeDraft { Record = new TerrainShapeRecord { Id=id, Ceiling=ceiling,
            Position=position,Rotation=rotation }, Expected = count });
    }
    private void ReceiveShapeSamples(int id,Vector3Int[] coords,float[] densities)
    {
        ShapeDraft draft;
        if (!_shapeDrafts.TryGetValue(id,out draft) || coords == null || densities == null || coords.Length != densities.Length) return;
        for (int i = 0; i < coords.Length; i++)
        {
            if (draft.Record.Samples.Count >= draft.Expected) return;
            draft.Record.Samples.Add(new TerrainShapeSample(coords[i],densities[i]));
        }
    }
    private void EndReceivingShape(int id)
    {
        ShapeDraft draft;
        if (!_shapeDrafts.TryGetValue(id,out draft)) return;
        _shapeDrafts.Remove(id);
        if (draft.Record.Samples.Count != draft.Expected)
        {
            Debug.LogError("[Gobmine Shapes] Incomplete reliable shape snapshot for " + id +
                "; received " + draft.Record.Samples.Count + " of " + draft.Expected);
            return;
        }
        if (!_shapes.ContainsKey(id) && !_removedClientShapes.Contains(id) && _queuedClientShapes.Add(id))
            _shapePending.Enqueue(draft.Record);
    }
    private IEnumerator SendShapesForLateJoin(NetworkConnectionToClient target)
    {
        // Called inside World.SendTerrainSnapshot, after the terrain/fluid baseline.
        var records = new List<TerrainShapeRecord>(_shapeRecords.Values);
        foreach (TerrainShapeRecord r in records)
        {
            // A new observer can join in the middle of the live Begin/Part/End stream.
            // Never skip the piece: wait for that stream to finish, then send a full
            // dedicated snapshot. Client Begin is idempotent if the live stream won.
            while (_liveShapeSending.Contains(r.Id))
            {
                if (!NetworkServer.connections.ContainsKey(target.connectionId)) yield break;
                yield return null;
            }
            TerrainShapeChunk shape;
            if (!_shapes.TryGetValue(r.Id,out shape) || shape == null) continue;
            Vector3 pos = shape.transform.position; Quaternion rot = shape.transform.rotation;
            TargetBeginShape(target,r.Id,r.Ceiling,pos,rot,r.Samples.Count);
            for (int offset = 0; offset < r.Samples.Count; offset += 48)
            {
                if (!NetworkServer.connections.ContainsKey(target.connectionId)) yield break;
                GetSamplePart(r,offset,out Vector3Int[] coords,out float[] density);
                TargetShapePart(target,r.Id,coords,density);
                yield return null;
            }
            TargetEndShape(target,r.Id);
        }
    }
    [TargetRpc]
    private void TargetBeginShape(NetworkConnectionToClient target,int id,bool ceiling,Vector3 pos,Quaternion rot,int count)
    { if (!isServer) BeginReceivingShape(id,ceiling,pos,rot,count); }
    [TargetRpc]
    private void TargetShapePart(NetworkConnectionToClient target,int id,Vector3Int[] coords,float[] density)
    { if (!isServer) ReceiveShapeSamples(id,coords,density); }
    [TargetRpc]
    private void TargetEndShape(NetworkConnectionToClient target,int id)
    { if (!isServer) EndReceivingShape(id); }

    // Real moving-body contact instead of the old invisible falling-column damage.
    // The client-controlled CharacterController receives a velocity impulse via an
    // owner-only TargetRpc, and uses its normal collision-constrained Move path.
    public void ServerTickShapeCollision(TerrainShapeChunk piece,Vector3 velocity)
    {
        if (!isServer || !collapseDamagesPlayers || piece == null || piece.Body == null || piece.Body.isKinematic) return;
        float speed = velocity.magnitude;
        if (speed < minimumShapeImpactSpeed || _connectedPlayers.Count == 0) return;
        foreach (Transform player in _connectedPlayers)
        {
            if (player == null || (player.position-piece.transform.position).sqrMagnitude > 1600f) continue;
            GobminePlayerHealth health = player.GetComponent<GobminePlayerHealth>();
            if (health == null || health.IsDead) continue;
            Collider playerBody = player.GetComponent<Collider>();
            if (playerBody == null || !playerBody.enabled) continue;
            long hitKey = ((long)piece.ShapeId << 32) | health.netIdentity.netId;
            float next;
            if (_shapeHitCooldown.TryGetValue(hitKey,out next) && Time.time < next) continue;
            bool touched = false;
            Vector3 movement = velocity * Time.fixedDeltaTime;
            foreach (BoxCollider box in piece.Colliders)
            {
                if (box == null) continue;
                Bounds swept = box.bounds;
                swept.Encapsulate(new Bounds(box.bounds.center - movement,box.bounds.size));
                if (!swept.Intersects(playerBody.bounds)) continue;
                touched = true; break;
            }
            if (!touched) continue;
            float damage = TerrainShapeImpactRules.Damage(piece.Volume,speed,minimumShapeImpactSpeed,
                damagePerShapeSizeAndSpeed,maximumShapeImpactDamage);
            if (!health.ServerApplyCollapseDamage(damage,shapeImpactCooldown)) continue;
            _shapeHitCooldown[hitKey] = Time.time + shapeImpactCooldown;
            CollapseDamageHits++;
            var movementController = player.GetComponent<PlayerMovement>();
            if (movementController != null && movementController.connectionToClient != null)
            {
                Vector3 direction = playerBody.bounds.center - piece.transform.position;
                direction.y = 0f;
                if (direction.sqrMagnitude < 0.0001f)
                    direction = new Vector3(velocity.x,0f,velocity.z);
                if (direction.sqrMagnitude < 0.0001f) direction = Vector3.forward;
                direction.Normalize();
                float knock = TerrainShapeImpactRules.Knockback(piece.Volume,speed,minimumShapeImpactSpeed,
                    knockbackPerShapeSizeAndSpeed,maximumShapeKnockbackSpeed);
                movementController.TargetReceiveChunkKnockback(movementController.connectionToClient,
                    direction * knock + Vector3.up * Mathf.Min(knock * 0.32f,4f));
            }
        }
    }

    private List<TerrainShapeRecord> SnapshotFallingShapesForSave()
    {
        var result = new List<TerrainShapeRecord>();
        // Positive lifetime = temporary physical effect. Do not resurrect a
        // transient falling piece when loading a saved world next session.
        if (detachedShapeLifetime <= 0f)
        {
            foreach (var entry in _shapeRecords)
            {
                TerrainShapeChunk shape;
                if (!_shapes.TryGetValue(entry.Key,out shape) || shape == null || entry.Value.Ceiling) continue;
                TerrainShapeRecord rec = entry.Value;
                rec.Position = shape.transform.position; rec.Rotation = shape.transform.rotation;
                if (!_streamingSuspendedShapes.Contains(rec.Id) && !_budgetFrozenShapes.Contains(rec.Id))
                { rec.Velocity = shape.GetVelocity(); rec.AngularVelocity = shape.Body.angularVelocity; }
                result.Add(rec);
            }
        }
        // Capture interrupted island removals as records as well. Load makes the
        // entire recorded mask air, preventing a duplicate terrain/chunk overlap.
        if (_collapseWork != null && _collapseWork.Shape != null && !_collapseWork.Shape.Ceiling)
            result.Add(_collapseWork.Shape);
        if (_buildingShape != null && !_buildingShape.Ceiling) result.Add(_buildingShape);
        foreach (var pending in _shapePending) if (!pending.Ceiling) result.Add(pending);
        return result;
    }
    private void LoadFallingShapeRecords(List<TerrainShapeRecord> records)
    {
        // Old permanent sculptures are treated as expired in temporary mode,
        // but their already-excavated terrain remains excavated.
        _loadedShapeRecords = detachedShapeLifetime <= 0f ? records : null;
        if (records == null) return;
        foreach (TerrainShapeRecord rec in records)
        {
            _nextShapeId = Mathf.Max(_nextShapeId,rec.Id+1);
            // V3 save may have been taken during an in-progress density removal.
            foreach (TerrainShapeSample sample in rec.Samples)
                if (sample.Density < 0f && _terrain.Get(sample.Grid) < 0f) _terrain.Set(sample.Grid,1f);
        }
    }
    private void ResetFallingShapes()
    {
        foreach (var shape in _shapes.Values) if (shape != null) Destroy(shape.gameObject);
        _shapes.Clear(); _shapeRecords.Clear(); _shapePending.Clear(); _shapeDrafts.Clear();
        _shapeSend.Clear(); _liveShapeSending.Clear(); _removedClientShapes.Clear(); _queuedClientShapes.Clear();
        _nextCollisionReadinessCheck.Clear(); _cachedCollisionReady.Clear(); _shapeHitCooldown.Clear(); _shapeRemove.Clear();
        _shapeSleepingSince.Clear();
        _shapeColliderVersions.Clear(); _shapeWaitReasons.Clear(); _shapeProbeChunks.Clear();
        _preparingMeshes.Clear(); _preparingOrder.Clear();
        _budgetFrozenShapes.Clear(); _streamingSuspendedShapes.Clear();
        _buildingShape = null; _buildingMesh = null; _loadedShapeRecords = null;
        _loadedShapesActivated = false; _nextShapeId = 1; _nextShapePose = 0f;
    }
}
