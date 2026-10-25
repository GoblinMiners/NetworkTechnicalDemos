using System;
using System.Collections.Generic;
using UnityEngine;
using Random = UnityEngine.Random;

public partial class World
{
    [Header("Rubble responsiveness")]
    [Tooltip("Expired COSMETIC work is discarded instead of appearing in a late burst. Inventory, ore pickups and cave-in damage are separate.")]
    [UnityEngine.Range(0.1f, 2f)] public float maximumCosmeticDebrisAgeSeconds = 0.5f;
    [Min(16)] public int maximumPendingCosmeticDebris = 256;
    [Tooltip("Representative rubble size cap. A grouped cubic metre does not become a giant cube jammed inside a thin opening.")]
    [UnityEngine.Range(0.05f, 1f)] public float maximumDebrisPieceSizeMetres = 0.35f;
    public int CosmeticDebrisCulled { get; private set; }
    public float OldestPendingDebrisSeconds { get; private set; }
    public float LastDebrisSpawnLatencySeconds { get; private set; }
    private readonly TerrainMeshFence _meshFence = new TerrainMeshFence();
    private struct RubbleKey : IEquatable<RubbleKey>
    {
        public Vector3Int Cell;
        public int MaterialId;
        public bool Collapse;
        public bool Equals(RubbleKey b) => Cell == b.Cell && MaterialId == b.MaterialId && Collapse == b.Collapse;
        public override bool Equals(object obj) => obj is RubbleKey && Equals((RubbleKey)obj);
        public override int GetHashCode() { unchecked { return (Cell.GetHashCode() * 397 ^ MaterialId) * 397 ^ (Collapse ? 1 : 0); } }
    }
    private sealed class RubbleBatch
    {
        public RubbleKey Key;
        public float Created, ReadyAt, Volume;
        public int Frame;
        public Vector3 Moment, SpawnHint;
        public readonly Dictionary<Vector3Int, long> RequiredVersions = new Dictionary<Vector3Int, long>();
    }
    private readonly Dictionary<RubbleKey, RubbleBatch> _openRubble = new Dictionary<RubbleKey, RubbleBatch>();
    private readonly Queue<RubbleBatch> _readyMiningRubble = new Queue<RubbleBatch>();
    private readonly Queue<RubbleBatch> _readyCollapseRubble = new Queue<RubbleBatch>();
    private readonly List<RubbleKey> _sealRubbleKeys = new List<RubbleKey>();
    private bool _rubbleCollapseTurn;

    private void QueueDebris(Vector3Int cell, Vector3 center, float volume, int materialId, bool collapse)
    {
        int size = Mathf.Clamp(debrisBucketSizeInCells, 1, 4);
        RubbleKey key = new RubbleKey { Cell = new Vector3Int(TerrainDensityStore.FloorDiv(cell.x, size),
            TerrainDensityStore.FloorDiv(cell.y, size), TerrainDensityStore.FloorDiv(cell.z, size)), MaterialId = materialId, Collapse = collapse };
        RubbleBatch batch;
        if (_openRubble.TryGetValue(key, out batch) && batch.Frame != Time.frameCount && Time.unscaledTime >= batch.ReadyAt)
        {
            // Freeze this request's mesh dependencies. Later cuts get a NEW
            // request and cannot indefinitely move its finish line.
            SealRubble(batch); _openRubble.Remove(key); batch = null;
        }
        if (batch == null)
        {
            batch = new RubbleBatch { Key = key, Created = Time.unscaledTime, Frame = Time.frameCount,
                ReadyAt = Time.unscaledTime + Mathf.Clamp(debrisBatchDelaySeconds, 0f, 0.25f) };
            _openRubble.Add(key, batch);
        }
        // Include possible placement/clearance offsets across chunk seams, not
        // just the cell owner. A shifted cube must not enter a stale neighbour collider.
        float margin = Mathf.Max(0.05f, maximumDebrisPieceSizeMetres) * 2f + voxelSize;
        Vector3Int low = GetChunkCoordFromPosition(center - Vector3.one * margin);
        Vector3Int high = GetChunkCoordFromPosition(center + Vector3.one * margin);
        for (int x = low.x; x <= high.x; x++) for (int y = low.y; y <= high.y; y++) for (int z = low.z; z <= high.z; z++)
        {
            Vector3Int dependency = new Vector3Int(x, y, z);
            batch.RequiredVersions[dependency] = _meshFence.Capture(dependency);
        }
        batch.Volume += volume; batch.Moment += center * volume; batch.SpawnHint = center;
    }
    private void SealRubble(RubbleBatch batch)
    {
        (batch.Key.Collapse ? _readyCollapseRubble : _readyMiningRubble).Enqueue(batch);
        int cap = Mathf.Max(16, maximumPendingCosmeticDebris);
        while (_readyCollapseRubble.Count + _readyMiningRubble.Count > cap)
        {
            // Trim old collapse visuals first; this does not cancel damage.
            (_readyCollapseRubble.Count > 0 ? _readyCollapseRubble : _readyMiningRubble).Dequeue();
            CosmeticDebrisCulled++;
        }
    }
    private void FlushDebris()
    {
        _sealRubbleKeys.Clear();
        foreach (var entry in _openRubble)
            if (Time.unscaledTime >= entry.Value.ReadyAt) _sealRubbleKeys.Add(entry.Key);
        foreach (RubbleKey key in _sealRubbleKeys) { SealRubble(_openRubble[key]); _openRubble.Remove(key); }
        bool configured = DebrisPool.Instance != null && DebrisPool.Instance.IsConfigured;
        if (!configured && PendingDebrisBuckets > 0 && !_warnedDebris)
        {
            _warnedDebris = true;
            Debug.LogError("[World] DebrisPool/prefab missing. Cosmetic rubble cannot spawn; inventory, recoverable ore and collapse damage remain authoritative.");
        }
        if (configured) _warnedDebris = false;
        int spawned = 0;
        int cap = Mathf.Max(1, debrisSpawnsPerFrame);
        int attempts = Mathf.Min(_readyMiningRubble.Count + _readyCollapseRubble.Count, Mathf.Max(32, cap * 16));
        OldestPendingDebrisSeconds = 0f;
        while (attempts-- > 0 && spawned < cap)
        {
            bool collapse = _readyMiningRubble.Count == 0 || (_readyCollapseRubble.Count > 0 && _rubbleCollapseTurn);
            Queue<RubbleBatch> queue = collapse ? _readyCollapseRubble : _readyMiningRubble;
            if (queue.Count == 0) break;
            _rubbleCollapseTurn = !_rubbleCollapseTurn;
            RubbleBatch batch = queue.Dequeue();
            float age = Time.unscaledTime - batch.Created;
            OldestPendingDebrisSeconds = Mathf.Max(OldestPendingDebrisSeconds, age);
            if (age > Mathf.Max(0.1f, maximumCosmeticDebrisAgeSeconds)) { CosmeticDebrisCulled++; continue; }
            bool ready = configured && DebrisPool.Instance.HasCapacity;
            foreach (var dependency in batch.RequiredVersions)
                if (!_activeChunks.ContainsKey(dependency.Key) || !_meshFence.IsSatisfied(dependency.Key, dependency.Value))
                { ready = false; break; }
            if (!ready) { queue.Enqueue(batch); continue; }
            Vector3 position;
            float size = Mathf.Min(Mathf.Pow(batch.Volume, 1f / 3f), Mathf.Max(0.05f, maximumDebrisPieceSizeMetres));
            if (!TryPlaceRubble(batch.Moment / batch.Volume, batch.SpawnHint, ref size, out position))
            { CosmeticDebrisCulled++; continue; }
            // Visual scale and mass agree. Material quantities were already
            // accounted from the ACTUAL removed volume, not this representative.
            GameObject obj = DebrisPool.Instance.GetDebris(position, Random.rotation, Vector3.one * size,
                size * size * size, batch.Key.MaterialId);
            if (obj == null) { queue.Enqueue(batch); continue; }
            LastDebrisSpawnLatencySeconds = age; spawned++;
        }
        OldestPendingDebrisSeconds = 0f;
        foreach (RubbleBatch pending in _readyMiningRubble) OldestPendingDebrisSeconds = Mathf.Max(OldestPendingDebrisSeconds, Time.unscaledTime - pending.Created);
        foreach (RubbleBatch pending in _readyCollapseRubble) OldestPendingDebrisSeconds = Mathf.Max(OldestPendingDebrisSeconds, Time.unscaledTime - pending.Created);
        foreach (RubbleBatch pending in _openRubble.Values) OldestPendingDebrisSeconds = Mathf.Max(OldestPendingDebrisSeconds, Time.unscaledTime - pending.Created);
    }
    private bool TryPlaceRubble(Vector3 center, Vector3 hint, ref float size, out Vector3 position)
    {
        position = center;
        for (int attempt = 0; attempt < 4 && size >= 0.025f; attempt++, size *= 0.5f)
        {
            float radius = size * 0.87f;
            if (RubbleSpaceClear(center, radius)) { position = center; return true; }
            if (RubbleSpaceClear(hint, radius)) { position = hint; return true; }
            float step = Mathf.Max(0.02f, voxelSize * 0.5f);
            Vector3 p = hint / voxelSize;
            float latticeStep = step / voxelSize;
            Vector3 gradient = new Vector3(
                TerrainQuery.SampleDensity(p + Vector3.right * latticeStep, _densityReader) - TerrainQuery.SampleDensity(p - Vector3.right * latticeStep, _densityReader),
                TerrainQuery.SampleDensity(p + Vector3.up * latticeStep, _densityReader) - TerrainQuery.SampleDensity(p - Vector3.up * latticeStep, _densityReader),
                TerrainQuery.SampleDensity(p + Vector3.forward * latticeStep, _densityReader) - TerrainQuery.SampleDensity(p - Vector3.forward * latticeStep, _densityReader));
            Vector3 shifted = hint + gradient.normalized * (radius + step);
            if (RubbleSpaceClear(shifted, radius)) { position = shifted; return true; }
        }
        return false;
    }
    private bool RubbleSpaceClear(Vector3 center, float radius)
    {
        if (!IsAirAt(center)) return false;
        // Conservative sampled clearance for a rotated cube. This is not a full
        // collision sweep; extremely narrow fragments may be intentionally culled.
        for (int x = -1; x <= 1; x++) for (int y = -1; y <= 1; y++) for (int z = -1; z <= 1; z++)
            if ((x != 0 || y != 0 || z != 0) && !IsAirAt(center + new Vector3(x, y, z) * radius)) return false;
        return true;
    }
    private void ResetRubbleRuntime()
    {
        _meshFence.Clear(); _openRubble.Clear(); _readyMiningRubble.Clear(); _readyCollapseRubble.Clear(); _sealRubbleKeys.Clear();
        _rubbleCollapseTurn = false; CosmeticDebrisCulled = 0; OldestPendingDebrisSeconds = 0f; LastDebrisSpawnLatencySeconds = 0f;
    }
}
