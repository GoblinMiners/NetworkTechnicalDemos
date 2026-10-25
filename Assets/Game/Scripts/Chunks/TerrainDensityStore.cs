using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// One authoritative value per GLOBAL lattice sample. Chunk arrays are disposable
/// views, never independent sources of truth. Negative density is solid.
/// This generator intentionally preserves the original rectangular spawn room.
/// </summary>
public sealed class TerrainDensityStore
{
    public readonly Vector3Int ChunkSize;
    public readonly float VoxelSize;
    // Base rock noise depends on X/Z only. Cache it instead of recomputing Perlin
    // for every Y, volume corner, structural query and chunk rebuild.
    private readonly Dictionary<Vector2Int, float> _baseRockColumns = new Dictionary<Vector2Int, float>();
    private readonly Queue<Vector2Int> _baseRockOrder = new Queue<Vector2Int>();
    private const int BaseRockCacheCapacity = 32768;
    private readonly Dictionary<Vector3Int, float> _overrides = new Dictionary<Vector3Int, float>();
    private readonly HashSet<Vector3Int> _modifiedChunks = new HashSet<Vector3Int>();
    private readonly Dictionary<Vector3Int, Vector2Int> _airXExtents = new Dictionary<Vector3Int, Vector2Int>();

    public TerrainDensityStore(Vector3Int chunkSize, float voxelSize)
    {
        if (chunkSize.x < 1 || chunkSize.y < 1 || chunkSize.z < 1 || voxelSize <= 0f)
            throw new ArgumentOutOfRangeException("Terrain dimensions must be positive.");
        ChunkSize = chunkSize;
        VoxelSize = voxelSize;
    }

    public static int FloorDiv(int value, int divisor)
    {
        int q = value / divisor;
        return value % divisor < 0 ? q - 1 : q;
    }

    public Vector3Int Owner(Vector3Int p) => new Vector3Int(
        FloorDiv(p.x, ChunkSize.x), FloorDiv(p.y, ChunkSize.y), FloorDiv(p.z, ChunkSize.z));

    public float BaseDensity(Vector3Int p)
    {
        Vector3 w = (Vector3)p * VoxelSize;
        bool room = w.x >= -12f && w.x <= 12f && w.y >= -4f && w.y <= 8f && w.z >= -12f && w.z <= 12f;
        if (room) return 5f;
        Vector2Int key = new Vector2Int(p.x, p.z);
        float rock;
        if (_baseRockColumns.TryGetValue(key, out rock)) return rock;
        rock = -5f - Mathf.PerlinNoise(w.x * 0.1f, w.z * 0.1f) * 2f;
        if (_baseRockColumns.Count >= BaseRockCacheCapacity) _baseRockColumns.Remove(_baseRockOrder.Dequeue());
        _baseRockColumns.Add(key, rock); _baseRockOrder.Enqueue(key);
        return rock;
    }

    public float Get(Vector3Int p)
    {
        float value;
        return _overrides.TryGetValue(p, out value) ? value : BaseDensity(p);
    }

    public bool Set(Vector3Int p, float value)
    {
        if (float.IsNaN(value) || float.IsInfinity(value)) throw new ArgumentException("Non-finite density.");
        if (Get(p) == value) return false;
        _overrides[p] = value;
        foreach (Vector3Int c in ChunksSharingSample(p)) _modifiedChunks.Add(c);
        if (value >= 0f)
        {
            Vector3Int row = new Vector3Int(0, p.y, p.z);
            Vector2Int range;
            if (_airXExtents.TryGetValue(row, out range))
                _airXExtents[row] = new Vector2Int(Math.Min(range.x, p.x), Math.Max(range.y, p.x));
            else _airXExtents[row] = new Vector2Int(p.x, p.x);
        }
        return true;
    }

    /// <summary>
    /// A proof of connection to the infinite rock mass, not a search-size shortcut.
    /// Outside all air on this X row there is an unbroken solid ray to infinity.
    /// Valid for THIS solid-rock-minus-room, removal-only generator. A future cave
    /// generator/building tool MUST supply a different support proof.
    /// </summary>
    public bool HasSolidRayToInfinity(Vector3Int p)
    {
        if (Get(p) >= 0f) return false;
        Vector2Int range;
        bool anyAir = _airXExtents.TryGetValue(new Vector3Int(0, p.y, p.z), out range);
        float y = p.y * VoxelSize, z = p.z * VoxelSize;
        if (y >= -4f && y <= 8f && z >= -12f && z <= 12f)
        {
            int lo = Mathf.CeilToInt(-12f / VoxelSize), hi = Mathf.FloorToInt(12f / VoxelSize);
            range = anyAir ? new Vector2Int(Math.Min(range.x, lo), Math.Max(range.y, hi)) : new Vector2Int(lo, hi);
            anyAir = true;
        }
        return !anyAir || p.x < range.x || p.x > range.y;
    }

    public IEnumerable<Vector3Int> ChunksSharingSample(Vector3Int p)
    {
        Vector3Int c = Owner(p);
        int nx = p.x % ChunkSize.x == 0 ? 2 : 1;
        int ny = p.y % ChunkSize.y == 0 ? 2 : 1;
        int nz = p.z % ChunkSize.z == 0 ? 2 : 1;
        for (int x = 0; x < nx; x++)
            for (int y = 0; y < ny; y++)
                for (int z = 0; z < nz; z++)
                    yield return c - new Vector3Int(x, y, z);
    }

    public float[,,] MakeChunk(Vector3Int coord)
    {
        float[,,] result = new float[ChunkSize.x + 1, ChunkSize.y + 1, ChunkSize.z + 1];
        FillChunk(coord, result);
        return result;
    }

    public void FillChunk(Vector3Int coord, float[,,] destination)
    {
        Vector3Int origin = Vector3Int.Scale(coord, ChunkSize);
        for (int x = 0; x <= ChunkSize.x; x++)
            for (int y = 0; y <= ChunkSize.y; y++)
                for (int z = 0; z <= ChunkSize.z; z++)
                    destination[x, y, z] = Get(origin + new Vector3Int(x, y, z));
    }

    public KeyValuePair<Vector3Int, float>[] CopyOverrides()
    {
        var result = new KeyValuePair<Vector3Int, float>[_overrides.Count];
        int i = 0;
        foreach (var entry in _overrides) result[i++] = entry;
        return result;
    }

    public Dictionary<Vector3Int, float[,,]> ExportLegacyChunks()
    {
        var result = new Dictionary<Vector3Int, float[,,]>();
        foreach (Vector3Int c in _modifiedChunks)
            result[c] = MakeChunk(c); // All shared edges are exported from the same values.
        return result;
    }

    public void ImportLegacyChunks(Dictionary<Vector3Int, float[,,]> chunks)
    {
        foreach (var entry in chunks)
        {
            float[,,] d = entry.Value;
            if (d == null || d.GetLength(0) != ChunkSize.x + 1 || d.GetLength(1) != ChunkSize.y + 1 || d.GetLength(2) != ChunkSize.z + 1)
                continue;
            Vector3Int origin = Vector3Int.Scale(entry.Key, ChunkSize);
            for (int x = 0; x <= ChunkSize.x; x++)
                for (int y = 0; y <= ChunkSize.y; y++)
                    for (int z = 0; z <= ChunkSize.z; z++)
                    {
                        Vector3Int p = origin + new Vector3Int(x, y, z);
                        float value = d[x, y, z];
                        // Legacy seam conflicts resolve to the MOST excavated value.
                        // This project has no supported terrain-addition operation.
                        if (!float.IsNaN(value) && !float.IsInfinity(value) && value > Get(p)) Set(p, value);
                    }
        }
    }
}
