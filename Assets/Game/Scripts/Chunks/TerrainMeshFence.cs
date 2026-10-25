using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// A rubble request depends on the mesh version for ITS edit, not on a chunk
/// staying clean forever. Later removal-only edits cannot invalidate that version.
/// </summary>
public sealed class TerrainMeshFence
{
    private readonly Dictionary<Vector3Int, long> _wanted = new Dictionary<Vector3Int, long>();
    private readonly Dictionary<Vector3Int, long> _built = new Dictionary<Vector3Int, long>();
    private long _clock;
    public void Clear() { _wanted.Clear(); _built.Clear(); _clock = 0; }
    public void MarkDirty(Vector3Int chunk) { _wanted[chunk] = ++_clock; }
    public long Capture(Vector3Int chunk) { long value; return _wanted.TryGetValue(chunk, out value) ? value : 0; }
    public void MarkBuilt(Vector3Int chunk) { _built[chunk] = Capture(chunk); }
    public bool IsSatisfied(Vector3Int chunk, long version)
    {
        long value;
        return version <= 0 || (_built.TryGetValue(chunk, out value) && value >= version);
    }
}
