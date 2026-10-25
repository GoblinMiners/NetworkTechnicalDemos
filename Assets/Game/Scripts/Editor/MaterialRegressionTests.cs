#if UNITY_EDITOR
using System;
using System.IO;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

/// <summary>Runs the actual C# algorithms inside Unity, without entering play mode.</summary>
public static class MaterialRegressionTests
{
    private static int _passed,_failed;
    [MenuItem("Tools/Gobmine/Materials/Run All Regression Tests")]
    public static void RunAll()
    {
        if(EditorApplication.isPlaying)throw new InvalidOperationException("Stop play mode before running regression tests.");
        DebrisRegressionTests.RunAll();
        RunMaterials();
    }
    [MenuItem("Tools/Gobmine/Materials/Run Material Regression Tests")]
    public static void RunMaterials()
    {
        if(EditorApplication.isPlaying)throw new InvalidOperationException("Stop play mode before running regression tests.");
        _passed=_failed=0;
        Check("No deposit rules returns base stone",NoRules);
        Check("Same seed and coordinate are stable",Deterministic);
        Check("Query order does not change ore",QueryOrder);
        Check("Different seeds change deposits",DifferentSeeds);
        Check("Zero occurrence creates no ore",ZeroChance);
        Check("Ore respects vertical range",VerticalRange);
        Check("Later ore rule has overlap priority",OverlapPriority);
        Check("Invalid deposit radius rejected",InvalidRadius);
        Check("Fully empty cell is Air",()=>Assert(TerrainQuery.CellState(Vector3Int.zero,p=>1f)==TerrainCellState.Air,"air"));
        Check("Fully solid cell is Solid",()=>Assert(TerrainQuery.CellState(Vector3Int.zero,p=>-1f)==TerrainCellState.Solid,"solid"));
        Check("Partly excavated cell retains Surface state",()=>Assert(TerrainQuery.CellState(Vector3Int.zero,p=>p.x==0?-1f:1f)==TerrainCellState.Surface,"surface"));
        Check("Point density interpolates the field",PointDensity);
        Check("One mesh groups stone and gold separately",TwoMaterialMesh);
        Check("Buried ore has no internal rendered faces",BuriedOre);
        Check("Air produces no mesh triangles",AirMesh);
        Check("Mesh material resolver receives global negative cells",GlobalMeshCells);
        Check("Same material resource buckets merge",MergePiles);
        Check("Different materials remain separate",SeparatePiles);
        Check("Negative and positive resource buckets differ",NegativeBuckets);
        Check("Moving a pickup does not lose its value",MovingPile);
        Check("A resource record can be consumed only once",SingleConsumption);
        Check("Invalid resource amounts are rejected",InvalidResources);
        Check("Resource snapshots preserve fractional amounts",ResourceRoundTrip);
        Check("One save contains terrain, seed, rules and resource value",SaveRoundTrip);
        Check("Checksum rejects a corrupted save",CorruptSave);
        Check("Truncated save is rejected",TruncatedSave);
        Check("Empty world snapshot round trips",EmptySave);
        Check("Save replacement retains a recoverable backup",BackupRecovery);
        Check("Default catalogue IDs and assets are valid",Catalogue);
        Check("Rule signature changes when rewards change",SignatureChanges);
        Check("Duplicate material IDs fail closed",DuplicateIDs);
        Debug.Log($"[Gobmine Material Tests] {_passed} passed; {_failed} failed. These do not replace play-mode or multiplayer tests.");
        if(_failed>0)throw new Exception("Material tests failed. Copy the full Console output.");
    }
    private static void Check(string name,Action test)
    {try{test();_passed++;Debug.Log("PASS: "+name);}catch(Exception ex){_failed++;Debug.LogError("FAIL: "+name+"\n"+ex);}}
    private static void Assert(bool condition,string message){if(!condition)throw new Exception(message);}
    private static void Near(double a,double b,double tolerance=1e-6){Assert(Math.Abs(a-b)<=tolerance,$"Expected {b}, got {a}");}
    private static void Throws(Action action){try{action();}catch(Exception){return;}throw new Exception("Expected an exception.");}
    private static TerrainMaterialGenerator.Rule Rule(int id=2,int chance=1000)=>new TerrainMaterialGenerator.Rule {Id=id,Grid=8,Chance=chance,MinRadius=2,MaxRadius=4,MinY=-1000,MaxY=1000};
    private static TerrainMaterialGenerator Generator(int seed=1729)=>new TerrainMaterialGenerator(seed,1,new[]{Rule()});
    private static void NoRules(){var g=new TerrainMaterialGenerator(1,1,null);for(int x=-20;x<=20;x++)Assert(g.GetSolidMaterial(new Vector3Int(x,-x,x+4))==1,"unexpected ore");}
    private static void Deterministic()
    {var a=Generator();var b=Generator();for(int x=-25;x<25;x++)for(int z=-8;z<8;z++)Assert(a.GetSolidMaterial(new Vector3Int(x,-3,z))==b.GetSolidMaterial(new Vector3Int(x,-3,z)),"nondeterministic");}
    private static void QueryOrder()
    {var g=Generator();var answers=new Dictionary<Vector3Int,int>();for(int x=-32;x<=32;x++) {var p=new Vector3Int(x,-16,16);answers[p]=g.GetSolidMaterial(p);}for(int x=32;x>=-32;x--){var p=new Vector3Int(x,-16,16);Assert(answers[p]==g.GetSolidMaterial(p),"order dependence");}}
    private static void DifferentSeeds()
    {var a=Generator(1);var b=Generator(2);int changes=0;for(int x=-24;x<24;x++)for(int z=-8;z<8;z++)if(a.GetSolidMaterial(new Vector3Int(x,0,z))!=b.GetSolidMaterial(new Vector3Int(x,0,z)))changes++;Assert(changes>0,"seed ignored");}
    private static void ZeroChance()
    {var g=new TerrainMaterialGenerator(123,1,new[]{Rule(2,0)});for(int x=-24;x<=24;x++)Assert(g.GetSolidMaterial(new Vector3Int(x,3,-x))==1,"zero chance ore");}
    private static void VerticalRange()
    {var r=Rule();r.MinY=0;r.MaxY=0;var g=new TerrainMaterialGenerator(17,1,new[]{r});bool found=false;for(int x=-16;x<16;x++)for(int z=-16;z<16;z++){Assert(g.GetSolidMaterial(new Vector3Int(x,-1,z))==1,"ore below range");Assert(g.GetSolidMaterial(new Vector3Int(x,1,z))==1,"ore above range");found|=g.GetSolidMaterial(new Vector3Int(x,0,z))==2;}Assert(found,"no ore in valid range");}
    private static void OverlapPriority()
    {var a=new TerrainMaterialGenerator(12,1,new[]{Rule(2)});var b=new TerrainMaterialGenerator(12,1,new[]{Rule(4)});var both=new TerrainMaterialGenerator(12,1,new[]{Rule(2),Rule(4)});bool found=false;for(int x=-12;x<12;x++)for(int z=-12;z<12;z++){var p=new Vector3Int(x,0,z);if(a.GetSolidMaterial(p)==2 && b.GetSolidMaterial(p)==4){found=true;Assert(both.GetSolidMaterial(p)==4,"priority reversed");}}Assert(found,"test fixture has no overlap");}
    private static void InvalidRadius(){var r=Rule();r.MaxRadius=5;Throws(()=>new TerrainMaterialGenerator(1,1,new[]{r}));}
    private static void PointDensity(){Near(TerrainQuery.SampleDensity(new Vector3(0.25f,0.4f,0.7f),p=>p.x==0?-1f:1f),-0.5);}
    private static float[,,] Plane(int x=2)
    {var d=new float[x+1,2,2];for(int i=0;i<=x;i++)for(int y=0;y<2;y++)for(int z=0;z<2;z++)d[i,y,z]=y==0?-1:1;return d;}
    private static void TwoMaterialMesh()
    {var m=TerrainMeshBuilder.Build(Plane(),new Vector3Int(2,1,1),1,Vector3Int.zero,p=>p.x==0?1:4);Assert(m.Triangles.Count==2,"one material swallowed the other");Assert(m.Triangles[1].Count==6 && m.Triangles[4].Count==6,"wrong triangle assignment");foreach(var v in m.Vertices)Near(v.y,0.5);}
    private static void BuriedOre()
    {var d=new float[3,2,2];for(int x=0;x<3;x++)for(int y=0;y<2;y++)for(int z=0;z<2;z++)d[x,y,z]=-1;var m=TerrainMeshBuilder.Build(d,new Vector3Int(2,1,1),1,Vector3Int.zero,p=>p.x==0?1:4);Assert(m.Vertices.Count==0,"internal material boundary rendered");}
    private static void AirMesh()
    {var d=new float[2,2,2];var m=TerrainMeshBuilder.Build(d,Vector3Int.one,1,Vector3Int.zero,p=>0);Assert(m.Vertices.Count==0,"air mesh");}
    private static void GlobalMeshCells()
    {var cells=new HashSet<Vector3Int>();TerrainMeshBuilder.Build(Plane(),new Vector3Int(2,1,1),0.25f,new Vector3Int(-17,8,16),p=>{cells.Add(p);return 1;});Assert(cells.SetEquals(new[]{new Vector3Int(-17,8,16),new Vector3Int(-16,8,16)}),"material coordinates are chunk-local");}
    private static void MergePiles()
    {var s=new ResourcePileStore();long id=s.Add(2,2,0.125,Vector3.zero);long second=s.Add(2,2,0.375,Vector3.one*0.2f);ResourcePileStore.Pile p;Assert(id==second && s.Count==1 && s.TryGet(id,out p),"not merged");s.TryGet(id,out p);Near(p.Units,0.5);}
    private static void SeparatePiles(){var s=new ResourcePileStore();s.Add(2,2,1,Vector3.zero);s.Add(4,4,1,Vector3.zero);Assert(s.Count==2,"materials merged");}
    private static void NegativeBuckets(){var s=new ResourcePileStore();s.Add(2,2,1,new Vector3(-0.1f,0,0));s.Add(2,2,1,new Vector3(0.1f,0,0));Assert(s.Count==2,"negative coordinate rounded towards zero");}
    private static void MovingPile()
    {var s=new ResourcePileStore();long id=s.Add(4,4,1,Vector3.zero);s.UpdatePosition(id,new Vector3(0,-10,0));Assert(s.Add(4,4,0.5,Vector3.zero)==id,"original bucket lost");ResourcePileStore.Pile p;s.TryGet(id,out p);Near(p.Units,1.5);Near(p.Position.y,-10);s.Remove(id,out p);Assert(s.Add(4,4,1,Vector3.zero)!=id && s.Count==1,"stale bucket after pickup");}
    private static void SingleConsumption(){var s=new ResourcePileStore();long id=s.Add(2,2,3,Vector3.zero);ResourcePileStore.Pile p;Assert(s.Remove(id,out p),"first collection failed");Assert(!s.Remove(id,out p),"double collection");}
    private static void InvalidResources(){var s=new ResourcePileStore();Throws(()=>s.Add(2,2,double.NaN,Vector3.zero));Throws(()=>s.Add(2,2,-1,Vector3.zero));Throws(()=>s.Add(0,2,1,Vector3.zero));}
    private static void ResourceRoundTrip(){var s=new ResourcePileStore();s.Add(2,2,0.125,new Vector3(-1,0,0));s.Add(4,4,1.875,Vector3.one);var copy=new ResourcePileStore();copy.Import(s.Copy());double total=0;foreach(var p in copy.All)total+=p.Units;Near(total,2);Assert(copy.Count==2,"snapshot lost a material");}
    private static MaterialWorldPersistence.Snapshot Snapshot()
    {var s=new MaterialWorldPersistence.Snapshot {Seed=1729,ChunkSize=new Vector3Int(16,16,16),VoxelSize=0.25f,RulesSignature=new string('A',64)};s.Density.Add(new MaterialWorldPersistence.DensityPoint {Position=new Vector3Int(-16,-4,7),Value=1.25f});s.Resources.Add(new ResourcePileStore.Pile {MaterialId=4,ItemId=4,Units=0.125,Position=new Vector3(1,-3,2)});return s;}
    private static void SaveRoundTrip(){var a=Snapshot();var b=MaterialWorldPersistence.Decode(MaterialWorldPersistence.Encode(a));Assert(b.Seed==a.Seed && b.ChunkSize==a.ChunkSize && b.RulesSignature==a.RulesSignature,"metadata lost");Assert(b.Density[0].Position==a.Density[0].Position,"density coordinate lost");Near(b.Resources[0].Units,0.125);Near(b.Density[0].Value,1.25);}
    private static void CorruptSave(){var b=MaterialWorldPersistence.Encode(Snapshot());b[30]^=1;Throws(()=>MaterialWorldPersistence.Decode(b));}
    private static void TruncatedSave(){Throws(()=>MaterialWorldPersistence.Decode(new byte[10]));}
    private static void EmptySave(){var a=Snapshot();a.Density.Clear();a.Resources.Clear();var b=MaterialWorldPersistence.Decode(MaterialWorldPersistence.Encode(a));Assert(b.Density.Count==0 && b.Resources.Count==0,"empty save");}
    private static void BackupRecovery()
    {
        string directory=Path.Combine(Path.GetTempPath(),"GobmineTest-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        try
        {
            string path=Path.Combine(directory,"test.dat");var s=Snapshot();MaterialWorldPersistence.Write(path,s);
            s.Seed=99;MaterialWorldPersistence.Write(path,s);File.WriteAllBytes(path,new byte[]{1,2,3});
            MaterialWorldPersistence.Snapshot loaded;string error;
            Assert(MaterialWorldPersistence.TryLoad(path,out loaded,out error) && loaded.Seed==1729,"backup was not recovered");
        }
        finally{Directory.Delete(directory,true);}
    }
    private static TerrainMaterialCatalog Cat()=>Resources.Load<TerrainMaterialCatalog>("Gobmine/TerrainMaterialCatalog");
    private static void Catalogue(){var c=Cat();Assert(c!=null,"catalogue missing");c.ValidateOrThrow();Assert(c.Get(0)==null && c.Get(1)!=null && c.Get(4)!=null,"default IDs incorrect");}
    private static void SignatureChanges()
    {
        var original=Cat();var copy=UnityEngine.Object.Instantiate(original);var changed=UnityEngine.Object.Instantiate(original.definitions[0]);
        try{copy.definitions=(TerrainMaterialDefinition[])original.definitions.Clone();copy.definitions[0]=changed;string a=copy.RulesSignature();changed.itemsPerCubicMetre+=1;Assert(copy.RulesSignature()!=a,"yield not in signature");}
        finally{UnityEngine.Object.DestroyImmediate(changed);UnityEngine.Object.DestroyImmediate(copy);}
    }
    private static void DuplicateIDs()
    {var copy=UnityEngine.Object.Instantiate(Cat());try{copy.definitions=new[]{Cat().definitions[0],Cat().definitions[0]};Throws(()=>copy.ValidateOrThrow());}finally{UnityEngine.Object.DestroyImmediate(copy);}}
}
#endif
