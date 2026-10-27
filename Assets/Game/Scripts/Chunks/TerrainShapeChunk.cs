using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>Immutable density samples captured before a disconnected solid is erased.</summary>
[Serializable]
public sealed class TerrainShapeRecord
{
    public int Id;
    public bool Ceiling;
    public Vector3 Position;
    public Quaternion Rotation = Quaternion.identity;
    public Vector3 Velocity;
    public Vector3 AngularVelocity;
    public readonly List<TerrainShapeSample> Samples = new List<TerrainShapeSample>();
}

[Serializable]
public struct TerrainShapeSample
{
    public Vector3Int Grid;
    public float Density;
    public TerrainShapeSample(Vector3Int grid, float density) { Grid = grid; Density = density; }
}

/// <summary>
/// Extracts the EXACT sampled density shape, with air outside the disconnected component.
/// Shared marching-cubes tables and interpolation agree with ordinary terrain meshes.
/// Work is spread over frames; only the final Mesh allocation runs in one step.
/// </summary>
public sealed class TerrainShapeMeshJob
{
    private readonly Dictionary<Vector3Int, float> _solid;
    private readonly Dictionary<Vector3Int, float> _air;
    private readonly List<Vector3Int> _cells;
    private readonly Dictionary<Vector3, int> _vertexIndices = new Dictionary<Vector3, int>();
    private readonly List<Vector3> _vertices = new List<Vector3>();
    private readonly float[] _cornerDensity = new float[8];
    private readonly SortedDictionary<int, List<int>> _triangles = new SortedDictionary<int, List<int>>();
    private readonly Func<Vector3Int, int> _materialAt;
    private readonly float _spacing;
    public readonly Vector3 Pivot;
    public int SampleCount => _solid.Count;
    public int BuiltCells { get; private set; }
    public bool Complete => BuiltCells >= _cells.Count;
    public float ApproximateVolume => _solid.Count * _spacing * _spacing * _spacing;

    public TerrainShapeMeshJob(TerrainShapeRecord source, float spacing, Func<Vector3Int, int> materialAt)
    {
        if (source == null || spacing <= 0f || materialAt == null) throw new ArgumentException("Invalid terrain shape mesh inputs");
        _spacing = spacing;
        _materialAt = materialAt;
        _solid = new Dictionary<Vector3Int, float>(source.Samples.Count);
        _air = new Dictionary<Vector3Int, float>();
        Vector3Int lo = new Vector3Int(int.MaxValue, int.MaxValue, int.MaxValue);
        Vector3Int hi = new Vector3Int(int.MinValue, int.MinValue, int.MinValue);
        foreach (TerrainShapeSample sample in source.Samples)
        {
            if (sample.Density >= 0f) { _air[sample.Grid] = sample.Density; continue; }
            _solid[sample.Grid] = sample.Density;
            lo = Vector3Int.Min(lo, sample.Grid);
            hi = Vector3Int.Max(hi, sample.Grid);
        }
        if (_solid.Count == 0) { Pivot = Vector3.zero; _cells = new List<Vector3Int>(); return; }
        Pivot = ((Vector3)lo + (Vector3)hi) * (0.5f * spacing);
        var cells = new HashSet<Vector3Int>();
        foreach (Vector3Int p in _solid.Keys)
            for (int x = 0; x <= 1; x++) for (int y = 0; y <= 1; y++) for (int z = 0; z <= 1; z++)
                cells.Add(p - new Vector3Int(x,y,z));
        _cells = new List<Vector3Int>(cells);
        _cells.Sort((a,b) => { int d = a.x.CompareTo(b.x); if (d != 0) return d;
            d = a.y.CompareTo(b.y); return d != 0 ? d : a.z.CompareTo(b.z); });
    }

    public void Step(int maxCells)
    {
        int end = Mathf.Min(_cells.Count, BuiltCells + Mathf.Max(1, maxCells));
        for (; BuiltCells < end; BuiltCells++)
        {
            Vector3Int cell = _cells[BuiltCells];
            float[] density = _cornerDensity;
            int cube = 0;
            for (int i = 0; i < 8; i++)
            {
                float value;
                Vector3Int grid = cell + MarchingTable.Corners[i];
                density[i] = _solid.TryGetValue(grid,out value) || _air.TryGetValue(grid,out value) ? value : 1f;
                if (density[i] < 0f) cube |= 1 << i;
            }
            if (MarchingTable.Triangles[cube,0] == -1) continue;
            int material = Mathf.Max(1, _materialAt(cell));
            List<int> group;
            if (!_triangles.TryGetValue(material, out group)) { group = new List<int>(); _triangles.Add(material, group); }
            for (int i = 0; MarchingTable.Triangles[cube,i] != -1; i += 3)
            {
                Vector3 a = EdgePoint(cell, MarchingTable.Triangles[cube,i], density);
                Vector3 b = EdgePoint(cell, MarchingTable.Triangles[cube,i+1], density);
                Vector3 c = EdgePoint(cell, MarchingTable.Triangles[cube,i+2], density);
                if (Vector3.Cross(b-a,c-a).sqrMagnitude < 1e-14f) continue;
                group.Add(Vertex(c)); group.Add(Vertex(b)); group.Add(Vertex(a));
            }
        }
    }

    private int Vertex(Vector3 point)
    {
        int index;
        if (_vertexIndices.TryGetValue(point, out index)) return index;
        index = _vertices.Count; _vertices.Add(point); _vertexIndices.Add(point,index); return index;
    }

    private Vector3 EdgePoint(Vector3Int cell, int edge, float[] densities)
    {
        Vector3 a = MarchingTable.Edges[edge,0], b = MarchingTable.Edges[edge,1];
        Vector3Int ai = cell + Vector3Int.RoundToInt(a), bi = cell + Vector3Int.RoundToInt(b);
        float da = 1f, db = 1f;
        for (int i = 0; i < 8; i++)
        {
            if (MarchingTable.Corners[i] == Vector3Int.RoundToInt(a)) da = densities[i];
            if (MarchingTable.Corners[i] == Vector3Int.RoundToInt(b)) db = densities[i];
        }
        float t = Mathf.Abs(da) < 0.00001f ? 0f : Mathf.Abs(db) < 0.00001f ? 1f :
            Mathf.Abs(da-db) < 0.00001f ? 0.5f : Mathf.Clamp01(da / (da-db));
        return (Vector3.Lerp((Vector3)ai,(Vector3)bi,t) * _spacing) - Pivot;
    }

    public Mesh CreateMesh(List<int> materialIds)
    {
        if (!Complete) throw new InvalidOperationException("Mesh building has not completed");
        var mesh = new Mesh { name = "Gobmine detached density surface", indexFormat = IndexFormat.UInt32 };
        mesh.SetVertices(_vertices);
        mesh.subMeshCount = Mathf.Max(1,_triangles.Count);
        int index = 0;
        foreach (var group in _triangles)
        {
            mesh.SetTriangles(group.Value,index++); materialIds.Add(group.Key);
        }
        mesh.RecalculateNormals(); mesh.RecalculateBounds();
        return mesh;
    }

    /// <summary>
    /// Build <= maxBoxes compound colliders that tightly bound the ACTUAL occupied
    /// density samples in each coarse group. The old version expanded a single
    /// occupied sample to an entire (possibly huge) grouping tile: such inflated
    /// boxes could intersect nearby tunnel walls and stop detached pieces falling.
    /// The visual marching-cubes mesh is unchanged. Colliders remain approximate.
    /// </summary>
    public List<Bounds> CreateCompoundBoxes(int maxBoxes)
    {
        var result = new List<Bounds>();
        if (_solid.Count == 0) return result;
        int step = 1, limit = Mathf.Max(8,maxBoxes);
        Dictionary<Vector3Int,Bounds> groups = null;
        for (int attempt = 0; attempt < 24; attempt++)
        {
            groups = new Dictionary<Vector3Int,Bounds>();
            foreach (Vector3Int sample in _solid.Keys)
            {
                Vector3Int group = new Vector3Int(TerrainDensityStore.FloorDiv(sample.x,step),
                    TerrainDensityStore.FloorDiv(sample.y,step), TerrainDensityStore.FloorDiv(sample.z,step));
                // Slightly inset so the proxy is less likely to start embedded in
                // neighbouring stone. At coarse resolutions include only the true
                // occupied sample extent, NOT the entire grouping cell volume.
                Bounds occupied = new Bounds((Vector3)sample * _spacing - Pivot,
                    Vector3.one * (_spacing * 0.88f));
                Bounds current;
                if (groups.TryGetValue(group,out current))
                {
                    current.Encapsulate(occupied);
                    groups[group] = current;
                }
                else groups.Add(group,occupied);
            }
            if (groups.Count <= limit || step >= 1048576) break;
            step *= 2;
        }
        var keys = new List<Vector3Int>(groups.Keys);
        keys.Sort((a,b) => { int n = a.x.CompareTo(b.x); if (n != 0) return n;
            n = a.y.CompareTo(b.y); return n != 0 ? n : a.z.CompareTo(b.z); });
        foreach (Vector3Int group in keys) result.Add(groups[group]);
        return result;
    }

}

/// <summary>
/// A single rigidbody holds an entire disconnected object, however complicated the visual mesh.
/// Proxy BoxColliders bound CPU/PhysX cost; they are NOT an exact concave collision surface.
/// Cosmetic mining rubble continues to use DebrisPool separately.
/// </summary>
public sealed class TerrainShapeChunk : MonoBehaviour
{
    public int ShapeId { get; private set; }
    public bool IsCeiling { get; private set; }
    public float Volume { get; private set; }
    public Vector3 Pivot { get; private set; }
    public Rigidbody Body { get; private set; }
    public bool ServerOwned { get; private set; }
    public float BornAt { get; private set; }
    public readonly List<BoxCollider> Colliders = new List<BoxCollider>();
    public float SimulatedVolume => Volume;
    public Vector3 LastVelocity { get; private set; }
    private Mesh _mesh;
    private World _world;

    public void Configure(TerrainShapeRecord source, TerrainShapeMeshJob job, World world,
        bool server, int maxBoxes, float kilogramsPerCubicMetre)
    {
        ShapeId = source.Id; IsCeiling = source.Ceiling; _world = world; ServerOwned = server;
        BornAt = Time.time; Pivot = job.Pivot; Volume = Mathf.Max(0.0001f,job.ApproximateVolume);
        gameObject.name = (IsCeiling ? "CeilingChunk_" : "DetachedShape_") + ShapeId;
        transform.SetPositionAndRotation(source.Position,source.Rotation);
        gameObject.layer = world.terrainLayer;
        var filter = gameObject.AddComponent<MeshFilter>();
        var renderer = gameObject.AddComponent<MeshRenderer>();
        var ids = new List<int>();
        _mesh = job.CreateMesh(ids); filter.sharedMesh = _mesh;
        var materials = new List<Material>();
        foreach (int id in ids) materials.Add(world.GetMaterialForSurface(id));
        if (materials.Count == 0) materials.Add(world.GetVoxelMaterial());
        renderer.SetSharedMaterials(materials);
        foreach (Bounds bounds in job.CreateCompoundBoxes(maxBoxes))
        {
            BoxCollider box = gameObject.AddComponent<BoxCollider>();
            box.center = bounds.center; box.size = bounds.size;
            Colliders.Add(box);
        }
        Body = gameObject.AddComponent<Rigidbody>();
        Body.mass = Mathf.Clamp(Volume * kilogramsPerCubicMetre,1f,20000f);
        Body.interpolation = RigidbodyInterpolation.Interpolate;
        Body.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
        Body.isKinematic = !server; Body.useGravity = server;
        if (server)
        {
            DebrisPool.SetLinearVelocity(Body,source.Velocity);
            Body.angularVelocity = source.AngularVelocity;
            Body.WakeUp();
        }
    }

    private void FixedUpdate()
    {
        if (!ServerOwned || Body == null || _world == null) return;
        Vector3 velocity = GetVelocity();
        _world.ServerTickShapeCollision(this, LastVelocity.sqrMagnitude > velocity.sqrMagnitude ? LastVelocity : velocity);
        LastVelocity = velocity;
    }
    public Vector3 GetVelocity()
    {
#if UNITY_6000_0_OR_NEWER
        return Body != null ? Body.linearVelocity : Vector3.zero;
#else
        return Body != null ? Body.velocity : Vector3.zero;
#endif
    }
    public void SetPose(Vector3 position, Quaternion rotation)
    {
        if (ServerOwned) return;
        transform.SetPositionAndRotation(position,rotation);
    }
    private void OnDestroy() { if (_mesh != null) Destroy(_mesh); }
}
