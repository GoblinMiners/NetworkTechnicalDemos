using System.Collections.Generic;
using Mirror;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>Client-only batched surface meshes. No fluid particles, rigidbodies or networked cell GameObjects.</summary>
public sealed class GobmineFluidRenderer:MonoBehaviour
{
    private const int RegionCells=4;
    private World _world;
    private Vector3Int _lastCentre=new Vector3Int(int.MaxValue,0,0);
    private readonly Dictionary<Vector3Int,View> _views=new Dictionary<Vector3Int,View>();
    private readonly HashSet<Vector3Int> _wanted=new HashSet<Vector3Int>();
    private readonly HashSet<Vector3Int> _dirty=new HashSet<Vector3Int>();
    private readonly Queue<Vector3Int> _queue=new Queue<Vector3Int>();
    private readonly Queue<View> _pool=new Queue<View>();
    private readonly List<Vector3> _vertices=new List<Vector3>();
    private readonly List<int> _water=new List<int>(),_lava=new List<int>();
    private readonly List<View> _allViews=new List<View>();
    private sealed class View { public GameObject Object; public Mesh Mesh; public MeshRenderer Renderer; }
    public void Initialize(World world){_world=world;}
    private static Vector3Int Region(Vector3Int p)=>new Vector3Int(TerrainDensityStore.FloorDiv(p.x,RegionCells),TerrainDensityStore.FloorDiv(p.y,RegionCells),TerrainDensityStore.FloorDiv(p.z,RegionCells));
    public void MarkCellDirty(Vector3Int cell)
    {
        Mark(Region(cell));
        foreach(var d in TerrainStructuralSolver.Directions)Mark(Region(cell+d));
    }
    private void Mark(Vector3Int region){if(_wanted.Contains(region) && _dirty.Add(region))_queue.Enqueue(region);}
    private void Update()
    {
        if(_world==null || !_world.IsReady || !_world.ExpeditionEnabled || NetworkClient.localPlayer==null)return;
        var s=_world.ExpeditionProfile;float metre=s.fluidCellMetres;
        Vector3Int centre=Region(Vector3Int.FloorToInt(NetworkClient.localPlayer.transform.position/metre));
        if(centre!=_lastCentre){_lastCentre=centre;Refresh(centre);}
        int builds=Mathf.Max(1,s.fluidRegionsPerFrame);
        while(builds-->0 && _queue.Count>0)
        {
            Vector3Int region=_queue.Dequeue();_dirty.Remove(region);
            if(_wanted.Contains(region))Build(region);
        }
    }
    private void Refresh(Vector3Int centre)
    {
        var s=_world.ExpeditionProfile;
        int radius=Mathf.CeilToInt((s.fluidActiveRadiusMetres+2f)/(s.fluidCellMetres*RegionCells));
        var regions=new List<Vector3Int>();
        for(int x=-radius;x<=radius;x++)for(int y=-radius;y<=radius;y++)for(int z=-radius;z<=radius;z++)
        {if(x*x+y*y+z*z<=(radius+1)*(radius+1))regions.Add(centre+new Vector3Int(x,y,z));}
        regions.Sort((a,b)=>(a-centre).sqrMagnitude.CompareTo((b-centre).sqrMagnitude));
        if(regions.Count>s.maxRenderedFluidRegions)regions.RemoveRange(s.maxRenderedFluidRegions,regions.Count-s.maxRenderedFluidRegions);
        _wanted.Clear();foreach(var r in regions)_wanted.Add(r);
        var remove=new List<Vector3Int>();
        foreach(var e in _views)if(!_wanted.Contains(e.Key)){e.Value.Object.SetActive(false);_pool.Enqueue(e.Value);remove.Add(e.Key);}
        foreach(var r in remove)_views.Remove(r);
        // Reprioritize both missing and changed regions near the player.
        _queue.Clear();_dirty.Clear();
        foreach(var r in regions)Mark(r);
    }
    private void Build(Vector3Int region)
    {
        _vertices.Clear();_water.Clear();_lava.Clear();
        float size=_world.ExpeditionProfile.fluidCellMetres;Vector3Int origin=region*RegionCells;
        for(int x=0;x<RegionCells;x++)for(int y=0;y<RegionCells;y++)for(int z=0;z<RegionCells;z++)
        {
            Vector3Int cell=origin+new Vector3Int(x,y,z);var state=_world.FluidCell(cell);
            if(state.Amount<=0)continue;
            List<int> triangles=state.Kind==GobmineFluidKind.Water?_water:_lava;
            float h=(float)state.Amount/GobmineFluidStore.Capacity;
            Vector3 p=new Vector3(x,y,z)*size;
            var above=_world.FluidCell(cell+Vector3Int.up);
            if(h<0.999f || above.Kind!=state.Kind || above.Amount==0)
                Quad(p+new Vector3(0,h,0)*size,p+new Vector3(0,h,1)*size,p+new Vector3(1,h,1)*size,p+new Vector3(1,h,0)*size,triangles);
            var below=_world.FluidCell(cell+Vector3Int.down);
            if(below.Kind!=state.Kind || below.Amount<GobmineFluidStore.Capacity)
                Quad(p,p+Vector3.right*size,p+new Vector3(1,0,1)*size,p+Vector3.forward*size,triangles);
            Side(cell,Vector3Int.left,state,p,size,h,0,triangles);
            Side(cell,Vector3Int.right,state,p,size,h,1,triangles);
            Side(cell,Vector3Int.back,state,p,size,h,2,triangles);
            Side(cell,Vector3Int.forward,state,p,size,h,3,triangles);
        }
        View view;
        if(_vertices.Count==0)
        {
            if(_views.TryGetValue(region,out view)){view.Object.SetActive(false);_pool.Enqueue(view);_views.Remove(region);}
            return;
        }
        if(!_views.TryGetValue(region,out view))
        {
            view=_pool.Count>0?_pool.Dequeue():NewView();_views[region]=view;
        }
        view.Object.transform.localPosition=(Vector3)origin*size;view.Object.SetActive(true);
        view.Mesh.Clear();view.Mesh.SetVertices(_vertices);view.Mesh.subMeshCount=2;
        view.Mesh.SetTriangles(_water,0);view.Mesh.SetTriangles(_lava,1);view.Mesh.RecalculateNormals();view.Mesh.RecalculateBounds();
    }
    private void Side(Vector3Int cell,Vector3Int direction,GobmineFluidCell state,Vector3 p,float size,float height,int side,List<int> triangles)
    {
        var neighbour=_world.FluidCell(cell+direction);
        float low=neighbour.Kind==state.Kind?(float)neighbour.Amount/GobmineFluidStore.Capacity:0f;
        if(low>=height)return;
        if(side==0)Quad(p+new Vector3(0,low,0)*size,p+new Vector3(0,low,1)*size,p+new Vector3(0,height,1)*size,p+new Vector3(0,height,0)*size,triangles);
        if(side==1)Quad(p+new Vector3(1,low,1)*size,p+new Vector3(1,low,0)*size,p+new Vector3(1,height,0)*size,p+new Vector3(1,height,1)*size,triangles);
        if(side==2)Quad(p+new Vector3(1,low,0)*size,p+new Vector3(0,low,0)*size,p+new Vector3(0,height,0)*size,p+new Vector3(1,height,0)*size,triangles);
        if(side==3)Quad(p+new Vector3(0,low,1)*size,p+new Vector3(1,low,1)*size,p+new Vector3(1,height,1)*size,p+new Vector3(0,height,1)*size,triangles);
    }
    private void Quad(Vector3 a,Vector3 b,Vector3 c,Vector3 d,List<int> triangles)
    {
        int i=_vertices.Count;_vertices.Add(a);_vertices.Add(b);_vertices.Add(c);_vertices.Add(d);
        triangles.Add(i);triangles.Add(i+1);triangles.Add(i+2);triangles.Add(i);triangles.Add(i+2);triangles.Add(i+3);
    }
    private View NewView()
    {
        var go=new GameObject("Fluid region");go.transform.SetParent(transform,false);
        var filter=go.AddComponent<MeshFilter>();var renderer=go.AddComponent<MeshRenderer>();
        var mesh=new Mesh{name="Gobmine fluid surface",indexFormat=IndexFormat.UInt32};mesh.MarkDynamic();filter.sharedMesh=mesh;
        renderer.sharedMaterials=new[]{_world.ExpeditionProfile.waterMaterial,_world.ExpeditionProfile.lavaMaterial};
        renderer.shadowCastingMode=ShadowCastingMode.Off;renderer.receiveShadows=false;
        var v=new View{Object=go,Mesh=mesh,Renderer=renderer};_allViews.Add(v);return v;
    }
    private void OnDestroy(){foreach(var v in _allViews)if(v.Mesh!=null)Destroy(v.Mesh);}
}
