using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Mirror;
using Random = UnityEngine.Random;

[RequireComponent(typeof(NetworkIdentity))]
public partial class World : NetworkBehaviour
{
    public static World Instance { get; private set; }

    [Header("World Settings")]
    [SerializeField] public int customSeed = 0;
    [SyncVar] public int seed;
    public float noiseScale = 0.05f; // Retained for scene compatibility; original room generator does not use it.
    [SerializeField] private Material voxelMaterial;
    public Vector3Int chunkSize = new Vector3Int(16, 16, 16);
    public float voxelSize = 0.25f;
    [Tooltip("Layer assigned to generated terrain. Match the miner's Terrain Layer mask.")]
    [UnityEngine.Range(0, 31)] public int terrainLayer = 0;

    [Header("Infinite Loading")]
    [Min(1)] public int viewDistanceInChunks = 6;
    [Min(1)] public int sleepDistanceInChunks = 12;
    public int maxYChunk = 4;
    [Min(1)] public int chunkBuildsPerFrame = 2;

    [Header("Structural Rules")]
    // Retained for old scenes and Editor tools. New roof rule uses metres below.
    [HideInInspector] public int minCeilingThickness = 2;
    [HideInInspector] public int maxCollapseVolume = 150000; // Legacy field; NEVER used as a support/size cutoff.
    [Min(1)] public int structuralOperationsPerFrame = 4096;
    [Min(0.1f)] public float structuralMillisecondsPerFrame = 3f;
    [Tooltip("Maximum samples per frame, shared fairly between direct mining and collapse. Time budgets can stop sooner.")]
    [UnityEngine.Range(1, 8192)] public int densityChangesPerFrame = 1024;
    [Min(1)] public int meshRebuildsPerFrame = 4;

    [Header("Performance budgets")]
    [Tooltip("Soft limit checked between atomic removal batches. One small collapse batch is allowed to ensure progress.")]
    [Min(0.1f)] public float removalMillisecondsPerFrame = 3f;
    [Tooltip("Atomic batch size. Large values increase the worst-case frame spike.")]
    [UnityEngine.Range(16, 128)] public int removalBatchSize = 64;
    [Tooltip("Soft limit checked between complete mesh/collider rebuilds. One rebuild can exceed this value.")]
    [Min(0.1f)] public float meshMillisecondsPerFrame = 3f;
    [Tooltip("Reject additional, not-yet-applied mining requests when a player's pending queue is full. No terrain or rewards are changed for rejected requests.")]
    [UnityEngine.Range(1, 32)] public int maxQueuedMiningRequestsPerPlayer = 4;

    [Header("Mining Validation")]
    public bool requireExploringToMine = true;
    [Min(0.01f)] public float maxMineRadius = 3f;
    [Min(0.01f)] public float maxMineAmount = 100f;
    [Min(0.1f)] public float maxMineReach = 15f;

    [Header("Debris")]
    [Tooltip("Material in each spatial bucket is combined into rubble. This is NOT exact mesh fracture.")]
    [UnityEngine.Range(1, 4)] public int debrisBucketSizeInCells = 2;
    [Min(1)] public int debrisSpawnsPerFrame = 4;
    [Tooltip("Briefly combine adjacent removal batches before emitting rubble. Inventory rewards do not wait for this.")]
    [UnityEngine.Range(0f, 0.25f)] public float debrisBatchDelaySeconds = 0.06f;

    public bool IsReady { get; private set; }
    public bool CanMine => IsReady && (!requireExploringToMine || GameManager.Instance == null ||
        GameManager.Instance.currentGameState == GameState.Exploring);
    public int PendingDebrisBuckets => _openRubble.Count + _readyMiningRubble.Count + _readyCollapseRubble.Count;
    public bool HasPendingStructuralWork => _solver != null || _structuralChanges.Count > 0 ||
        _collapseQueue.Count > 0 || _collapseWork != null || _ceilingWork != null || _ceilingQueue.Count > 0 ||
        (_ceilingScanner != null && _ceilingScanner.HasPending);
    public int PendingMiningRequests => _edits.Count + (_miningWork != null ? 1 : 0);
    public int PendingMeshRebuilds => _dirtyMeshes.Count;
    public int ActiveChunkCount => _activeChunks.Count;
    public int PendingChunkBuilds => _chunksToGenerate.Count;
    public int RejectedMiningRequests { get; private set; }
    public float LastRemovalMilliseconds { get; private set; }
    public float LastMeshMilliseconds { get; private set; }

    private TerrainDensityStore _terrain;
    private Func<Vector3Int, float> _densityReader;
    private readonly Dictionary<Vector3Int, Chunk> _activeChunks = new Dictionary<Vector3Int, Chunk>();
    private readonly Dictionary<Vector3Int, Chunk> _sleepingChunks = new Dictionary<Vector3Int, Chunk>();
    private readonly Queue<Chunk> _chunkPool = new Queue<Chunk>();
    private readonly Queue<Vector3Int> _chunksToGenerate = new Queue<Vector3Int>();
    private readonly HashSet<Vector3Int> _generationQueued = new HashSet<Vector3Int>();
    private readonly List<Transform> _connectedPlayers = new List<Transform>();
    private readonly HashSet<Vector3Int> _dirtyMeshes = new HashSet<Vector3Int>();
    private readonly Queue<Vector3Int> _dirtyMeshOrder = new Queue<Vector3Int>();
    private readonly HashSet<Vector3Int> _structuralChanges = new HashSet<Vector3Int>();
    private readonly Queue<List<Vector3Int>> _collapseQueue = new Queue<List<Vector3Int>>();
    private TerrainStructuralSolver _solver;
    private readonly TerrainVolume _volume = new TerrainVolume();
    private readonly Queue<PendingEdit> _edits = new Queue<PendingEdit>();
    private Coroutine _chunkRoutine;
    private RemovalWork _miningWork, _collapseWork;
    private bool _singleSampleCollapseTurn;
    private readonly List<DensityChange> _commitChanges = new List<DensityChange>(128);
    private readonly HashSet<Vector3Int> _commitCells = new HashSet<Vector3Int>();
    private readonly Dictionary<Vector3Int, TerrainVolume.Measure> _beforeCellMeasures = new Dictionary<Vector3Int, TerrainVolume.Measure>();
    private readonly System.Diagnostics.Stopwatch _removalWatch = new System.Diagnostics.Stopwatch();
    private readonly System.Diagnostics.Stopwatch _meshWatch = new System.Diagnostics.Stopwatch();
    private readonly System.Diagnostics.Stopwatch _structureWatch = new System.Diagnostics.Stopwatch();
    private static readonly Unity.Profiling.ProfilerMarker RemovalMarker = new Unity.Profiling.ProfilerMarker("Gobmine.Removal");
    private static readonly Unity.Profiling.ProfilerMarker StructureMarker = new Unity.Profiling.ProfilerMarker("Gobmine.Structure");
    private static readonly Unity.Profiling.ProfilerMarker MeshMarker = new Unity.Profiling.ProfilerMarker("Gobmine.MeshAndCollider");
    private static readonly Unity.Profiling.ProfilerMarker DebrisMarker = new Unity.Profiling.ProfilerMarker("Gobmine.DebrisSpawn");
    private static readonly Unity.Profiling.ProfilerMarker ResourcesMarker = new Unity.Profiling.ProfilerMarker("Gobmine.ResourcesAndSave");
    private static readonly Unity.Profiling.ProfilerMarker StreamingMarker = new Unity.Profiling.ProfilerMarker("Gobmine.ChunkStreaming");
    private readonly List<Vector3Int> _lastStreamingCenters = new List<Vector3Int>();
    private int _lastStreamingView = -1, _lastStreamingSleep = -1, _lastStreamingTop;
    private long _revision;
    private bool _savedOnStop;
    private bool _warnedDebris;
    private int _collapseEffectCount;
    private Vector3 _collapseEffectMoment;
    private float _nextCollapseEffectTime;

    private struct PendingEdit { public Vector3 Point; public float Radius, Amount; public PlayerInventory Miner; }
    private struct DensityChange { public Vector3Int Position; public float Value; }
    private sealed class RemovalWork
    {
        public List<DensityChange> Changes;
        public List<Vector3Int> CollapseSamples;
        public int Count => IsCollapse ? CollapseSamples.Count : Changes.Count;
        public int Cursor;
        public bool IsCollapse;
        public PlayerInventory Miner;
    }
    // Reliable, revisioned late-join synchronization. Live deltas are retained while
    // a frozen baseline is streamed, then replayed after that baseline.
    private struct Delta { public long Revision; public Vector3Int[] Positions; public float[] Values; }
    private readonly List<Delta> _bufferedDeltas = new List<Delta>();
    private readonly HashSet<int> _syncingConnections = new HashSet<int>();
    private bool _receivingSnapshot;
    private bool _snapshotRequested;
    private long _snapshotRevision;
    private long _lastClientRevision;

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    public void RegisterPlayer(Transform player)
    {
        if (player != null && !_connectedPlayers.Contains(player)) _connectedPlayers.Add(player);
        if (player != null) RegisterInventory(player.GetComponent<PlayerInventory>());
    }

    public void UnregisterPlayer(Transform player) => _connectedPlayers.Remove(player);

    public override void OnStartServer()
    {
        base.OnStartServer();
        seed = customSeed == 0 ? Random.Range(1000, 99999) : customSeed;
        ResetRuntimeState();
        if (!InitializeMaterialWorld()) return;
        foreach (Transform player in _connectedPlayers)
            if (player != null) RegisterInventory(player.GetComponent<PlayerInventory>());
        InitializeCeilingScanner();
        foreach (var entry in _terrain.CopyOverrides())
        {
            if (entry.Value >= 0f) _structuralChanges.Add(entry.Key);
            NotifyCeilingChange(entry.Key, false);
        }
        _savedOnStop = false;
        StartWorldGeneration(); // A dedicated server also needs its own colliders.
    }

    public override void OnStartClient()
    {
        base.OnStartClient();
        if (DebrisPool.Instance != null) DebrisPool.Instance.EnsureClientPoolRegistered();
        if (resourcePickupPrefab == null) resourcePickupPrefab = Resources.Load<GameObject>("Gobmine/OrePickup");
        if (resourcePickupPrefab != null)
        {
            uint id = resourcePickupPrefab.GetComponent<NetworkIdentity>().assetId;
            if (!NetworkClient.prefabs.ContainsKey(id)) NetworkClient.RegisterPrefab(resourcePickupPrefab);
        }
        if (!isServer) RequestSnapshot();
    }

    public override void OnStopServer()
    {
        SaveCurrentWorld(); // Synchronous at shutdown: do not lose the save to process exit.
        ResetRuntimeState();
        base.OnStopServer();
    }

    public override void OnStopClient()
    {
        if (!isServer) ResetRuntimeState();
        base.OnStopClient();
    }

    private void ResetRuntimeState()
    {
        ResetMaterialRuntime();
        StopAllCoroutines();
        _chunkRoutine = null;
        foreach (var entry in _activeChunks) if (entry.Value != null) Destroy(entry.Value.gameObject);
        foreach (var entry in _sleepingChunks) if (entry.Value != null) Destroy(entry.Value.gameObject);
        while (_chunkPool.Count > 0) { Chunk chunk = _chunkPool.Dequeue(); if (chunk != null) Destroy(chunk.gameObject); }
        _activeChunks.Clear(); _sleepingChunks.Clear();
        _chunksToGenerate.Clear(); _generationQueued.Clear(); _dirtyMeshes.Clear(); _dirtyMeshOrder.Clear();
        _structuralChanges.Clear(); _collapseQueue.Clear(); _edits.Clear();
        ResetRubbleRuntime(); ResetCaveInRuntime(); _bufferedDeltas.Clear();
        _syncingConnections.Clear();
        _miningWork = null; _collapseWork = null; _solver = null; _terrain = null; _densityReader = null;
        _singleSampleCollapseTurn = false;
        _commitChanges.Clear(); _commitCells.Clear(); _beforeCellMeasures.Clear();
        _lastStreamingCenters.Clear(); _lastStreamingView = -1; _lastStreamingSleep = -1;
        RejectedMiningRequests = 0; LastRemovalMilliseconds = 0f; LastMeshMilliseconds = 0f;
        _revision = 0; _lastClientRevision = 0;
        _receivingSnapshot = false; _snapshotRequested = false;
        _warnedDebris = false;
        _collapseEffectCount = 0; _collapseEffectMoment = Vector3.zero; _nextCollapseEffectTime = 0f;
        IsReady = false;
    }

    private void OnApplicationQuit()
    {
        if (isServer) SaveCurrentWorld();
    }

    private void SaveCurrentWorld()
    {
        if (_savedOnStop || _terrain == null) return;
        _savedOnStop = SaveMaterialWorld();
    }

    private void Update()
    {
        if (_terrain == null) return;
        if (isServer && IsReady)
        {
            // Accepted edits and cascades finish even if the game leaves Exploring.
            // The game-state gate applies when accepting NEW mining requests only.
            using (RemovalMarker.Auto()) ProcessRemovalWork();
            using (StructureMarker.Auto()) RunStructuralBudget();
            EmitCollapseEffects();
        }
        using (MeshMarker.Auto()) FlushDirtyMeshes();
        // Each rubble bucket waits only for its OWN collision views, not for every
        // dirty chunk in a busy multiplayer world. Distant buckets cannot block it.
        if (isServer)
        {
            UpdateCollapseDamage();
            using (DebrisMarker.Auto()) FlushDebris();
            using (ResourcesMarker.Auto()) UpdateMaterialWorld();
        }
    }

    // Trusted server-side compatibility API. Clients must use an owned mining tool,
    // which sends an aim direction, not arbitrary terrain positions or item quantities.
    public void RequestModifyTerrain(Vector3 hitPoint, float radius, float amount)
    {
        if (isServer && CanMine) QueueMining(hitPoint, radius, amount, null);
    }

    [Server]
    public void QueuePlayerMining(PlayerInventory miner, Vector3 hitPoint, float radius, float amount)
    {
        if (!CanMine || miner == null || !miner.isServer ||
            Vector3.Distance(miner.transform.position, hitPoint) > maxMineReach) return;
        QueueMining(hitPoint, radius, amount, miner);
    }

    private void QueueMining(Vector3 point, float radius, float amount, PlayerInventory miner)
    {
        if (!Finite(point) || !Finite(radius) || !Finite(amount) || radius <= 0f || amount <= 0f) return;
        if (_edits.Count >= 256) { RejectedMiningRequests++; return; }
        int queuedForMiner = _miningWork != null && _miningWork.Miner == miner ? 1 : 0;
        foreach (PendingEdit pending in _edits) if (pending.Miner == miner) queuedForMiner++;
        if (queuedForMiner >= Mathf.Max(1, maxQueuedMiningRequestsPerPlayer))
        { RejectedMiningRequests++; return; }
        _edits.Enqueue(new PendingEdit {
            Point = point, Radius = Mathf.Min(radius, maxMineRadius), Amount = Mathf.Min(amount, maxMineAmount), Miner = miner
        });
    }

    private static bool Finite(float f) => !float.IsNaN(f) && !float.IsInfinity(f);
    private static bool Finite(Vector3 p) => Finite(p.x) && Finite(p.y) && Finite(p.z);

    private List<DensityChange> BuildBrush(PendingEdit edit)
    {
        var changes = new List<DensityChange>();
        Vector3 low = (edit.Point - Vector3.one * edit.Radius) / voxelSize;
        Vector3 high = (edit.Point + Vector3.one * edit.Radius) / voxelSize;
        Vector3Int lo = Vector3Int.FloorToInt(low), hi = Vector3Int.CeilToInt(high);
        for (int x = lo.x; x <= hi.x; x++)
            for (int y = lo.y; y <= hi.y; y++)
                for (int z = lo.z; z <= hi.z; z++)
                {
                    Vector3Int p = new Vector3Int(x, y, z);
                    float old = _terrain.Get(p);
                    if (old >= 0f) continue; // Hitting empty air cannot mint more debris.
                    float distance = Vector3.Distance((Vector3)p * voxelSize, edit.Point);
                    if (distance >= edit.Radius) continue;
                    float value = Mathf.Min(1f, old + (edit.Amount / HardnessAtSample(p)) * (1f - distance / edit.Radius));
                    if (value > old) changes.Add(new DensityChange { Position = p, Value = value });
                }
        return changes;
    }

    // Split count allowance without letting a huge collapse monopolize direct mining.
    // The one-sample setting alternates frames when both lanes are busy.
    private static Vector2Int SplitRemovalBudget(int total, bool mining, bool collapse, bool collapseTurn)
    {
        total = Mathf.Clamp(total, 1, 8192);
        if (!mining) return new Vector2Int(0, collapse ? total : 0);
        if (!collapse) return new Vector2Int(total, 0);
        if (total == 1) return collapseTurn ? new Vector2Int(0, 1) : new Vector2Int(1, 0);
        int reserve = Mathf.Max(1, total / 4);
        return new Vector2Int(total - reserve, reserve);
    }

    private void ProcessRemovalWork()
    {
        _removalWatch.Restart();
        bool mining = _miningWork != null || _edits.Count > 0;
        bool collapse = _collapseWork != null || _collapseQueue.Count > 0 || _ceilingWork != null || _ceilingQueue.Count > 0 ||
            (_ceilingScanner != null && _ceilingScanner.HasPending);
        Vector2Int allowance = SplitRemovalBudget(densityChangesPerFrame, mining, collapse, _singleSampleCollapseTurn);
        _singleSampleCollapseTurn = !_singleSampleCollapseTurn;
        int mined = ProcessRemovalLane(ref _miningWork, false, allowance.x);
        // Roof checks run even when an island search/removal is busy. Their
        // own time budget is measured separately from density/volume work.
        _removalWatch.Stop();
        RunCeilingBudget();
        _removalWatch.Start();
        int remaining = allowance.y + allowance.x - mined;
        bool hasRoof = _ceilingWork != null || _ceilingQueue.Count > 0;
        bool hasIsland = _collapseWork != null || _collapseQueue.Count > 0;
        int roofAllowance = SplitCeilingQuota(remaining, hasRoof, hasIsland, _singleSampleRoofTurn);
        if (remaining == 1 && hasRoof && hasIsland) _singleSampleRoofTurn = !_singleSampleRoofTurn;
        int roofUsed = ProcessRemovalLane(ref _ceilingWork, true, roofAllowance, true);
        ProcessRemovalLane(ref _collapseWork, true, remaining - roofUsed);
        LastRemovalMilliseconds = (float)_removalWatch.Elapsed.TotalMilliseconds;
    }

    private int ProcessRemovalLane(ref RemovalWork work, bool collapse, int allowance, bool ceiling = false)
    {
        int used = 0, items = 0;
        bool committed = false;
        // Every busy lane may make one atomic batch even when the soft time budget
        // has expired. This prevents starvation. It is not a hard frame-time cap.
        while (used < allowance && items++ < 32)
        {
            if ((committed || items > 1) && _removalWatch.Elapsed.TotalMilliseconds >= Mathf.Max(0.1f, removalMillisecondsPerFrame)) break;
            if (work == null)
            {
                if (collapse)
                {
                    Queue<List<Vector3Int>> queue = ceiling ? _ceilingQueue : _collapseQueue;
                    if (queue.Count == 0) break;
                    work = new RemovalWork { CollapseSamples = queue.Dequeue(), IsCollapse = true };
                }
                else
                {
                    if (_edits.Count == 0) break;
                    PendingEdit edit = _edits.Dequeue();
                    work = new RemovalWork { Changes = BuildBrush(edit), Miner = edit.Miner };
                }
            }
            int batch = Mathf.Clamp(removalBatchSize, 16, 128);
            if (collapse && _removalWatch.Elapsed.TotalMilliseconds >= Mathf.Max(0.1f, removalMillisecondsPerFrame)) batch = Mathf.Min(batch, 16);
            int count = Mathf.Min(batch, Mathf.Min(allowance - used, work.Count - work.Cursor));
            if (count > 0)
            {
                CommitChanges(work, count);
                used += count;
                committed = true;
            }
            if (work.Cursor >= work.Count) work = null;
        }
        return used;
    }

    private void EmitCollapseEffects()
    {
        if (_collapseEffectCount == 0 || Time.unscaledTime < _nextCollapseEffectTime) return;
        Vector3 center = _collapseEffectMoment / _collapseEffectCount;
        GameEvents.TriggerClusterCollapsed(center, _collapseEffectCount);
        RpcCollapseEffect(center, _collapseEffectCount);
        _collapseEffectCount = 0; _collapseEffectMoment = Vector3.zero;
        _nextCollapseEffectTime = Time.unscaledTime + 0.1f;
    }

    private void CommitChanges(RemovalWork work, int requestedCount)
    {
        List<DensityChange> changes = _commitChanges;
        HashSet<Vector3Int> cells = _commitCells;
        changes.Clear(); cells.Clear();
        int end = work.Cursor + requestedCount;
        for (; work.Cursor < end; work.Cursor++)
        {
            DensityChange change = work.IsCollapse
                ? new DensityChange { Position = work.CollapseSamples[work.Cursor], Value = 1f }
                : work.Changes[work.Cursor];
            if (work.IsCollapse && _terrain.Get(change.Position) >= 0f) continue;
            if (change.Value <= _terrain.Get(change.Position)) continue;
            changes.Add(change);
            for (int x = 0; x <= 1; x++)
                for (int y = 0; y <= 1; y++)
                    for (int z = 0; z <= 1; z++)
                        cells.Add(change.Position - new Vector3Int(x, y, z));
        }
        if (changes.Count == 0) return;
        var before = _beforeCellMeasures;
        before.Clear();
        foreach (Vector3Int cell in cells) before[cell] = _volume.SampleCell(cell, _densityReader);

        var positions = new Vector3Int[changes.Count];
        var values = new float[changes.Count];
        for (int i = 0; i < changes.Count; i++)
        {
            Vector3Int p = changes[i].Position;
            float old = _terrain.Get(p);
            _terrain.Set(p, changes[i].Value);
            positions[i] = p; values[i] = changes[i].Value;
            // Fractional weakening cannot disconnect the solid-sample graph.
            // It CAN make a roof thin, so the independent roof scanner still sees it.
            if (old < 0f && changes[i].Value >= 0f) _structuralChanges.Add(p);
            NotifyCeilingChange(p, !work.IsCollapse);
            MarkSampleDirty(p);
            if (old < 0f && changes[i].Value >= 0f)
            {
                if (work.IsCollapse)
                {
                    _collapseEffectMoment += (Vector3)p * voxelSize;
                    _collapseEffectCount++;
                }
            }
        }

        BeginRemovalRewardBatch();
        foreach (Vector3Int cell in cells)
        {
            TerrainVolume.Measure removed = before[cell] - _volume.SampleCell(cell, _densityReader);
            if (removed.Volume <= 0.0000001f) continue; // Numerical round-off, in unit-cell volumes.
            Vector3 localCenter = removed.Moment / removed.Volume;
            localCenter = Vector3.Max(Vector3.zero, Vector3.Min(Vector3.one, localCenter));
            Vector3 center = ((Vector3)cell + localCenter) * voxelSize;
            RewardRemoval(cell, center, removed.Volume * voxelSize * voxelSize * voxelSize, work.IsCollapse, work.Miner);
        }
        FlushRemovalRewardBatch(work.Miner);
        // Host and remote miners use this SAME authoritative broadcast path.
        RpcApplyDensityDelta(++_revision, positions, values);
    }

    private void MarkSampleDirty(Vector3Int sample)
    {
        foreach (Vector3Int chunk in _terrain.ChunksSharingSample(sample))
        {
            _meshFence.MarkDirty(chunk);
            if (_dirtyMeshes.Add(chunk)) _dirtyMeshOrder.Enqueue(chunk);
        }
    }

    private void RunStructuralBudget()
    {
        if (_solver == null && _structuralChanges.Count > 0 && _collapseQueue.Count == 0 &&
            _collapseWork == null)
        {
            // Bound pass construction as well as traversal. All other changed
            // samples remain queued, including changes made during a prior pass.
            var seeds = new List<Vector3Int>();
            foreach (Vector3Int p in _structuralChanges)
            {
                seeds.Add(p);
                if (seeds.Count >= 256) break;
            }
            foreach (Vector3Int p in seeds) _structuralChanges.Remove(p);
            _solver = new TerrainStructuralSolver(seeds, _terrain.Get,
                _terrain.HasSolidRayToInfinity, 0); // Roofs have a separate, non-blocking scanner.
        }
        if (_solver == null) return;
        _structureWatch.Restart();
        int remaining = Mathf.Max(1, structuralOperationsPerFrame);
        do
        {
            int step = Mathf.Min(32, remaining);
            _solver.Step(step);
            remaining -= step;
        }
        while (!_solver.IsComplete && remaining > 0 && _structureWatch.Elapsed.TotalMilliseconds < structuralMillisecondsPerFrame);

        List<Vector3Int> collapse;
        while (_solver.TryGetCollapse(out collapse))
            _collapseQueue.Enqueue(collapse); // Transfer ownership; no giant per-frame copy.
        if (_solver.IsComplete) _solver = null;
    }

    private void FlushDirtyMeshes()
    {
        _meshWatch.Restart();
        LastMeshMilliseconds = 0f;
        if (_dirtyMeshOrder.Count == 0 || _receivingSnapshot) return;
        int builds = 0;
        while (_dirtyMeshOrder.Count > 0 && builds < Mathf.Max(1, meshRebuildsPerFrame))
        {
            if (builds > 0 && _meshWatch.Elapsed.TotalMilliseconds >= Mathf.Max(0.1f, meshMillisecondsPerFrame)) break;
            Vector3Int coord = _dirtyMeshOrder.Dequeue();
            _dirtyMeshes.Remove(coord);
            Chunk chunk;
            if (_activeChunks.TryGetValue(coord, out chunk))
            {
                _terrain.FillChunk(coord, chunk.GetDensities());
                chunk.BuildMeshSync(); // Also runs on a dedicated server.
                _meshFence.MarkBuilt(coord);
                builds++;
            }
            // Sleeping and ungenerated chunks rebuild from the store on activation.
        }
        LastMeshMilliseconds = (float)_meshWatch.Elapsed.TotalMilliseconds;
    }

    [ClientRpc]
    private void RpcCollapseEffect(Vector3 center, int size)
    {
        if (!isServer) GameEvents.TriggerClusterCollapsed(center, size);
    }

    [ClientRpc]
    private void RpcApplyDensityDelta(long revision, Vector3Int[] positions, float[] values)
    {
        if (isServer) return;
        Delta delta = new Delta { Revision = revision, Positions = positions, Values = values };
        if (!IsReady || _receivingSnapshot || _snapshotRequested)
        {
            _bufferedDeltas.Add(delta);
            return;
        }
        if (revision <= _lastClientRevision) return;
        if (revision != _lastClientRevision + 1)
        {
            _bufferedDeltas.Add(delta);
            RequestSnapshot(); // Recover if interest management interrupted observation.
            return;
        }
        ApplyClientDelta(delta);
    }

    private void ApplyClientDelta(Delta delta)
    {
        for (int i = 0; i < delta.Positions.Length; i++)
        {
            _terrain.Set(delta.Positions[i], delta.Values[i]);
            MarkSampleDirty(delta.Positions[i]);
        }
        _lastClientRevision = delta.Revision;
    }

    private void RequestSnapshot()
    {
        if (_snapshotRequested) return;
        _snapshotRequested = true;
        IsReady = false;
        StartCoroutine(RequestSnapshotWhenReady());
    }

    private IEnumerator RequestSnapshotWhenReady()
    {
        while (NetworkClient.active && !NetworkClient.ready) yield return null;
        if (NetworkClient.active) CmdRequestTerrainSnapshot();
    }

    [Command(requiresAuthority = false)]
    private void CmdRequestTerrainSnapshot(NetworkConnectionToClient sender = null)
    {
        if (sender == null || !sender.isReady || _terrain == null || !_syncingConnections.Add(sender.connectionId)) return;
        StartCoroutine(SendTerrainSnapshot(sender));
    }

    private IEnumerator SendTerrainSnapshot(NetworkConnectionToClient target)
    {
        long revision = _revision;
        var snapshot = _terrain.CopyOverrides(); // A value copy, never live mutable arrays.
        TargetBeginTerrainSnapshot(target, revision, chunkSize, voxelSize, maxYChunk, seed, _worldRulesSignature);
        const int batchSize = 128; // Keep individual reliable RPCs modest in size.
        for (int offset = 0; offset < snapshot.Length; offset += batchSize)
        {
            if (!NetworkServer.connections.ContainsKey(target.connectionId)) break;
            int count = Mathf.Min(batchSize, snapshot.Length - offset);
            var positions = new Vector3Int[count];
            var values = new float[count];
            for (int i = 0; i < count; i++) { positions[i] = snapshot[offset + i].Key; values[i] = snapshot[offset + i].Value; }
            TargetTerrainSnapshotPart(target, positions, values);
            yield return null;
        }
        _syncingConnections.Remove(target.connectionId);
        if (NetworkServer.connections.ContainsKey(target.connectionId)) TargetEndTerrainSnapshot(target);
    }

    [TargetRpc]
    private void TargetBeginTerrainSnapshot(NetworkConnectionToClient target, long revision, Vector3Int size, float spacing, int topChunk, int worldSeed, string rulesSignature)
    {
        if (isServer) return;
        _receivingSnapshot = true;
        IsReady = false;
        try { InitializeClientMaterials(worldSeed, rulesSignature); }
        catch (Exception ex)
        {
            _terrain = null; _densityReader = null; IsReady = false;
            Debug.LogError("[Gobmine Materials] " + ex.Message); NetworkClient.Disconnect(); return;
        }
        chunkSize = size; voxelSize = spacing; maxYChunk = topChunk;
        _terrain = new TerrainDensityStore(chunkSize, voxelSize);
        _densityReader = _terrain.Get;
        _snapshotRevision = revision;
        foreach (var entry in _activeChunks) entry.Value.gameObject.SetActive(false);
    }

    [TargetRpc]
    private void TargetTerrainSnapshotPart(NetworkConnectionToClient target, Vector3Int[] positions, float[] values)
    {
        if (isServer || _terrain == null) return;
        for (int i = 0; i < positions.Length; i++) _terrain.Set(positions[i], values[i]);
    }

    [TargetRpc]
    private void TargetEndTerrainSnapshot(NetworkConnectionToClient target)
    {
        if (isServer || _terrain == null) return;
        _lastClientRevision = _snapshotRevision;
        _bufferedDeltas.Sort((a, b) => a.Revision.CompareTo(b.Revision));
        bool missingRevision = false;
        foreach (Delta delta in _bufferedDeltas)
        {
            if (delta.Revision <= _lastClientRevision) continue;
            if (delta.Revision != _lastClientRevision + 1) { missingRevision = true; break; }
            ApplyClientDelta(delta);
        }
        _bufferedDeltas.Clear();
        _receivingSnapshot = false;
        _snapshotRequested = false;
        if (missingRevision)
        {
            // Never silently accept a hole in terrain history after a late-join baseline.
            RequestSnapshot();
            return;
        }
        // Rebuild existing views, or generate the initial colliders before allowing mining.
        foreach (var entry in _activeChunks)
        {
            entry.Value.Init(chunkSize, voxelSize, ChunkPosition(entry.Key), noiseScale, seed, _terrain.MakeChunk(entry.Key));
            entry.Value.gameObject.SetActive(true);
            _meshFence.MarkBuilt(entry.Key);
        }
        _dirtyMeshes.Clear(); _dirtyMeshOrder.Clear();
        StartWorldGeneration();
    }

    private void StartWorldGeneration()
    {
        for (int x = -2; x <= 2; x++)
            for (int y = -2; y <= 2; y++)
                for (int z = -2; z <= 2; z++)
                {
                    Vector3Int c = new Vector3Int(x, y, z);
                    if (c.y <= maxYChunk && !_activeChunks.ContainsKey(c) && !_sleepingChunks.ContainsKey(c)) GenerateChunkAt(c);
                }
        IsReady = true;
        if (_chunkRoutine == null) _chunkRoutine = StartCoroutine(ChunkManagerRoutine());
    }

    private IEnumerator ChunkManagerRoutine()
    {
        float nextRefresh = 0f;
        while (true)
        {
            if (!IsReady) { yield return null; continue; }
            if (Time.unscaledTime >= nextRefresh)
            {
                using (StreamingMarker.Auto()) UpdateVisibleChunks();
                nextRefresh = Time.unscaledTime + 0.25f;
            }
            int count = Mathf.Max(1, chunkBuildsPerFrame);
            while (count-- > 0 && _chunksToGenerate.Count > 0)
            {
                Vector3Int coord = _chunksToGenerate.Dequeue();
                _generationQueued.Remove(coord);
                if (!_activeChunks.ContainsKey(coord) && NearAnyPlayer(coord, viewDistanceInChunks))
                {
                    using (StreamingMarker.Auto())
                    {
                        Chunk sleeping;
                        if (_sleepingChunks.TryGetValue(coord, out sleeping))
                        {
                            _sleepingChunks.Remove(coord);
                            sleeping.Init(chunkSize, voxelSize, ChunkPosition(coord), noiseScale, seed, _terrain.MakeChunk(coord));
                            sleeping.gameObject.SetActive(true);
                            _activeChunks[coord] = sleeping;
                            _meshFence.MarkBuilt(coord);
                        }
                        else GenerateChunkAt(coord);
                    }
                }
            }
            yield return null;
        }
    }

    private bool NearAnyPlayer(Vector3Int c, int distance)
    {
        foreach (Transform player in _connectedPlayers)
        {
            if (player == null) continue;
            Vector3Int p = GetChunkCoordFromPosition(player.position);
            if (Mathf.Abs(c.x - p.x) <= distance && Mathf.Abs(c.y - p.y) <= distance && Mathf.Abs(c.z - p.z) <= distance) return true;
        }
        return false;
    }

    private void UpdateVisibleChunks()
    {
        _connectedPlayers.RemoveAll(p => p == null);
        if (_connectedPlayers.Count == 0) { _lastStreamingCenters.Clear(); return; }
        bool changed = _lastStreamingView != viewDistanceInChunks || _lastStreamingSleep != sleepDistanceInChunks ||
            _lastStreamingTop != maxYChunk || _lastStreamingCenters.Count != _connectedPlayers.Count;
        if (!changed)
            for (int i = 0; i < _connectedPlayers.Count; i++)
                if (_lastStreamingCenters[i] != GetChunkCoordFromPosition(_connectedPlayers[i].position)) { changed = true; break; }
        if (!changed) return;
        _lastStreamingCenters.Clear();
        foreach (Transform player in _connectedPlayers) _lastStreamingCenters.Add(GetChunkCoordFromPosition(player.position));
        _lastStreamingView = viewDistanceInChunks; _lastStreamingSleep = sleepDistanceInChunks; _lastStreamingTop = maxYChunk;
        var required = new HashSet<Vector3Int>();
        foreach (Transform player in _connectedPlayers)
        {
            Vector3Int center = GetChunkCoordFromPosition(player.position);
            int distance = Mathf.Max(1, viewDistanceInChunks);
            for (int x = -distance; x <= distance; x++)
                for (int y = -distance; y <= distance; y++)
                    for (int z = -distance; z <= distance; z++)
                    {
                        Vector3Int c = center + new Vector3Int(x, y, z);
                        if (c.y <= maxYChunk) required.Add(c);
                    }
        }
        var remove = new List<Vector3Int>();
        foreach (var entry in _activeChunks)
            if (!required.Contains(entry.Key))
            {
                entry.Value.gameObject.SetActive(false);
                _sleepingChunks[entry.Key] = entry.Value;
                remove.Add(entry.Key);
            }
        foreach (Vector3Int c in remove) _activeChunks.Remove(c);
        remove.Clear();
        foreach (var entry in _sleepingChunks)
            if (!NearAnyPlayer(entry.Key, Mathf.Max(viewDistanceInChunks, sleepDistanceInChunks)))
            {
                _chunkPool.Enqueue(entry.Value);
                remove.Add(entry.Key);
            }
        foreach (Vector3Int c in remove) _sleepingChunks.Remove(c);
        // The old version rebuilt ALL waking sleeping chunks in this refresh.
        // Put both wake-ups and fresh chunks through the per-frame build budget.
        var missing = new List<Vector3Int>();
        foreach (Vector3Int c in required) if (!_activeChunks.ContainsKey(c)) missing.Add(c);
        missing.Sort((a, b) => NearestStreamingDistance(a).CompareTo(NearestStreamingDistance(b)));
        _chunksToGenerate.Clear(); _generationQueued.Clear();
        foreach (Vector3Int c in missing) { _generationQueued.Add(c); _chunksToGenerate.Enqueue(c); }
    }

    private int NearestStreamingDistance(Vector3Int coord)
    {
        int best = int.MaxValue;
        foreach (Vector3Int center in _lastStreamingCenters)
        {
            Vector3Int d = coord - center;
            int distance = d.x * d.x + d.y * d.y + d.z * d.z;
            if (distance < best) best = distance;
        }
        return best;
    }

    private Vector3 ChunkPosition(Vector3Int c) => (Vector3)Vector3Int.Scale(c, chunkSize) * voxelSize;

    private void GenerateChunkAt(Vector3Int c)
    {
        Chunk chunk;
        if (_chunkPool.Count > 0) chunk = _chunkPool.Dequeue();
        else
        {
            GameObject obj = new GameObject();
            obj.transform.SetParent(transform);
            chunk = obj.AddComponent<Chunk>();
        }
        chunk.gameObject.name = $"Chunk_{c.x}_{c.y}_{c.z}";
        chunk.gameObject.layer = terrainLayer;
        chunk.transform.position = ChunkPosition(c);
        chunk.transform.rotation = Quaternion.identity;
        chunk.transform.localScale = Vector3.one;
        chunk.Init(chunkSize, voxelSize, ChunkPosition(c), noiseScale, seed, _terrain.MakeChunk(c));
        chunk.gameObject.SetActive(true);
        _activeChunks[c] = chunk;
        _meshFence.MarkBuilt(c);
    }

    public Vector3Int GetChunkCoordFromPosition(Vector3 pos) => new Vector3Int(
        Mathf.FloorToInt(pos.x / (chunkSize.x * voxelSize)),
        Mathf.FloorToInt(pos.y / (chunkSize.y * voxelSize)),
        Mathf.FloorToInt(pos.z / (chunkSize.z * voxelSize)));

    public Material GetVoxelMaterial() => voxelMaterial;
}
