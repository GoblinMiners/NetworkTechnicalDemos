#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;

/// <summary>Run in Unity. Pure algorithm tests; not physics or networking integration tests.</summary>
public static class GobminePerformanceRegressionTests
{
    private static int _passed, _failed;
    [MenuItem("Tools/Gobmine/Performance/Run Performance Regression Tests")]
    public static void Run()
    {
        if (EditorApplication.isPlaying) throw new InvalidOperationException("Stop play mode first.");
        _passed = _failed = 0;
        Check("Removal quotas respect their total and give both lanes work", FairBudgets);
        Check("Single-sample budget alternates mining and collapse", SingleSampleBudget);
        Check("Single active lane receives full allowance", SingleLaneBudget);
        Check("Cached density matches original generator", DensityMatchesOriginal);
        Check("Density overrides take priority over cache", OverrideWins);
        Check("Rock-column cache remains bounded", BoundedCache);
        Check("Adjacent chunks still share identical samples", SharedChunkSamples);
        Check("Completely solid volume is one with central moment", SolidVolume);
        Check("Completely air volume is zero", EmptyVolume);
        Check("Partly cut cells retain interpolated volume", PartialVolume);
        Check("Sequential plane cuts conserve removed volume", VolumeConservation);
        Check("Mesh result buffers can be reused without changing output", ReusedMesh);
        Check("Reusing a mesh for air clears previous geometry", ReusedAirMesh);
        Check("Reusing a mesh clears previous material groups", ReusedMaterials);
        Debug.Log($"[Gobmine Performance Tests] {_passed} passed; {_failed} failed. These are not physics, render or Mirror play-mode tests.");
        if (_failed > 0) throw new Exception("Performance regression tests failed. Send the full Console output.");
    }
    private static void Check(string name, Action test)
    { try { test(); _passed++; Debug.Log("PASS: " + name); } catch (Exception ex) { _failed++; Debug.LogError("FAIL: " + name + "\n" + ex); } }
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Near(float a, float b, float tolerance = 0.00002f) { Assert(Mathf.Abs(a - b) <= tolerance, $"Expected {b}, got {a}"); }
    private static Vector2Int Split(int total, bool mining, bool collapse, bool turn = false)
    {
        MethodInfo method = typeof(World).GetMethod("SplitRemovalBudget", BindingFlags.Static | BindingFlags.NonPublic);
        if (method == null) throw new Exception("Install the performance World.cs before running these tests.");
        return (Vector2Int)method.Invoke(null, new object[] { total, mining, collapse, turn });
    }
    private static void FairBudgets()
    {
        for (int n = 2; n <= 8192; n++)
        {
            Vector2Int b = Split(n, true, true);
            Assert(b.x > 0 && b.y > 0 && b.x + b.y == n, "Invalid split for " + n);
        }
    }
    private static void SingleSampleBudget()
    { Assert(Split(1, true, true, false) == new Vector2Int(1, 0), "mining turn"); Assert(Split(1, true, true, true) == new Vector2Int(0, 1), "collapse turn"); }
    private static void SingleLaneBudget()
    { Assert(Split(128, true, false) == new Vector2Int(128, 0), "mining quota"); Assert(Split(128, false, true) == new Vector2Int(0, 128), "collapse quota"); Assert(Split(128, false, false) == Vector2Int.zero, "idle quota"); }
    private static TerrainDensityStore Store() => new TerrainDensityStore(new Vector3Int(16, 16, 16), 0.25f);
    private static float OriginalDensity(Vector3Int p)
    {
        Vector3 w = (Vector3)p * 0.25f;
        bool room = w.x >= -12f && w.x <= 12f && w.y >= -4f && w.y <= 8f && w.z >= -12f && w.z <= 12f;
        return room ? 5f : -5f - Mathf.PerlinNoise(w.x * 0.1f, w.z * 0.1f) * 2f;
    }
    private static void DensityMatchesOriginal()
    {
        var store = Store();
        for (int x = -65; x <= 65; x += 3) for (int z = -65; z <= 65; z += 3) for (int y = -20; y <= 40; y += 4)
        { var p = new Vector3Int(x, y, z); Near(store.Get(p), OriginalDensity(p), 0f); }
    }
    private static void OverrideWins()
    { var s = Store(); var p = new Vector3Int(60, -30, 20); s.Get(p); s.Set(p, 0.75f); Near(s.Get(p), 0.75f); Near(s.Get(p + Vector3Int.up), OriginalDensity(p + Vector3Int.up), 0f); }
    private static void BoundedCache()
    {
        var s = Store(); for (int x = 100; x < 40100; x++) s.Get(new Vector3Int(x, -30, 0));
        var f = typeof(TerrainDensityStore).GetField("_baseRockColumns", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert(f != null, "Install the performance density store.");
        var cache = (Dictionary<Vector2Int, float>)f.GetValue(s);
        Assert(cache.Count <= 32768, "unbounded cache"); Near(s.Get(new Vector3Int(100, -30, 0)), OriginalDensity(new Vector3Int(100, -30, 0)), 0f);
    }
    private static void SharedChunkSamples()
    {
        var s = Store(); s.Set(new Vector3Int(16, -31, 3), 0.5f);
        float[,,] left = s.MakeChunk(new Vector3Int(0, -2, 0)), right = s.MakeChunk(new Vector3Int(1, -2, 0));
        for (int y = 0; y <= 16; y++) for (int z = 0; z <= 16; z++) Near(left[16, y, z], right[0, y, z], 0f);
    }
    private static void SolidVolume()
    { var m = new TerrainVolume().SampleCell(Vector3Int.zero, p => -1f); Near(m.Volume, 1f); Near(m.Moment.x, 0.5f); Near(m.Moment.y, 0.5f); Near(m.Moment.z, 0.5f); }
    private static void EmptyVolume()
    { var m = new TerrainVolume().SampleCell(Vector3Int.zero, p => 0f); Near(m.Volume, 0f); Near(m.Moment.sqrMagnitude, 0f); }
    private static void PartialVolume()
    { var m = new TerrainVolume().SampleCell(Vector3Int.zero, p => p.x - 0.25f); Near(m.Volume, 0.25f); Near(m.Moment.x, 0.03125f); }
    private static void VolumeConservation()
    {
        var volume = new TerrainVolume(); float removed = 0f, previous = 1f;
        for (int i = 1; i <= 20; i++)
        { float cut = 1f - i / 20f; float current = volume.SampleCell(Vector3Int.zero, p => p.x - cut).Volume; removed += previous - current; previous = current; }
        Near(removed, 1f);
    }
    private static float[,,] Plane(float top = 1f)
    { var d = new float[3, 2, 2]; for (int x = 0; x < 3; x++) for (int y = 0; y < 2; y++) for (int z = 0; z < 2; z++) d[x, y, z] = y == 0 ? -1f : top; return d; }
    private static void ReusedMesh()
    {
        var size = new Vector3Int(2, 1, 1); var scratch = new TerrainMeshBuilder.Result();
        var fresh = TerrainMeshBuilder.Build(Plane(), size, 0.25f, Vector3Int.zero, p => p.x == 0 ? 1 : 4);
        var reused = TerrainMeshBuilder.Build(Plane(), size, 0.25f, Vector3Int.zero, p => p.x == 0 ? 1 : 4, scratch);
        Assert(ReferenceEquals(reused, scratch), "did not reuse buffer");
        Assert(fresh.Vertices.Count == reused.Vertices.Count && fresh.Triangles.Count == reused.Triangles.Count, "changed counts");
        for (int i = 0; i < fresh.Vertices.Count; i++) Assert(fresh.Vertices[i] == reused.Vertices[i], "changed vertices");
        foreach (var pair in fresh.Triangles)
        { List<int> b = reused.Triangles[pair.Key]; Assert(b.Count == pair.Value.Count, "changed triangle count"); for (int i = 0; i < b.Count; i++) Assert(b[i] == pair.Value[i], "changed triangles"); }
    }
    private static void ReusedAirMesh()
    {
        var size = new Vector3Int(2, 1, 1); var scratch = new TerrainMeshBuilder.Result();
        TerrainMeshBuilder.Build(Plane(), size, 1f, Vector3Int.zero, p => 1, scratch);
        var air = new float[3, 2, 2];
        TerrainMeshBuilder.Build(air, size, 1f, Vector3Int.zero, p => { throw new Exception("Air requested a material."); }, scratch);
        Assert(scratch.Vertices.Count == 0 && scratch.Triangles.Count == 0, "stale geometry");
    }
    private static void ReusedMaterials()
    {
        var size = new Vector3Int(2, 1, 1); var scratch = new TerrainMeshBuilder.Result();
        TerrainMeshBuilder.Build(Plane(), size, 1f, Vector3Int.zero, p => p.x == 0 ? 1 : 4, scratch);
        TerrainMeshBuilder.Build(Plane(), size, 1f, Vector3Int.zero, p => 2, scratch);
        Assert(scratch.Triangles.Count == 1 && scratch.Triangles.ContainsKey(2), "stale material");
    }
}
#endif
