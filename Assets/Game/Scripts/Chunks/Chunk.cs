using System.Collections.Generic;
using UnityEngine;

[RequireComponent(typeof(MeshFilter),typeof(MeshRenderer),typeof(MeshCollider))]
public class Chunk : MonoBehaviour
{
    private MeshFilter _meshFilter;
    private MeshRenderer _meshRenderer;
    private MeshCollider _meshCollider;
    private Mesh _mesh;
    [System.NonSerialized] public float[,,] _densities;
    private Vector3Int _size,_globalOrigin;
    private float _voxelSize;
    private readonly List<int> _triangleMaterials=new List<int>();
    private void Awake()
    {
        _meshFilter=GetComponent<MeshFilter>();_meshRenderer=GetComponent<MeshRenderer>();_meshCollider=GetComponent<MeshCollider>();
        _mesh=new Mesh {name="Gobmine terrain",indexFormat=UnityEngine.Rendering.IndexFormat.UInt32};_meshFilter.sharedMesh=_mesh;
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
    public void BuildMeshSync()
    {
        if(_densities==null)return;
        World world=World.Instance;
        var data=TerrainMeshBuilder.Build(_densities,_size,_voxelSize,_globalOrigin,p=>world!=null?world.GetSolidMaterialId(p):1);
        _meshCollider.sharedMesh=null;_mesh.Clear();_mesh.SetVertices(data.Vertices);_triangleMaterials.Clear();
        _mesh.subMeshCount=Mathf.Max(1,data.Triangles.Count);
        var materials=new List<Material>();int submesh=0;
        foreach(var group in data.Triangles)
        {
            _mesh.SetTriangles(group.Value,submesh++);
            materials.Add(world!=null?world.GetMaterialForSurface(group.Key):null);
            for(int i=0;i<group.Value.Count;i+=3)_triangleMaterials.Add(group.Key);
        }
        if(materials.Count==0)materials.Add(world!=null?world.GetVoxelMaterial():null);
        _meshRenderer.sharedMaterials=materials.ToArray();_meshRenderer.enabled=_triangleMaterials.Count>0;
        _mesh.RecalculateNormals();_mesh.RecalculateBounds();_meshFilter.sharedMesh=_mesh;
        if(_triangleMaterials.Count>0)_meshCollider.sharedMesh=_mesh;
    }
}
