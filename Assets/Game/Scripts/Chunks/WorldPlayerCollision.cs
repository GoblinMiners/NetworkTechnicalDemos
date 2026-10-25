using UnityEngine;

/// <summary>
/// Local collision-streaming guard. This is another part of World, NOT a separate component.
/// Builds only the small body/sweep area, within a shared per-frame emergency budget.
/// Does not change density, mining rewards, save data, or network authority.
/// </summary>
public partial class World
{
    [Header("Player collision streaming")]
    [Tooltip("Additional near-player chunk builds, shared across all guarded moves this frame. One build can exceed the soft time limit.")]
    [UnityEngine.Range(1, 4)] public int playerCollisionBuildsPerFrame = 1;
    [Min(0.1f)] public float playerCollisionMillisecondsPerFrame = 2f;
    private int _playerCollisionFrame = -1, _playerCollisionBuilds;
    private double _playerCollisionBuildMilliseconds;
    public int PlayerCollisionWaits { get; private set; }
    public int PlayerCollisionPriorityBuilds { get; private set; }
    private static readonly Unity.Profiling.ProfilerMarker PlayerCollisionMarker =
        new Unity.Profiling.ProfilerMarker("Gobmine.PlayerCollisionStreaming");

    public bool TryPreparePlayerCollision(Bounds sweptBody, out string reason)
    {
        reason = null;
        if (_terrain == null || !IsReady || _receivingSnapshot)
        { reason = "Waiting for terrain snapshot/startup"; PlayerCollisionWaits++; return false; }
        Vector3 metres = (Vector3)chunkSize * voxelSize;
        Vector3Int low, high;
        if (!PlayerMotorRules.ChunkRange(sweptBody, metres, 64, out low, out high))
        { reason = "Invalid or excessively large movement bounds"; PlayerCollisionWaits++; return false; }
        // maxYChunk is a generation limit, NOT an empty-space declaration.
        if (high.y > maxYChunk)
        { reason = "Above generated terrain limit (World / Max Y Chunk)"; PlayerCollisionWaits++; return false; }
        if (_playerCollisionFrame != Time.frameCount)
        { _playerCollisionFrame = Time.frameCount; _playerCollisionBuilds = 0; _playerCollisionBuildMilliseconds = 0; }
        bool builtAny = false;
        for (int x = low.x; x <= high.x; x++)
        for (int y = low.y; y <= high.y; y++)
        for (int z = low.z; z <= high.z; z++)
        {
            Vector3Int coord = new Vector3Int(x, y, z);
            Chunk chunk;
            if (_activeChunks.TryGetValue(coord, out chunk) && chunk != null && chunk.CollisionReady) continue;
            if (_playerCollisionBuilds >= Mathf.Max(1, playerCollisionBuildsPerFrame) ||
                (_playerCollisionBuilds > 0 && _playerCollisionBuildMilliseconds >= Mathf.Max(0.1f, playerCollisionMillisecondsPerFrame)))
            { reason = "Waiting for nearby collision chunk " + coord; PlayerCollisionWaits++; return false; }
            double started = Time.realtimeSinceStartupAsDouble;
            using (PlayerCollisionMarker.Auto())
            {
                if (chunk != null)
                {
                    chunk.Init(chunkSize, voxelSize, ChunkPosition(coord), noiseScale, seed, _terrain.MakeChunk(coord));
                    chunk.gameObject.SetActive(true);
                    _meshFence.MarkBuilt(coord);
                }
                else if (_sleepingChunks.TryGetValue(coord, out chunk) && chunk != null)
                {
                    _sleepingChunks.Remove(coord);
                    chunk.Init(chunkSize, voxelSize, ChunkPosition(coord), noiseScale, seed, _terrain.MakeChunk(coord));
                    chunk.gameObject.SetActive(true);
                    _activeChunks[coord] = chunk;
                    _meshFence.MarkBuilt(coord);
                }
                else
                {
                    _sleepingChunks.Remove(coord);
                    GenerateChunkAt(coord);
                    chunk = _activeChunks[coord];
                }
            }
            builtAny = true;
            _playerCollisionBuilds++; PlayerCollisionPriorityBuilds++;
            _playerCollisionBuildMilliseconds += (Time.realtimeSinceStartupAsDouble - started) * 1000.0;
            if (!chunk.CollisionReady)
            { reason = "Terrain collider disabled/invalid on " + coord; PlayerCollisionWaits++; return false; }
        }
        if (builtAny) Physics.SyncTransforms(); // New/woken collider poses must be visible to this frame's Move.
        // Dirty active chunks retain their previous VALID collider until their synchronous
        // rebuild. Removal-only edits make it conservatively too solid, not a hole. Waiting
        // for every dirty mesh would freeze movement while the player continuously mines.
        return true;
    }

    /// <summary>Conservative recovery test: only a completely solid cell is considered embedded.</summary>
    public bool PlayerCentreInsideSolidCell(Vector3 worldCentre)
    {
        if (_terrain == null || !PlayerMotorRules.Finite(worldCentre) || voxelSize <= 0f) return false;
        Vector3Int cell = Vector3Int.FloorToInt(worldCentre / voxelSize);
        return TerrainQuery.CellState(cell, _densityReader) == TerrainCellState.Solid;
    }
}
