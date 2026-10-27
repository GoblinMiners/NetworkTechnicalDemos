using System.Collections.Generic;
using UnityEngine;

[RequireComponent(typeof(MeshFilter),typeof(MeshRenderer),typeof(MeshCollider))]
public class Chunk : MonoBehaviour
{
    private MeshFilter _meshFilter;
    private MeshRenderer _meshRenderer;
    private MeshCollider _meshCollider;
    private Mesh _mesh;
    private bool _hasBuiltMesh;
    // Empty (fully air or solid) chunks correctly have no surface mesh/collider.
    // A null mesh is only a fault when this chunk actually contains triangles.
    public bool CollisionReady => _hasBuiltMesh && gameObject.activeInHierarchy &&
        _meshCollider != null && _meshCollider.enabled && !_meshCollider.isTrigger && !_meshCollider.convex &&
        (_triangleMaterials.Count == 0 || _meshCollider.sharedMesh != null);
    [System.NonSerialized] public float[,,] _densities;
    private Vector3Int _size,_globalOrigin;
    private float _voxelSize;
    private readonly List<int> _triangleMaterials=new List<int>();
    private readonly TerrainMeshBuilder.Result _meshData=new TerrainMeshBuilder.Result();
    private readonly List<Material> _materials=new List<Material>();
    private System.Func<Vector3Int,int> _materialResolver;
    private void Awake()
    {
        _meshFilter=GetComponent<MeshFilter>();_meshRenderer=GetComponent<MeshRenderer>();_meshCollider=GetComponent<MeshCollider>();
        _materialResolver=ResolveMaterial;
        _mesh=new Mesh {name="Gobmine terrain",indexFormat=UnityEngine.Rendering.IndexFormat.UInt32};_meshFilter.sharedMesh=_mesh;
        _mesh.MarkDynamic();
    }
    public void Init(Vector3Int size,float voxelSize,Vector3 worldPos,float noiseScale,int seed,float[,,] loadedData=null)
    {
        _size=size;_voxelSize=voxelSize;_globalOrigin=Vector3Int.RoundToInt(worldPos/voxelSize);
        _densities=loadedData??new float[size.x+1,size.y+1,size.z+1];BuildMeshSync();
    }
    private void OnDestroy(){if(_mesh!=null)Destroy(_mesh);}
    public float[,,] GetDensities()=>_densities;
    public int GetHitMaterialId(int triangleIndex)=>triangleIndex>=0 && triangleIndex<_triangleMaterials.Count?_triangleMaterials[triangleIndex]:0;
    public void EditTerrain(Vector3 hit,float radius,float amount)
    {if(amount==0){BuildMeshSync();return;}if(World.Instance!=null)World.Instance.RequestModifyTerrain(hit,radius,amount);}
    private int ResolveMaterial(Vector3Int cell) => World.Instance!=null?World.Instance.GetSolidMaterialId(cell):1;
    public void BuildMeshSync()
    {
        _hasBuiltMesh = false;
        if(_densities==null)return;
        World world=World.Instance;
        var data=TerrainMeshBuilder.Build(_densities,_size,_voxelSize,_globalOrigin,_materialResolver,_meshData);
        _meshCollider.sharedMesh=null;_mesh.Clear();_mesh.SetVertices(data.Vertices);_triangleMaterials.Clear();
        _mesh.subMeshCount=Mathf.Max(1,data.Triangles.Count);
        var materials=_materials; materials.Clear();int submesh=0;
        foreach(var group in data.Triangles)
        {
            _mesh.SetTriangles(group.Value,submesh++);
            materials.Add(world!=null?world.GetMaterialForSurface(group.Key):null);
            for(int i=0;i<group.Value.Count;i+=3)_triangleMaterials.Add(group.Key);
        }
        if(materials.Count==0)materials.Add(world!=null?world.GetVoxelMaterial():null);
        _meshRenderer.SetSharedMaterials(materials);_meshRenderer.enabled=_triangleMaterials.Count>0;
        _mesh.RecalculateNormals();_mesh.RecalculateBounds();_meshFilter.sharedMesh=_mesh;
        if(_triangleMaterials.Count>0)_meshCollider.sharedMesh=_mesh;
        _hasBuiltMesh = true;
        if (world != null) world.RefreshRadiationSurface(_globalOrigin, data);
    }
}
