using System;
using System.Collections.Generic;
using Mirror;
using UnityEngine;

public partial class World
{
    [Header("Cave-ins - independent ceiling checks")]
    [Tooltip("Maximum actual vertical rock thickness between two air spaces. METRES, not samples. Zero disables this rule only.")]
    [UnityEngine.Range(0f, 4f)] public float ceilingCollapseThicknessMetres = 1f;
    [Min(8)] public int ceilingChecksPerFrame = 256;
    [Min(0.1f)] public float ceilingMillisecondsPerFrame = 1f;

    [Header("Cave-ins - server damage")]
    public bool collapseDamagesPlayers = true;
    [Min(0f)] public float collapseDamagePerCubicMetre = 100f;
    [Min(0f)] public float maximumCollapseDamagePerHit = 40f;
    [Min(0.05f)] public float collapseDamageCooldown = 0.5f;
    [Min(0f)] public float minimumCollapseImpactSpeed = 1f;
    [Min(1f)] public float maximumCollapseFallDistance = 10f;
    [Tooltip("Only nearby falling material is simulated for damage. Includes a margin for players walking into the fall.")]
    [Min(10f)] public float collapseHazardActivationRadius = 24f;

    private TerrainCeilingScanner _ceilingScanner;
    private readonly Queue<List<Vector3Int>> _ceilingQueue = new Queue<List<Vector3Int>>();
    private RemovalWork _ceilingWork;
    private bool _singleSampleRoofTurn;
    private static int SplitCeilingQuota(int remaining, bool roof, bool island, bool roofTurn)
    {
        if (!roof || remaining <= 0) return 0;
        if (!island) return remaining;
        if (remaining == 1) return roofTurn ? 1 : 0;
        return Mathf.Max(1, remaining / 2);
    }
    private readonly System.Diagnostics.Stopwatch _ceilingWatch = new System.Diagnostics.Stopwatch();
    private static readonly Unity.Profiling.ProfilerMarker CeilingMarker = new Unity.Profiling.ProfilerMarker("Gobmine.CeilingChecks");
    private static readonly Unity.Profiling.ProfilerMarker CollapseDamageMarker = new Unity.Profiling.ProfilerMarker("Gobmine.CollapseDamage");
    public int PendingCeilingBands => _ceilingScanner == null ? 0 : _ceilingScanner.PendingBands;
    public int PendingCeilingColumns => _ceilingQueue.Count + (_ceilingWork != null ? 1 : 0);
    public long CeilingSamplesScheduled { get; private set; }
    public int ActiveCollapseHazards => _fallPatches.Count + _openFallPatches.Count;
    public int CollapseDamageHits { get; private set; }
    public float LastCeilingMilliseconds { get; private set; }

    private void InitializeCeilingScanner()
    {
        _ceilingScanner = new TerrainCeilingScanner(_densityReader,
            Mathf.Clamp(ceilingCollapseThicknessMetres, 0f, 4f) / voxelSize);
    }
    private void NotifyCeilingChange(Vector3Int p, bool urgent)
    {
        if (_ceilingScanner == null) InitializeCeilingScanner();
        _ceilingScanner.NotifyChange(p, urgent);
    }
    private void RunCeilingBudget()
    {
        if (_ceilingScanner == null || !_ceilingScanner.HasPending) return;
        using (CeilingMarker.Auto())
        {
            _ceilingWatch.Restart();
            int remaining = Mathf.Max(8, ceilingChecksPerFrame);
            do
            {
                int step = Mathf.Min(8, remaining);
                _ceilingScanner.Step(step);
                remaining -= step;
                List<Vector3Int> span;
                while (_ceilingScanner.TryTakeCollapse(out span))
                {
                    _ceilingQueue.Enqueue(span);
                    CeilingSamplesScheduled += span.Count;
                }
            }
            while (remaining > 0 && _ceilingScanner.HasPending &&
                _ceilingWatch.Elapsed.TotalMilliseconds < Mathf.Max(0.1f, ceilingMillisecondsPerFrame));
            LastCeilingMilliseconds = (float)_ceilingWatch.Elapsed.TotalMilliseconds;
        }
    }

    /// <summary>Explicit diagnostic action; existing saves are also queued at startup.</summary>
    [Server]
    public void RecheckSavedCeilings()
    {
        if (_terrain == null) return;
        InitializeCeilingScanner();
        foreach (var entry in _terrain.CopyOverrides()) NotifyCeilingChange(entry.Key, false);
    }

    private sealed class FallColumn
    {
        public Bounds Bounds;
        public float Volume;
        public float HeightMoment;
    }
    private sealed class FallPatch
    {
        public readonly Dictionary<Vector2Int, FallColumn> Columns = new Dictionary<Vector2Int, FallColumn>();
        public readonly Dictionary<Vector3Int, long> MeshVersions = new Dictionary<Vector3Int, long>();
        public readonly HashSet<GobminePlayerHealth> ConsideredPlayers = new HashSet<GobminePlayerHealth>();
        public Vector3 Center;
        public float Created, Started = -1f, PreviousDrop;
        public void Clear()
        {
            Columns.Clear(); MeshVersions.Clear(); ConsideredPlayers.Clear();
            Center = Vector3.zero; Created = 0f; Started = -1f; PreviousDrop = 0f;
        }
    }
    private readonly Dictionary<Vector3Int, FallPatch> _openFallPatches = new Dictionary<Vector3Int, FallPatch>();
    private readonly List<FallPatch> _fallPatches = new List<FallPatch>();
    private readonly Stack<FallPatch> _fallPatchPool = new Stack<FallPatch>();
    private readonly Dictionary<GobminePlayerHealth, float> _collapseDamageThisTick = new Dictionary<GobminePlayerHealth, float>();
    private float _nextDamageTick;
    private bool _warnedMissingHealth;

    private bool NearHazardPlayer(Vector3 center)
    {
        float radius = Mathf.Max(10f, collapseHazardActivationRadius);
        foreach (Transform player in _connectedPlayers)
        {
            if (player == null) continue;
            Vector3 delta = player.position - center;
            if (delta.x * delta.x + delta.z * delta.z <= radius * radius &&
                Mathf.Abs(delta.y) <= Mathf.Max(1f, maximumCollapseFallDistance) + 6f) return true;
        }
        return false;
    }

    // Called only for actual collapse removal, never ordinary tool carving.
    private void QueueCollapseHazard(Vector3Int cell, Vector3 center, float volume)
    {
        if (!collapseDamagesPlayers || volume <= 0f || !NearHazardPlayer(center)) return;
        Vector3Int bucket = new Vector3Int(TerrainDensityStore.FloorDiv(cell.x, 4),
            TerrainDensityStore.FloorDiv(cell.y, 4), TerrainDensityStore.FloorDiv(cell.z, 4));
        FallPatch patch;
        if (!_openFallPatches.TryGetValue(bucket, out patch))
        {
            patch = _fallPatchPool.Count > 0 ? _fallPatchPool.Pop() : new FallPatch();
            patch.Clear(); patch.Center = center; patch.Created = Time.time;
            _openFallPatches.Add(bucket, patch);
        }
        Vector2Int xz = new Vector2Int(cell.x, cell.z);
        FallColumn column;
        Bounds bounds = new Bounds(((Vector3)cell + Vector3.one * 0.5f) * voxelSize, Vector3.one * voxelSize);
        if (!patch.Columns.TryGetValue(xz, out column))
        {
            column = new FallColumn { Bounds = bounds };
            patch.Columns.Add(xz, column);
        }
        else { Bounds merged = column.Bounds; merged.Encapsulate(bounds); column.Bounds = merged; }
        column.Volume += volume; column.HeightMoment += center.y * volume;
        Vector3Int owner = _terrain.Owner(cell);
        patch.MeshVersions[owner] = _meshFence.Capture(owner);
    }

    // Geometry-aware falling-column damage, intentionally independent of the
    // number of cosmetic rigidbodies. It is not per-fragment PhysX impact damage.
    private void UpdateCollapseDamage()
    {
        foreach (FallPatch patch in _openFallPatches.Values) _fallPatches.Add(patch);
        _openFallPatches.Clear();
        if (Time.time < _nextDamageTick) return;
        _nextDamageTick = Time.time + 0.05f;
        _collapseDamageThisTick.Clear();
        float gravity = Mathf.Max(0.01f, -Physics.gravity.y);
        float maxDrop = Mathf.Max(1f, maximumCollapseFallDistance);
        using (CollapseDamageMarker.Auto())
        {
            for (int i = _fallPatches.Count - 1; i >= 0; i--)
            {
                FallPatch patch = _fallPatches[i];
                if (!collapseDamagesPlayers || !NearHazardPlayer(patch.Center)) { RetireFallPatch(i); continue; }
                if (patch.Started < 0f)
                {
                    bool ready = true, hasViews = true;
                    foreach (var dependency in patch.MeshVersions)
                    {
                        if (!_activeChunks.ContainsKey(dependency.Key)) { ready = false; hasViews = false; }
                        else if (!_meshFence.IsSatisfied(dependency.Key, dependency.Value)) ready = false;
                    }
                    // Do not damage through a roof that has not disappeared from
                    // the server's collision mesh yet. No dependency on rubble.
                    if (!ready)
                    {
                        // Unloaded/off-screen terrain must not retain an old hit
                        // that fires when a player returns much later.
                        if (!hasViews && Time.time - patch.Created > 2f) RetireFallPatch(i);
                        continue;
                    }
                    patch.Started = Time.time;
                }
                float elapsed = Mathf.Max(0f, Time.time - patch.Started);
                float drop = Mathf.Min(maxDrop, 0.5f * gravity * elapsed * elapsed);
                float speed = Mathf.Sqrt(2f * gravity * drop);
                if (speed >= minimumCollapseImpactSpeed)
                {
                    foreach (Transform player in _connectedPlayers)
                    {
                        if (player == null) continue;
                        GobminePlayerHealth health = player.GetComponent<GobminePlayerHealth>();
                        if (health == null)
                        {
                            if (!_warnedMissingHealth) Debug.LogWarning("[Gobmine Cave-ins] Player has no GobminePlayerHealth. Run Install Cave-in Settings and Player Health outside Play mode.");
                            _warnedMissingHealth = true; continue;
                        }
                        if (health.IsDead || patch.ConsideredPlayers.Contains(health)) continue;
                        Bounds body = health.DamageBounds;
                        float amount = 0f;
                        bool touched = false;
                        foreach (FallColumn column in patch.Columns.Values)
                        {
                            if (!CollapseFallMath.SweepsPlayer(column.Bounds, patch.PreviousDrop, drop, body)) continue;
                            // A chunk already below the player does not strike upward.
                            if (column.HeightMoment / column.Volume < body.min.y) continue;
                            touched = true;
                            float x = Mathf.Clamp(body.center.x, column.Bounds.min.x + voxelSize * 0.05f, column.Bounds.max.x - voxelSize * 0.05f);
                            float z = Mathf.Clamp(body.center.z, column.Bounds.min.z + voxelSize * 0.05f, column.Bounds.max.z - voxelSize * 0.05f);
                            float sourceY = column.HeightMoment / column.Volume;
                            // Stops hits through intervening floors/walls using the
                            // authoritative field, even if render meshes are queued.
                            if (SolidBlocksFall(x, z, sourceY, Mathf.Min(sourceY, body.max.y))) continue;
                            amount += column.Volume * Mathf.Max(0f, collapseDamagePerCubicMetre) * Mathf.Clamp(speed / 3f, 0.25f, 3f);
                        }
                        if (!touched) continue;
                        patch.ConsideredPlayers.Add(health); // One encounter per player per patch.
                        if (amount <= 0f) continue;
                        float previous; _collapseDamageThisTick.TryGetValue(health, out previous);
                        _collapseDamageThisTick[health] = previous + amount;
                    }
                }
                patch.PreviousDrop = drop;
                if (drop >= maxDrop) RetireFallPatch(i);
            }
            foreach (var hit in _collapseDamageThisTick)
                if (hit.Key != null && hit.Key.ServerApplyCollapseDamage(
                    Mathf.Min(Mathf.Max(0f, maximumCollapseDamagePerHit), hit.Value), collapseDamageCooldown)) CollapseDamageHits++;
        }
    }

    private bool SolidBlocksFall(float x, float z, float fromY, float toY)
    {
        return CollapseFallMath.BlockedByTerrain(new Vector3(x, fromY, z), toY,
            voxelSize, SampleDamageDensity);
    }
    private float SampleDamageDensity(Vector3 p) => TerrainQuery.SampleDensity(p / voxelSize, _densityReader);
    private void RetireFallPatch(int index)
    {
        FallPatch patch = _fallPatches[index];
        int last = _fallPatches.Count - 1;
        _fallPatches[index] = _fallPatches[last]; _fallPatches.RemoveAt(last);
        patch.Clear();
        if (_fallPatchPool.Count < 256) _fallPatchPool.Push(patch);
    }
    private void ResetCaveInRuntime()
    {
        _ceilingScanner = null; _ceilingQueue.Clear(); _ceilingWork = null; _singleSampleRoofTurn = false;
        _openFallPatches.Clear(); _fallPatches.Clear(); _fallPatchPool.Clear(); _collapseDamageThisTick.Clear();
        _nextDamageTick = 0f; _warnedMissingHealth = false;
        CeilingSamplesScheduled = 0; CollapseDamageHits = 0; LastCeilingMilliseconds = 0f;
    }
}

/// <summary>Pure swept-volume arithmetic used by the server and regression tests.</summary>
public static class CollapseFallMath
{
    public static bool BlockedByTerrain(Vector3 from, float targetY, float step, Func<Vector3, float> density)
    {
        if (step <= 0f) throw new ArgumentOutOfRangeException(nameof(step));
        if (from.y <= targetY) return false;
        if (density(from) < -0.00001f || density(new Vector3(from.x, targetY, from.z)) < -0.00001f) return true;
        // For fixed X/Z, trilinear density is linear between Y lattice planes.
        // Test those planes plus both segment endpoints so a thin intervening
        // layer cannot fall between arbitrarily phased sampling steps.
        for (int latticeY = Mathf.FloorToInt(from.y / step); latticeY * step > targetY; latticeY--)
            if (density(new Vector3(from.x, latticeY * step, from.z)) < -0.00001f) return true;
        return false;
    }
    public static bool SweepsPlayer(Bounds original, float previousDrop, float currentDrop, Bounds player)
    {
        if (currentDrop < previousDrop) return false;
        return original.min.x < player.max.x && original.max.x > player.min.x &&
               original.min.z < player.max.z && original.max.z > player.min.z &&
               original.max.y - previousDrop >= player.min.y && original.min.y - currentDrop <= player.max.y;
    }
}
