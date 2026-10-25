#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Pure-data regression tests. Run Tools > Gobmine > Run Debris Regression Tests.
/// No NUnit package, scene edits, prefabs, networking session or save-file writes.
/// These tests do not replace host/client and dedicated-server play-mode testing.
/// </summary>
public static class DebrisRegressionTests
{
    private static int _passed, _failed;

    [MenuItem("Tools/Gobmine/Run Debris Regression Tests")]
    public static void RunAll()
    {
        _passed = _failed = 0;
        Check("Negative grid coordinates", TestFloorDivision);
        Check("All eight shared chunk-corner copies", TestSharedCorner);
        Check("Unloaded chunk uses canonical removal", TestUnloaded);
        Check("Legacy seam conflicts keep excavation", TestLegacySeam);
        Check("Below-zero isolated rock", () => TestIsolated(-40));
        Check("Above-zero isolated rock", () => TestIsolated(40));
        Check("150001-sample island exceeds old limit", TestLargeIsland);
        Check("Cached supported paths do not falsely collapse", TestSupportedCache);
        Check("Island crosses chunk boundary", TestCrossChunkIsland);
        Check("Random connectivity against independent oracle", TestRandomConnectivity);
        Check("Full roof patch approached from below", () => TestRoofPatch(-1));
        Check("Full roof patch approached from above", () => TestRoofPatch(2));
        Check("Starting midway through thick roof", TestThickRoof);
        Check("Air on only one side is not a thin roof", TestOneBoundary);
        Check("Interpolated thickness, not sample count", TestInterpolatedThickness);
        Check("Zero disables thin-roof rule", TestDisabledRoofRule);
        Check("Full and empty cell material volumes", TestFullEmptyVolume);
        Check("Partial cuts account for surface movement", TestPartialVolume);
        Check("Internal weakening does not invent removed volume", TestInternalWeakening);
        Check("Empty-space changes produce no material", TestEmptyVolume);
        Check("Volume and centroid for axis-aligned cuts", TestPlanarVolumes);
        Check("Save/import terrain round trip", TestSaveRoundTrip);
        Check("Severed support triggers follow-up collapse", TestSeveredSupport);
        Check("Cut during an unfinished structural pass", TestCutDuringPass);
        Check("Volume is monotonic during repeated cuts", TestMonotonicVolume);
        Debug.Log($"[Gobmine Debris Tests] {_passed} passed; {_failed} failed.");
        if (_failed > 0) throw new Exception("Debris regression tests failed. See preceding Console errors.");
    }

    private static void Check(string name, Action test)
    {
        try { test(); _passed++; Debug.Log("PASS: " + name); }
        catch (Exception ex) { _failed++; Debug.LogError("FAIL: " + name + "\n" + ex); }
    }
    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
    private static void Near(float a, float b, float tolerance = 0.00002f)
    {
        Assert(Mathf.Abs(a - b) <= tolerance, $"Expected {b}, got {a}");
    }
    private static TerrainDensityStore Store() => new TerrainDensityStore(new Vector3Int(16,16,16), 0.25f);

    private static HashSet<Vector3Int> Finish(TerrainStructuralSolver solver, int budget = 64)
    {
        var removed = new HashSet<Vector3Int>();
        int steps = 0;
        while (!solver.IsComplete)
        {
            solver.Step(budget);
            List<Vector3Int> result;
            while (solver.TryGetCollapse(out result)) foreach (Vector3Int p in result) removed.Add(p);
            if (++steps > 1000000) throw new Exception("Structural pass did not finish.");
        }
        return removed;
    }
    private static HashSet<Vector3Int> Solve(IEnumerable<Vector3Int> seeds, Func<Vector3Int,float> density,
        Func<Vector3Int,bool> anchor, int thickness = 0, int budget = 64) =>
        Finish(new TerrainStructuralSolver(seeds, density, anchor, thickness), budget);

    private static void TestFloorDivision()
    {
        Assert(TerrainDensityStore.FloorDiv(-1,16) == -1, "-1 sample owner");
        Assert(TerrainDensityStore.FloorDiv(-16,16) == -1, "negative boundary owner");
        Assert(TerrainDensityStore.FloorDiv(-17,16) == -2, "negative next chunk owner");
    }
    private static void TestSharedCorner()
    {
        var store = Store(); Vector3Int p = new Vector3Int(16,-16,16); store.Set(p,1f);
        int count = 0;
        foreach (Vector3Int c in store.ChunksSharingSample(p))
        {
            var d = store.MakeChunk(c); Vector3Int local = p - Vector3Int.Scale(c, store.ChunkSize);
            Near(d[local.x,local.y,local.z],1f); count++;
        }
        Assert(count == 8, "A chunk-corner sample needs eight consistent views.");
    }
    private static void TestUnloaded()
    {
        var store = Store(); Vector3Int p = new Vector3Int(-17,-22,31); store.Set(p,1f);
        Vector3Int c = store.Owner(p), local = p - Vector3Int.Scale(c, store.ChunkSize);
        var d = store.MakeChunk(c); Near(d[local.x,local.y,local.z],1f);
    }
    private static void TestLegacySeam()
    {
        var store = Store(); Vector3Int p = new Vector3Int(16,-17,17);
        Vector3Int left = new Vector3Int(0,-2,1), right = new Vector3Int(1,-2,1);
        var a = store.MakeChunk(left); var b = store.MakeChunk(right);
        a[16,15,1] = 3f;
        store.ImportLegacyChunks(new Dictionary<Vector3Int,float[,,]> { {right,b}, {left,a} });
        Near(store.Get(p),3f);
        Near(store.MakeChunk(right)[0,15,1],3f);
    }
    private static void TestIsolated(int y)
    {
        var store = Store(); Vector3Int p = new Vector3Int(100,y,100);
        foreach (Vector3Int direction in TerrainStructuralSolver.Directions) store.Set(p+direction,1f);
        Assert(!store.HasSolidRayToInfinity(p), "Isolated rock cannot prove support.");
        var result = Solve(new[] {p}, store.Get, store.HasSolidRayToInfinity);
        Assert(result.SetEquals(new[] {p}), "The isolated sample must collapse regardless of Y.");
    }
    private static void TestLargeIsland()
    {
        Func<Vector3Int,float> density = p => p.y == -30 && p.z == 0 && p.x >= 0 && p.x <= 150000 ? -1f : 1f;
        var solver = new TerrainStructuralSolver(new[] {new Vector3Int(0,-30,0)}, density, p => false, 0);
        solver.Step(128); Assert(!solver.IsComplete, "A work budget must pause, not classify support.");
        Assert(Finish(solver,128).Count == 150001, "The old 150000 size limit must not preserve this island.");
    }
    private static void TestSupportedCache()
    {
        var seeds = new List<Vector3Int>();
        for (int x=0;x<20;x++) seeds.Add(new Vector3Int(x,0,0));
        Func<Vector3Int,float> d = p => p.y==0 && p.z==0 && p.x>=0 && p.x<20 ? -1f : 1f;
        Assert(Solve(seeds,d,p=>p.x==0,0,1).Count==0,"Reaching previously proved support must not look disconnected.");
    }
    private static void TestCrossChunkIsland()
    {
        Func<Vector3Int,float> d = p=>p.x>=14 && p.x<=18 && p.y==-5 && p.z==0 ? -1f : 1f;
        Assert(Solve(new[] {new Vector3Int(16,-5,0)},d,p=>false).Count==5,"All chunks must share one graph.");
    }
    private static HashSet<Vector3Int> UnsupportedOracle(HashSet<Vector3Int> solid, Func<Vector3Int,bool> anchor)
    {
        var supported = new HashSet<Vector3Int>(); var queue = new Queue<Vector3Int>();
        foreach(Vector3Int p in solid) if(anchor(p)) { supported.Add(p); queue.Enqueue(p); }
        while(queue.Count>0)
        {
            Vector3Int p=queue.Dequeue();
            foreach(Vector3Int d in TerrainStructuralSolver.Directions)
                if(solid.Contains(p+d) && supported.Add(p+d)) queue.Enqueue(p+d);
        }
        var result = new HashSet<Vector3Int>(solid); result.ExceptWith(supported); return result;
    }
    private static void TestRandomConnectivity()
    {
        var random = new System.Random(7135);
        for(int trial=0;trial<80;trial++)
        {
            var solid = new HashSet<Vector3Int>();
            for(int x=0;x<7;x++) for(int y=-3;y<=3;y++) for(int z=0;z<5;z++)
                if(random.NextDouble()<0.55) solid.Add(new Vector3Int(x,y,z));
            Func<Vector3Int,bool> anchor=p=>p.x==0;
            var expected=UnsupportedOracle(solid,anchor);
            var seeds = new List<Vector3Int>(solid);
            for (int i=seeds.Count-1;i>0;i--)
            {
                int j=random.Next(i+1); Vector3Int temporary=seeds[i]; seeds[i]=seeds[j]; seeds[j]=temporary;
            }
            var actual=Solve(seeds,p=>solid.Contains(p)?-1f:1f,anchor,0,1+trial%31);
            Assert(actual.SetEquals(expected),"Connectivity oracle mismatch in trial "+trial);
        }
    }
    private static void TestRoofPatch(int editedY)
    {
        var seeds = new List<Vector3Int>(); var expected = new HashSet<Vector3Int>();
        for(int x=0;x<5;x++) for(int z=0;z<5;z++)
        {
            seeds.Add(new Vector3Int(x,editedY,z));
            expected.Add(new Vector3Int(x,0,z)); expected.Add(new Vector3Int(x,1,z));
        }
        Func<Vector3Int,float> d=p=>expected.Contains(p)?-1f:1f;
        // Every solid sample is otherwise supported: this isolates the thin-roof rule.
        Assert(Solve(seeds,d,p=>true,2).SetEquals(expected),"The complete affected patch must collapse.");
    }
    private static void TestThickRoof()
    {
        Func<Vector3Int,float> d=p=>p.y>=0 && p.y<=3 ? -1f : 1f;
        Assert(TerrainStructuralSolver.FindThinSpan(new Vector3Int(0,2,0),d,2)==null,"Do not count only the upper half.");
    }
    private static void TestOneBoundary()
    {
        Assert(TerrainStructuralSolver.FindThinSpan(Vector3Int.zero,p=>p.y<=2?-1f:1f,2)==null,"Both air boundaries are required.");
    }
    private static void TestInterpolatedThickness()
    {
        Func<Vector3Int,float> two=p=>p.y==0||p.y==1?-5f:1f;
        Func<Vector3Int,float> one=p=>p.y==0?-5f:1f;
        Assert(TerrainStructuralSolver.FindThinSpan(Vector3Int.zero,two,2)==null,"Two samples can span more than two spacings.");
        Assert(TerrainStructuralSolver.FindThinSpan(Vector3Int.zero,one,2)!=null,"The actual thin layer should collapse.");
    }
    private static void TestDisabledRoofRule() =>
        Assert(TerrainStructuralSolver.FindThinSpan(Vector3Int.zero,p=>p.y==0?-1f:1f,0)==null,"Zero should disable roof checks.");
    private static void TestFullEmptyVolume()
    {
        var v=new TerrainVolume(); Near(v.SampleCell(Vector3Int.zero,p=>-1f).Volume,1f);
        Near(v.SampleCell(Vector3Int.zero,p=>1f).Volume,0f);
    }
    private static void TestPartialVolume()
    {
        var v=new TerrainVolume(); var a=v.SampleCell(Vector3Int.zero,p=>p.x-0.7f);
        var b=v.SampleCell(Vector3Int.zero,p=>p.x-0.3f); var r=a-b;
        Near(r.Volume,0.4f); Near((r.Moment/r.Volume).x,0.5f);
    }
    private static void TestInternalWeakening()
    {
        var v=new TerrainVolume(); Near((v.SampleCell(Vector3Int.zero,p=>-5f)-v.SampleCell(Vector3Int.zero,p=>-1f)).Volume,0f);
    }
    private static void TestEmptyVolume()
    {
        var v=new TerrainVolume(); Near((v.SampleCell(Vector3Int.zero,p=>1f)-v.SampleCell(Vector3Int.zero,p=>5f)).Volume,0f);
    }
    private static void TestPlanarVolumes()
    {
        var v=new TerrainVolume();
        for(int axis=0;axis<3;axis++) for(int n=1;n<20;n++)
        {
            int a=axis; float cut=n/20f;
            var m=v.SampleCell(Vector3Int.zero,p=>(a==0?p.x:a==1?p.y:p.z)-cut);
            Near(m.Volume,cut);
            Vector3 center=m.Moment/m.Volume;
            Near(a==0?center.x:a==1?center.y:center.z,cut/2f);
        }
    }
    private static void TestSaveRoundTrip()
    {
        var a=Store(); var points=new[] {new Vector3Int(-17,-23,5),new Vector3Int(16,-16,16),new Vector3Int(80,-51,25)};
        foreach(Vector3Int p in points) a.Set(p,1f);
        var b=Store(); b.ImportLegacyChunks(a.ExportLegacyChunks());
        foreach(Vector3Int p in points) Near(b.Get(p),1f);
    }
    private static void TestSeveredSupport()
    {
        var solid=new HashSet<Vector3Int>(); for(int x=0;x<4;x++) solid.Add(new Vector3Int(x,0,0));
        Func<Vector3Int,float> d=p=>solid.Contains(p)?-1f:1f;
        Assert(Solve(solid,d,p=>p.x==0).Count==0,"Initial structure should be supported.");
        Vector3Int cut=new Vector3Int(1,0,0); solid.Remove(cut);
        var expected=new[] {new Vector3Int(2,0,0),new Vector3Int(3,0,0)};
        Assert(Solve(new[] {cut},d,p=>p.x==0).SetEquals(expected),"Follow-up must detect the new island.");
    }
    private static void TestCutDuringPass()
    {
        var solid=new HashSet<Vector3Int>(); for(int x=0;x<=20;x++) solid.Add(new Vector3Int(x,0,0));
        Func<Vector3Int,float> d=p=>solid.Contains(p)?-1f:1f;
        var solver=new TerrainStructuralSolver(new[] {new Vector3Int(20,0,0)},d,p=>p.x==0,0);
        solver.Step(3); Vector3Int cut=new Vector3Int(5,0,0); solid.Remove(cut);
        var removed=Finish(solver); removed.UnionWith(Solve(new[] {cut},d,p=>p.x==0));
        Assert(removed.SetEquals(UnsupportedOracle(solid,p=>p.x==0)),"Mid-pass cut must not be lost.");
    }
    private static void TestMonotonicVolume()
    {
        var random=new System.Random(518); var values=new Dictionary<Vector3Int,float>();
        for(int x=0;x<2;x++) for(int y=0;y<2;y++) for(int z=0;z<2;z++) values[new Vector3Int(x,y,z)]=-2f;
        var v=new TerrainVolume(); float previous=1f;
        for(int i=0;i<200;i++)
        {
            Vector3Int p=new Vector3Int(random.Next(2),random.Next(2),random.Next(2)); values[p]+=0.1f;
            float current=v.SampleCell(Vector3Int.zero,q=>values[q]).Volume;
            Assert(current<=previous+0.000002f,"Removal must not increase material volume."); previous=current;
        }
    }
}
#endif
