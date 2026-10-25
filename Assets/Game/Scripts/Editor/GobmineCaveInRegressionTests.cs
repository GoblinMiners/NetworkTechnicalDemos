#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

/// <summary>Algorithm tests. These do not replace Unity physics/Mirror play-mode tests.</summary>
public static class GobmineCaveInRegressionTests
{
    private static int _passed, _failed;
    [MenuItem("Tools/Gobmine/Cave-ins/Run Cave-in Regression Tests")]
    public static void Run()
    {
        if (EditorApplication.isPlaying) throw new InvalidOperationException("Stop Play mode before algorithm tests.");
        _passed = _failed = 0;
        Check("Zero thickness disables thin-roof rule", Disabled);
        Check("Thickness uses interpolated surfaces", Interpolation);
        Check("One metre is not two samples at 0.25m", Metres);
        Check("Thicker separation remains intact", Thick);
        Check("Infinite solid below a floor is not a roof", SolidMass);
        Check("Thin patch is found from below", FromBelow);
        Check("Thin patch is found from above", FromAbove);
        Check("Lateral propagation removes the whole finite weak patch", Propagation);
        Check("Below-zero and chunk-edge patches work", BelowZero);
        Check("Already queued spans are not duplicated", Deduplicate);
        Check("Changes during a scan are retained", ChangedDuringScan);
        Check("Fractional weakening can trigger collapse without a sign change", Fractional);
        Check("Negative Y band boundaries are scanned", NegativeBand);
        Check("Fresh checks are promoted ahead of old queued columns", Priority);
        Check("One-sample allowance alternates roof and island work", SingleSampleFairness);
        Check("A mesh fence waits for its required version", FenceWait);
        Check("A later dirty edit cannot block old rubble", FenceLaterEdit);
        Check("A new rubble request waits for the newer mesh", FenceNewEdit);
        Check("Clearing mesh fences removes old completion", FenceReset);
        Check("Falling columns can hit a player crossed between ticks", SweptHit);
        Check("Player beside the collapse is not hit", Beside);
        Check("Player above the collapse is not hit", Above);
        Check("An intact intermediate floor blocks damage", Shield);
        Check("Air between a fall and player does not block it", ClearPath);
        Check("Grid-aligned shielding catches a very thin intermediate layer", ThinShield);
        Debug.Log($"[Gobmine Cave-in Tests] {_passed} passed; {_failed} failed. Algorithm tests only, not rendering, physics, input or Mirror networking tests.");
        if (_failed > 0) throw new Exception("Cave-in regression failure. Send the full Console output.");
    }
    private static void Check(string name, Action action)
    { try { action(); _passed++; Debug.Log("PASS: " + name); } catch (Exception e) { _failed++; Debug.LogError("FAIL: " + name + "\n" + e); } }
    private static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Near(float a, float b) { Assert(Mathf.Abs(a - b) < 0.0001f, $"Expected {b}; got {a}"); }
    private static Func<Vector3Int, float> Slab(int bottom, int top) => p => p.y >= bottom && p.y <= top ? -5f : 1f;
    private static void Disabled()
    { TerrainCeilingScanner.Span s; Assert(!TerrainCeilingScanner.TryFindSpan(Vector3Int.zero, Slab(0, 0), 0f, out s), "zero was ignored"); }
    private static void Interpolation()
    { TerrainCeilingScanner.Span s; Assert(TerrainCeilingScanner.TryFindSpan(Vector3Int.zero, Slab(0, 1), 3f, out s), "not found"); Near(s.Thickness, 8f / 3f); }
    private static void Metres()
    {
        TerrainCeilingScanner.Span s;
        Assert(!TerrainCeilingScanner.TryFindSpan(Vector3Int.zero, Slab(0, 1), 0.5f / 0.25f, out s), "old threshold wrongly accepted 0.667m");
        Assert(TerrainCeilingScanner.TryFindSpan(Vector3Int.zero, Slab(0, 1), 1f / 0.25f, out s), "1m threshold failed");
    }
    private static void Thick()
    { TerrainCeilingScanner.Span s; Assert(!TerrainCeilingScanner.TryFindSpan(Vector3Int.zero, Slab(0, 3), 4f, out s), "1.167m slab collapsed at 1m"); }
    private static void SolidMass()
    { TerrainCeilingScanner.Span s; Assert(!TerrainCeilingScanner.TryFindSpan(Vector3Int.zero, p => p.y <= 0 ? -5f : 1f, 4f, out s), "bedrock collapsed"); }
    private static Func<Vector3Int, float> Patch(int centerY, int centerX = 0)
    {
        return p => Mathf.Abs(p.x - centerX) <= 2 && Mathf.Abs(p.z) <= 2 &&
            (p.y < centerY || p.y > centerY + 1) ? 1f : -5f;
    }
    private static HashSet<Vector3Int> Drain(TerrainCeilingScanner scanner, int maximumSteps = 10000)
    {
        var result = new HashSet<Vector3Int>();
        for (int i = 0; i < maximumSteps && scanner.HasPending; i++)
        {
            scanner.Step(8);
            List<Vector3Int> span;
            while (scanner.TryTakeCollapse(out span))
                foreach (Vector3Int p in span) Assert(result.Add(p), "duplicate scheduled sample: " + p);
        }
        Assert(!scanner.HasPending, "scanner did not finish within the test allowance");
        return result;
    }
    private static HashSet<Vector3Int> FindPatch(int y, Vector3Int change, int centerX = 0)
    {
        var scanner = new TerrainCeilingScanner(Patch(y, centerX), 4f);
        scanner.NotifyChange(change, true);
        return Drain(scanner);
    }
    private static void FromBelow() { Assert(FindPatch(0, Vector3Int.down).Count == 50, "missed the patch from below"); }
    private static void FromAbove() { Assert(FindPatch(0, Vector3Int.up * 2).Count == 50, "missed the patch from above"); }
    private static void Propagation()
    { var found = FindPatch(0, Vector3Int.down); Assert(found.Contains(new Vector3Int(2, 1, 2)) && found.Count == 50, "patch stopped at the brush"); }
    private static void BelowZero() { Assert(FindPatch(-20, new Vector3Int(16, -21, 0), 16).Count == 50, "seam/negative coordinates failed"); }
    private static void Deduplicate()
    {
        var scanner = new TerrainCeilingScanner(Patch(0), 4f);
        for (int i = 0; i < 20; i++) scanner.NotifyChange(Vector3Int.down, true);
        Assert(Drain(scanner).Count == 50, "duplicate notifications changed output");
        scanner.NotifyChange(Vector3Int.down, true);
        Assert(Drain(scanner).Count == 0, "pending removal was scheduled again");
    }
    private static void ChangedDuringScan()
    {
        var scanner = new TerrainCeilingScanner(Patch(0), 4f);
        scanner.NotifyChange(new Vector3Int(100, 0, 0), false); scanner.Step(1);
        scanner.NotifyChange(Vector3Int.down, true);
        Assert(Drain(scanner).Count == 50, "lost a change received during work");
    }
    private static void Fractional()
    {
        float solid = -5f;
        Func<Vector3Int, float> field = p => Mathf.Abs(p.x) <= 2 && Mathf.Abs(p.z) <= 2
            ? (p.y == 0 || p.y == 1 ? solid : 1f) : -5f;
        var scanner = new TerrainCeilingScanner(field, 2f);
        scanner.NotifyChange(Vector3Int.zero, true); Assert(Drain(scanner).Count == 0, "initial slab was not thick");
        solid = -0.2f;
        scanner.NotifyChange(Vector3Int.zero, true); Assert(Drain(scanner).Count == 50, "fractional thinning was missed");
    }
    private static void NegativeBand() { Assert(FindPatch(-8, new Vector3Int(0, -9, 0)).Count == 50, "negative band division failed"); }
    private static void Priority()
    {
        var scanner = new TerrainCeilingScanner(Patch(0), 4f);
        for (int x = 100; x < 200; x++) scanner.NotifyChange(new Vector3Int(x, 0, 0), false);
        scanner.NotifyChange(Vector3Int.down, true);
        bool found = false;
        for (int i = 0; i < 300 && !found; i++) { scanner.Step(1); List<Vector3Int> s; found = scanner.TryTakeCollapse(out s); }
        Assert(found, "urgent mining waited behind the saved-world backlog");
    }
    private static void SingleSampleFairness()
    {
        var method = typeof(World).GetMethod("SplitCeilingQuota", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        Assert(method != null, "install all cave-in scripts");
        int first = (int)method.Invoke(null, new object[] { 1, true, true, false });
        int second = (int)method.Invoke(null, new object[] { 1, true, true, true });
        Assert(first == 0 && second == 1, "one lane can starve");
    }
    private static void FenceWait()
    { var f = new TerrainMeshFence(); f.MarkDirty(Vector3Int.zero); long r = f.Capture(Vector3Int.zero); Assert(!f.IsSatisfied(Vector3Int.zero, r), "premature completion"); f.MarkBuilt(Vector3Int.zero); Assert(f.IsSatisfied(Vector3Int.zero, r), "build not recorded"); }
    private static void FenceLaterEdit()
    { var f = new TerrainMeshFence(); f.MarkDirty(Vector3Int.zero); long old = f.Capture(Vector3Int.zero); f.MarkBuilt(Vector3Int.zero); f.MarkDirty(Vector3Int.zero); Assert(f.IsSatisfied(Vector3Int.zero, old), "later edit blocked old request"); }
    private static void FenceNewEdit()
    { var f = new TerrainMeshFence(); f.MarkDirty(Vector3Int.zero); f.MarkBuilt(Vector3Int.zero); f.MarkDirty(Vector3Int.zero); Assert(!f.IsSatisfied(Vector3Int.zero, f.Capture(Vector3Int.zero)), "new request used old mesh"); }
    private static void FenceReset()
    { var f = new TerrainMeshFence(); f.MarkDirty(Vector3Int.zero); long old = f.Capture(Vector3Int.zero); f.MarkBuilt(Vector3Int.zero); f.Clear(); Assert(!f.IsSatisfied(Vector3Int.zero, old), "stale completion survived reset"); }
    private static Bounds Roof() => new Bounds(new Vector3(0f, 4f, 0f), new Vector3(1f, 0.5f, 1f));
    private static Bounds Player() => new Bounds(new Vector3(0f, 1f, 0f), new Vector3(0.8f, 2f, 0.8f));
    private static void SweptHit() { Assert(CollapseFallMath.SweepsPlayer(Roof(), 1f, 5f, Player()), "fast fall skipped a player"); }
    private static void Beside() { Bounds b = Player(); b.center += Vector3.right * 2f; Assert(!CollapseFallMath.SweepsPlayer(Roof(), 0f, 5f, b), "sideways damage"); }
    private static void Above() { Bounds b = Player(); b.center += Vector3.up * 6f; Assert(!CollapseFallMath.SweepsPlayer(Roof(), 0f, 5f, b), "upward damage"); }
    private static void Shield() { Assert(CollapseFallMath.BlockedByTerrain(new Vector3(0, 5, 0), 2f, 0.0625f, p => p.y >= 3 && p.y <= 3.25f ? -1f : 1f), "damage crossed a solid floor"); }
    private static void ThinShield() { Assert(CollapseFallMath.BlockedByTerrain(new Vector3(0, 4.95f, 0), 2f, 0.25f, p => Mathf.Abs(p.y - 3f) < 0.001f ? -1f : 1f), "thin grid boundary was missed"); }
    private static void ClearPath() { Assert(!CollapseFallMath.BlockedByTerrain(new Vector3(0, 5, 0), 2f, 0.0625f, p => 1f), "air blocked damage"); }
}
#endif
