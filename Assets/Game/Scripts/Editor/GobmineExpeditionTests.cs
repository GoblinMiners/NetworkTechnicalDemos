#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;

public static class GobmineExpeditionTests
{
    private static int passed,failed;
    private static void Check(bool value,string message){if(!value)throw new Exception(message);}
    private static void Near(float value,float expected,float epsilon=0.001f){Check(Mathf.Abs(value-expected)<=epsilon,$"Expected {expected}, got {value}.");}
    private static void Test(string name,Action body)
    {try{body();passed++;Debug.Log("[Expedition PASS] "+name);}catch(Exception e){failed++;Debug.LogError("[Expedition FAIL] "+name+": "+e);}}
    [MenuItem("Tools/Gobmine/Expedition/Run Expedition Rule Tests")]
    public static void Run()
    {
        passed=failed=0;
        Test("Depth reference is stable",()=>{Near(GobmineExpeditionRules.Depth(0,20),0);Near(GobmineExpeditionRules.Depth(8,-32),40);});
        Test("Normal jump is safe",()=>Near(GobmineExpeditionRules.FallDamage(1.1f,-24,25,3,12,100),0));
        Test("Safe fall threshold",()=>Near(GobmineExpeditionRules.FallDamage(3,-24,25,3,12,100),0));
        Test("Six metre hard landing",()=>Near(GobmineExpeditionRules.FallDamage(6,-24,25,3,12,100),100f/3f));
        Test("Twelve metre lethal landing",()=>Near(GobmineExpeditionRules.FallDamage(12,-24,25,3,12,100),100));
        Test("Terminal speed bounds energy",()=>Check(GobmineExpeditionRules.FallDamage(100,-24,10,3,12,100)==0,"Low terminal speed should cap the energy below the safe threshold."));
        Test("Water cushioning depends on depth",()=>{Near(GobmineExpeditionRules.WaterImpactMultiplier(0.25f,2.5f),0.9f);Near(GobmineExpeditionRules.WaterImpactMultiplier(2.5f,2.5f),0);});
        Test("Fall reported once",()=>{var f=new GobmineFallTracker();f.Reset(10);float d;Check(!f.Sample(9,false,false,0.1f,out d),"Air is not landing.");Check(f.Sample(0,true,false,0.1f,out d),"Landing missing.");Near(d,10);Check(!f.Sample(0,true,false,0.1f,out d),"Duplicate landing.");});
        Test("Terrain wait preserves fall without adding time",()=>{var f=new GobmineFallTracker();f.Reset(10);float d;f.Sample(8,false,false,0.2f,out d);f.Sample(8,false,true,100,out d);Near(f.AirborneSeconds,0.2f);Check(f.Sample(0,true,false,0.1f,out d),"Expected landing.");Near(d,10);});
        Test("Teleport reset clears old fall",()=>{var f=new GobmineFallTracker();f.Reset(100);float d;f.Sample(90,false,false,1,out d);f.Reset(-100);Check(!f.Sample(-100,true,false,1,out d),"Teleport caused damage.");});
        Test("Tool tiers require contiguous research",()=>{Check(GobmineExpeditionRules.ToolTier(8)==0,"Skipped prerequisites.");Check(GobmineExpeditionRules.ToolTier(15)==4,"Tier four missing.");Check(GobmineExpeditionRules.ToolTier(3)==2,"Tier two missing.");});
        Test("All equipment bits distinct",()=>Check(((int)GobmineResearch.Tool4|(int)GobmineResearch.RadiationSuit|(int)GobmineResearch.ShieldedContainer|(int)GobmineResearch.HeatSuit|(int)GobmineResearch.AirTank|(int)GobmineResearch.ImpactBoots)==504,"Overlapping bits."));
        Test("Caves deterministic",()=>WithSettings(s=>{var a=new GobmineCavernGenerator(1729,0.25f,s);var b=new GobmineCavernGenerator(1729,0.25f,s);for(int y=-3;y>-12;y--){var c=new Vector3Int(2,y,-3);Check(a.AtGrid(c).Centre==b.AtGrid(c).Centre,"Different chamber centre.");Check(a.AtGrid(c).Fluid==b.AtGrid(c).Fluid,"Different fluid.");}}));
        Test("Spawn room protected",()=>WithSettings(s=>{s.firstCavernDepth=8;s.surfaceReferenceY=50;s.cavernChancePerThousand=1000;var g=new GobmineCavernGenerator(1729,0.25f,s);for(int x=-1;x<=0;x++)for(int y=-1;y<=0;y++)for(int z=-1;z<=0;z++){var c=g.AtGrid(new Vector3Int(x,y,z));if(!c.Exists)continue;Check(c.Centre.x-c.Radius.x>14 || c.Centre.x+c.Radius.x< -14 || c.Centre.y-c.Radius.y>10 || c.Centre.y+c.Radius.y< -6 || c.Centre.z-c.Radius.z>14 || c.Centre.z+c.Radius.z< -14,"Cave intersects protected start volume.");}}));
        Test("Cave tops obey minimum depth",()=>WithSettings(s=>{s.cavernChancePerThousand=1000;var g=new GobmineCavernGenerator(2,0.25f,s);for(int y=-1;y>-20;y--){var c=g.AtGrid(new Vector3Int(1,y,1));if(c.Exists)Check(s.surfaceReferenceY-(c.Centre.y+c.Radius.y)>=s.firstCavernDepth,"Shallow cave escaped depth gate.");}}));
        Test("Lava top obeys depth gate",()=>WithSettings(s=>{s.cavernChancePerThousand=s.lavaPocketChancePerThousand=1000;var g=new GobmineCavernGenerator(2,0.25f,s);bool found=false;for(int y=-1;y>-30;y--){var c=g.AtGrid(new Vector3Int(1,y,1));if(c.Fluid==GobmineFluidKind.Lava){found=true;Check(s.surfaceReferenceY-(c.Centre.y+c.Radius.y)>=s.lavaStartsAtDepth,"Shallow lava.");}}Check(found,"No lava generated.");}));
        Test("Macrocell seams stay solid",()=>WithSettings(s=>{s.cavernChancePerThousand=1000;var g=new GobmineCavernGenerator(7,0.25f,s);for(int x=-120;x<120;x+=3)for(int z=-120;z<120;z+=7)Check(g.ModifyDensity(new Vector3Int(x,-192,z),-5)<0,"Cave cut through guaranteed seam.");}));
        Test("Support route stays in base rock",()=>WithSettings(s=>{s.cavernChancePerThousand=1000;var g=new GobmineCavernGenerator(7,0.25f,s);var t=new TerrainDensityStore(new Vector3Int(16,16,16),0.25f,g);for(int x=10;x<90;x+=7)for(int y=-380;y<-290;y+=9)for(int z=10;z<90;z+=11){var p=new Vector3Int(x,y,z);if(t.Get(p)>=0)continue;var end=g.SupportEndpoint(p);for(int yy=Math.Min(y,end.y);yy<=Math.Max(y,end.y);yy++)Check(t.Get(new Vector3Int(x,yy,z))<0,"Support certificate crosses a cavern.");}}));
        Test("Disconnected below-zero sample not anchored",()=>WithSettings(s=>{var g=new GobmineCavernGenerator(7,0.25f,s);var t=new TerrainDensityStore(new Vector3Int(16,16,16),0.25f,g);var p=new Vector3Int(96,-192,96);Check(t.Get(p)<0,"Fixture not rock.");foreach(var d in TerrainStructuralSolver.Directions)t.Set(p+d,1);Check(!t.HasSolidRayToInfinity(p),"Isolated sample falsely supported.");}));
        Test("Undisturbed cavern scaffold has support",()=>WithSettings(s=>{var g=new GobmineCavernGenerator(7,0.25f,s);var t=new TerrainDensityStore(new Vector3Int(16,16,16),0.25f,g);Check(t.HasSolidRayToInfinity(new Vector3Int(96,-192,96)),"Scaffold not supported.");}));
        Test("Cavern-aware solver removes actual island",()=>WithSettings(s=>{var g=new GobmineCavernGenerator(7,0.25f,s);var t=new TerrainDensityStore(new Vector3Int(16,16,16),0.25f,g);var p=new Vector3Int(96,-192,96);foreach(var d in TerrainStructuralSolver.Directions)t.Set(p+d,1);var solver=new TerrainStructuralSolver(new[]{p},t.Get,t.HasSolidRayToInfinity,0);for(int i=0;i<20000 && !solver.IsComplete;i++)solver.Step(64);List<Vector3Int> result;bool found=false;while(solver.TryGetCollapse(out result))if(result.Contains(p))found=true;Check(found,"Island did not collapse.");}));
        Test("Fluid transfer conserves exact integer amount",()=>{var s=Pool();s.Apply(new Vector3Int(0,2,0),new GobmineFluidCell(GobmineFluidKind.Water,1000));s.WakeAround(new Vector3Int(0,2,0));for(int i=0;i<4000;i++)s.Step(p=>true);Check(Sum(s)==1000,"Mass changed.");});
        Test("Solid floor blocks fluid",()=>{var s=Pool();s.Apply(Vector3Int.zero,new GobmineFluidCell(GobmineFluidKind.Water,1000));s.Wake(Vector3Int.zero);for(int i=0;i<100;i++)s.Step(p=>true);Check(s.Get(Vector3Int.down).Amount==0,"Fluid entered rock.");});
        Test("Inactive boundary retains fluid",()=>{var s=Pool();var c=new Vector3Int(0,2,0);s.Apply(c,new GobmineFluidCell(GobmineFluidKind.Water,1000));s.Wake(c);s.Step(p=>p==c);Check(s.Get(c).Amount==1000,"Flow into inactive region.");});
        Test("Dormant cells can wake again",()=>{var s=Pool();var c=new Vector3Int(0,2,0);s.Apply(c,new GobmineFluidCell(GobmineFluidKind.Water,1000));s.Wake(c);s.Step(p=>false);s.Wake(c);s.Step(p=>true);Check(s.Get(c).Amount==0,"Cell did not resume.");});
        Test("Water and lava do not delete one another",()=>{var s=new GobmineFluidStore(p=>default,p=>p==Vector3Int.zero || p==Vector3Int.right);s.Apply(Vector3Int.zero,new GobmineFluidCell(GobmineFluidKind.Water,1000));s.Apply(Vector3Int.right,new GobmineFluidCell(GobmineFluidKind.Lava,1000));s.Wake(Vector3Int.zero);s.Step(p=>true);Check(s.Get(Vector3Int.zero).Kind==GobmineFluidKind.Water && s.Get(Vector3Int.right).Kind==GobmineFluidKind.Lava,"Unexpected reaction.");});
        Test("Lava transfer is rate-limited",()=>{var s=Pool();var c=new Vector3Int(0,2,0);s.Apply(c,new GobmineFluidCell(GobmineFluidKind.Lava,1000));s.Wake(c);s.Step(p=>true);Check(s.Get(c+Vector3Int.down).Amount==100,"Lava downward rate mismatch.");Check(Sum(s)==1000,"Lava mass changed.");});
        Test("Zero override prevents reservoir refill",()=>{Func<Vector3Int,GobmineFluidCell> initial=p=>p==Vector3Int.up?new GobmineFluidCell(GobmineFluidKind.Water,1000):default;var s=new GobmineFluidStore(initial,p=>p==Vector3Int.zero || p==Vector3Int.up);s.Wake(Vector3Int.up);s.Step(p=>true);var reload=new GobmineFluidStore(initial,p=>true);reload.Import(s.CopyOverrides());Check(reload.Get(Vector3Int.up).Amount==0,"Drained source regenerated.");});
        Test("Fluid state rejects invalid kinds",()=>{var s=Pool();bool threw=false;try{s.Apply(Vector3Int.zero,new GobmineFluidCell((GobmineFluidKind)3,10));}catch(ArgumentException){threw=true;}Check(threw,"Bad kind accepted.");});
        Test("Fluid and research save round-trip",()=>{var s=Snapshot();s.ExpeditionUnlocks=511;s.Fluids.Add(new GobmineFluidPoint{Cell=new Vector3Int(-1,-220,3),Kind=GobmineFluidKind.Lava,Amount=712});s.Fluids.Add(new GobmineFluidPoint{Cell=Vector3Int.zero,Kind=GobmineFluidKind.None,Amount=0});var r=MaterialWorldPersistence.Decode(MaterialWorldPersistence.Encode(s));Check(r.ExpeditionUnlocks==511 && r.Fluids.Count==2 && r.Fluids[0].Amount==712 && r.Fluids[1].Amount==0,"Snapshot differs.");});
        Test("Save checksum rejects corruption",()=>{var bytes=MaterialWorldPersistence.Encode(Snapshot());bytes[20]^=1;bool threw=false;try{MaterialWorldPersistence.Decode(bytes);}catch(InvalidDataException){threw=true;}Check(threw,"Corruption accepted.");});
        Test("Legacy format still decodes",()=>{var r=MaterialWorldPersistence.Decode(LegacySave());Check(r.Fluids.Count==0 && r.ExpeditionUnlocks==0,"Legacy defaults incorrect.");});
        Test("Resource spatial index follows movement",()=>{var r=new ResourcePileStore();long id=r.Add(1,1,10,Vector3.zero);Check(r.Nearby(Vector3.zero,2).Count()==1,"Not indexed.");r.UpdatePosition(id,new Vector3(100,0,0));Check(r.Nearby(Vector3.zero,2).Count()==0 && r.Nearby(new Vector3(100,0,0),2).Count()==1,"Move index stale.");ResourcePileStore.Pile p;r.Remove(id,out p);Check(r.Nearby(new Vector3(100,0,0),2).Count()==0,"Removed pile indexed.");});
        Test("Ore cell depth cannot spill into shallow region",TestOreBand);
        Test("Changing cavern generation changes identity",()=>WithSettings(s=>{string old=s.GenerationSignature();s.firstCavernDepth+=2;Check(old!=s.GenerationSignature(),"Generation not signed.");}));
        Test("Changing work budget does not change world identity",()=>WithSettings(s=>{string old=s.GenerationSignature();s.fluidOperationsPerFrame+=1;Check(old==s.GenerationSignature(),"Performance tuning changed save identity.");}));
        Test("Invalid fluid resolution rejected",()=>WithSettings(s=>{s.fluidCellMetres=0.3f;bool threw=false;try{s.ValidateOrThrow(0.25f);}catch(InvalidOperationException){threw=true;}Check(threw,"Misaligned cell size accepted.");}));
        Debug.Log($"[Gobmine Expedition] {passed} passed, {failed} failed. These are rule tests, not a multiplayer/physics certification.");
        if(failed>0)throw new Exception("Expedition tests failed. Read the first FAIL above.");
    }
    private static void WithSettings(Action<GobmineExpeditionSettings> action)
    {var s=ScriptableObject.CreateInstance<GobmineExpeditionSettings>();try{action(s);}finally{UnityEngine.Object.DestroyImmediate(s);}}
    private static GobmineFluidStore Pool()=>new GobmineFluidStore(p=>default,p=>p.x>=-2 && p.x<=2 && p.z>=-2 && p.z<=2 && p.y>=0 && p.y<=3);
    private static int Sum(GobmineFluidStore s){int n=0;for(int x=-2;x<=2;x++)for(int y=0;y<=3;y++)for(int z=-2;z<=2;z++)n+=s.Get(new Vector3Int(x,y,z)).Amount;return n;}
    private static MaterialWorldPersistence.Snapshot Snapshot()=>new MaterialWorldPersistence.Snapshot{Seed=1729,ChunkSize=new Vector3Int(16,16,16),VoxelSize=0.25f,RulesSignature=new string('A',64)};
    private static byte[] LegacySave()
    {
        using(var ms=new MemoryStream())
        {
            using(var w=new BinaryWriter(ms,Encoding.UTF8,true))
            {w.Write(Encoding.ASCII.GetBytes("GMBMAT01"));w.Write(1);w.Write(TerrainMaterialCatalog.GeneratorVersion);w.Write(1729);w.Write(16);w.Write(16);w.Write(16);w.Write(0.25f);w.Write(new string('A',64));w.Write(0);w.Write(0);}
            byte[] payload=ms.ToArray();using(var sha=SHA256.Create()){var hash=sha.ComputeHash(payload);ms.Write(hash,0,hash.Length);}return ms.ToArray();
        }
    }
    private static void TestOreBand()
    {
        var stone=ScriptableObject.CreateInstance<TerrainMaterialDefinition>();var ore=ScriptableObject.CreateInstance<TerrainMaterialDefinition>();var c=ScriptableObject.CreateInstance<TerrainMaterialCatalog>();
        try
        {
            stone.materialId=1;ore.materialId=2;ore.generateDeposits=true;ore.useDepthBand=true;ore.minimumDepthMetres=20;ore.maximumDepthMetres=30;
            ore.depositChancePerThousand=1000;ore.depositGridSizeInCells=8;ore.minimumRadiusInCells=ore.maximumRadiusInCells=4;c.baseStoneId=1;c.definitions=new[]{stone,ore};
            var generator=new TerrainMaterialGenerator(1,c,0.25f,0f);bool inside=false;
            for(int x=-16;x<=16;x++)for(int y=-140;y<=-60;y++)
            {int id=generator.GetSolidMaterial(new Vector3Int(x,y,0));float depth=-(y+0.5f)*0.25f;if(id==2){inside=true;Check(depth>=20 && depth<=30,"Deposit escaped band.");}}
            Check(inside,"Fixture generated no ore.");
        }
        finally{UnityEngine.Object.DestroyImmediate(stone);UnityEngine.Object.DestroyImmediate(ore);UnityEngine.Object.DestroyImmediate(c);}
    }
}
#endif
