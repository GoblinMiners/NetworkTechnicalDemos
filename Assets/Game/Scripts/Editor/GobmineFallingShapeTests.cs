#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

/// <summary>Tests deterministic extraction and save compatibility. Not a multiplayer play-mode test.</summary>
public static class GobmineFallingShapeTests
{
    [MenuItem("Tools/Gobmine/Falling Shapes/Run Source-Level Unity Tests")]
    public static void Run()
    {
        int passed = 0;
        RunTest("Single density point makes a real surface", TestSinglePoint,ref passed);
        RunTest("Detailed cut surfaces retain air-side interpolation", TestHaloInterpolation,ref passed);
        RunTest("Connected sculpture remains ONE shape", TestConnectedArch,ref passed);
        RunTest("Collision proxy stays within box allowance", TestProxyBudget,ref passed);
        RunTest("Bigger and faster rocks inflict greater damage", TestImpacts,ref passed);
        RunTest("Shape save round-trip preserves density and pose", TestSaveV3,ref passed);
        RunTest("Older save formats still decode", TestOldSaves,ref passed);
        Debug.Log("[Gobmine Falling Shapes] " + passed + " rule tests passed. Now test actual rigidbodies/host-client in Play mode.");
    }
    private static void RunTest(string name,Action test,ref int passed)
    {
        try { test(); passed++; Debug.Log("[Gobmine Falling Shapes] PASS " + name); }
        catch (Exception ex) { Debug.LogError("[Gobmine Falling Shapes] FAIL " + name + ": " + ex); throw; }
    }
    private static void Need(bool condition,string explanation)
    { if (!condition) throw new InvalidOperationException(explanation); }
    private static TerrainShapeRecord One()
    {
        var r = new TerrainShapeRecord { Id = 1, Position = Vector3.zero };
        r.Samples.Add(new TerrainShapeSample(Vector3Int.zero,-1f));
        foreach (Vector3Int d in TerrainStructuralSolver.Directions) r.Samples.Add(new TerrainShapeSample(d,1f));
        return r;
    }
    private static TerrainShapeMeshJob Build(TerrainShapeRecord r,float step=1f)
    {
        var job = new TerrainShapeMeshJob(r,step,p => 1);
        while (!job.Complete) job.Step(3); // Exercise incremental work.
        return job;
    }
    private static void TestSinglePoint()
    {
        var job=Build(One());
        var materials=new List<int>(); Mesh mesh=job.CreateMesh(materials);
        try { Need(mesh.vertexCount>=6 && mesh.GetIndexCount(0)>=18,"Missing marching-cubes triangles");
            Need(materials.Count==1 && materials[0]==1,"Material mapping incorrect"); }
        finally { UnityEngine.Object.DestroyImmediate(mesh); }
    }
    private static void TestHaloInterpolation()
    {
        var r=One();
        for(int i=0;i<r.Samples.Count;i++) if(r.Samples[i].Grid==Vector3Int.right)
            r.Samples[i]=new TerrainShapeSample(Vector3Int.right,9f);
        var job=Build(r);
        var materials=new List<int>(); Mesh mesh=job.CreateMesh(materials);
        try { Need(mesh.bounds.max.x>0.08f && mesh.bounds.max.x<0.12f,
            "Original positive density should set X boundary at 0.1, not generic 0.5"); }
        finally { UnityEngine.Object.DestroyImmediate(mesh); }
    }
    private static void TestConnectedArch()
    {
        // Two legs + bridge. The single sample set is one detached component.
        var r=new TerrainShapeRecord{Id=2};
        for(int y=0;y<=6;y++)
        { r.Samples.Add(new TerrainShapeSample(new Vector3Int(0,y,0),-1f));
          r.Samples.Add(new TerrainShapeSample(new Vector3Int(6,y,0),-1f)); }
        for(int x=1;x<6;x++)r.Samples.Add(new TerrainShapeSample(new Vector3Int(x,6,0),-1f));
        var job=Build(r,0.25f);
        var ids=new List<int>();Mesh mesh=job.CreateMesh(ids);
        try { Need(mesh.vertexCount>24 && mesh.bounds.size.x>1.4f && mesh.bounds.size.y>1.4f,
            "Arch did not preserve its broad silhouette"); }
        finally {UnityEngine.Object.DestroyImmediate(mesh);}
    }
    private static void TestProxyBudget()
    {
        var r=new TerrainShapeRecord{Id=3};
        for(int x=0;x<22;x++) for(int z=0;z<18;z++)
            r.Samples.Add(new TerrainShapeSample(new Vector3Int(x,z%4,z),-1f));
        var job=Build(r);
        var boxes=job.CreateCompoundBoxes(32);
        Need(boxes.Count>0 && boxes.Count<=32,"Proxy collider cap is not respected");
    }
    private static void TestImpacts()
    {
        float s=TerrainShapeImpactRules.Damage(0.25f,5f,2f,8f,90f);
        float l=TerrainShapeImpactRules.Damage(9f,5f,2f,8f,90f);
        Need(s>0f && l>s,"Large object should do more damage at the same speed");
        Need(TerrainShapeImpactRules.Damage(1f,1f,2f,8f,90f)==0f,"Slow touch must not hurt");
        Need(TerrainShapeImpactRules.Damage(1f,40f,2f,8f,90f)<=90f,"Damage max ignored");
        Need(TerrainShapeImpactRules.Knockback(9f,5f,2f,1.7f,14f)>
             TerrainShapeImpactRules.Knockback(0.25f,5f,2f,1.7f,14f),"Large chunk should knock back more");
    }
    private static void TestSaveV3()
    {
        var s=NewSave();
        var r=One();r.Id=13;r.Position=new Vector3(2f,-22f,5f);
        r.Rotation=Quaternion.Euler(0,32,4);r.Velocity=new Vector3(0,-5,0);
        s.Shapes.Add(r);
        byte[] binary=MaterialWorldPersistence.Encode(s);
        var restored=MaterialWorldPersistence.Decode(binary);
        Need(restored.Shapes.Count==1 && restored.Shapes[0].Samples.Count==r.Samples.Count,
            "A persisted falling chunk lost its density samples");
        Need(!restored.ExpeditionState,"V3 shape-only save must not silently enable expedition mode");
        Need((restored.Shapes[0].Position-r.Position).sqrMagnitude<0.000001f,
            "A persisted falling chunk lost its world position");
    }
    private static void TestOldSaves()
    {
        var s=NewSave();
        var v1=MaterialWorldPersistence.Decode(MaterialWorldPersistence.Encode(s));
        Need(v1.Shapes.Count==0,"Existing no-expedition saves must load");
        s.ExpeditionState=true;
        var v2=MaterialWorldPersistence.Decode(MaterialWorldPersistence.Encode(s));
        Need(v2.Shapes.Count==0,"Existing expedition saves must load");
    }
    private static MaterialWorldPersistence.Snapshot NewSave() =>
        new MaterialWorldPersistence.Snapshot { Seed=1729, ChunkSize=new Vector3Int(16,16,16),
            VoxelSize=0.25f, RulesSignature=new string('a',64) };
}
#endif
