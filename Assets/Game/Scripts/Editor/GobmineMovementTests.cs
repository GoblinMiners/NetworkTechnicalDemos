#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

/// <summary>Editor menu tests; physics checks run in a temporary isolated Play Mode scene.</summary>
public static class GobmineMovementTests
{
    private const string Menu = "Tools/Gobmine/Movement/";
    private static int _passed, _failed;
    private static readonly BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    [MenuItem(Menu + "Run Movement Rule Tests")]
    public static void RunRules()
    {
        _passed = _failed = 0;
        Check("Jump launch speed matches configured height", () =>
        {
            float speed = PlayerMotorRules.JumpSpeed(1.1f, -24f);
            Near(speed * speed / 48f, 1.1f, 0.0001f);
        });
        Check("Invalid gravity rejected", () =>
        {
            bool rejected = false;
            try { PlayerMotorRules.JumpSpeed(1f, 0f); } catch (ArgumentOutOfRangeException) { rejected = true; }
            Assert(rejected);
        });
        Check("Diagonal speed capped", () => Near(PlayerMotorRules.HorizontalMotion(Vector2.one, Vector3.right, Vector3.forward, 5f).magnitude, 5f));
        Check("Analog input magnitude preserved", () => Near(PlayerMotorRules.HorizontalMotion(new Vector2(0.25f, 0f), Vector3.right, Vector3.forward, 4f).magnitude, 1f));
        Check("Buffered press consumed once", () =>
        {
            var b = new PlayerJumpBuffer(); b.Press(1f); b.Grounded(1.05f);
            Assert(b.CanConsume(1.05f, 0.12f, 0.1f)); b.Consume(); Assert(!b.CanConsume(1.05f, 0.12f, 0.1f));
        });
        Check("Coyote jump accepted", () =>
        {
            var b = new PlayerJumpBuffer(); b.Grounded(2f); b.Press(2.05f); Assert(b.CanConsume(2.06f, 0.12f, 0.1f));
        });
        Check("Expired coyote window rejected", () =>
        {
            var b = new PlayerJumpBuffer(); b.Grounded(2f); b.Press(2.2f); Assert(!b.CanConsume(2.21f, 0.12f, 0.1f));
        });
        Check("Expired press rejected", () =>
        {
            var b = new PlayerJumpBuffer(); b.Press(2f); b.Grounded(2.5f); Assert(!b.CanConsume(2.5f, 0.12f, 0.1f));
        });
        Check("Second mid-air press is not double jump", () =>
        {
            var b = new PlayerJumpBuffer(); b.Grounded(2f); b.Press(2f); b.Consume(); b.Press(2.02f);
            Assert(!b.CanConsume(2.02f, 0.12f, 0.1f));
        });
        Check("Next landing rearms jump", () =>
        {
            var b = new PlayerJumpBuffer(); b.Grounded(2f); b.Press(2f); b.Consume(); b.Press(3f); b.Grounded(3.04f);
            Assert(b.CanConsume(3.04f, 0.12f, 0.1f));
        });
        Check("Reset clears pending jump", () =>
        {
            var b = new PlayerJumpBuffer(); b.Grounded(2f); b.Press(2f); b.Reset(); Assert(!b.CanConsume(2f, 0.12f, 0.1f));
        });
        Check("Swept bounds cover body and downward displacement", () =>
        {
            Bounds b = PlayerMotorRules.SweptBounds(new Vector3(3.9f, 1f, 0f), 0.5f, 2f, new Vector3(0.5f, -1f, 0f), 0.3f, 0.15f, 0.05f);
            Assert(b.min.y < -1f && b.max.x > 4.8f && b.max.y > 2.2f);
        });
        Check("Negative chunk coordinates use floor not truncation", () =>
        {
            Vector3Int low, high;
            Assert(PlayerMotorRules.ChunkRange(new Bounds(new Vector3(-0.1f, -0.1f, -0.1f), Vector3.one * 0.1f), Vector3.one * 4f, 64, out low, out high));
            Assert(low == new Vector3Int(-1, -1, -1) && high == low);
        });
        Check("Boundary sweep includes both chunk owners", () =>
        {
            Vector3Int low, high;
            Assert(PlayerMotorRules.ChunkRange(new Bounds(new Vector3(4f, 2f, 2f), Vector3.one), Vector3.one * 4f, 64, out low, out high));
            Assert(low.x == 0 && high.x == 1);
        });
        Check("Oversized guard queries fail closed", () =>
        {
            Vector3Int low, high;
            Assert(!PlayerMotorRules.ChunkRange(new Bounds(Vector3.zero, Vector3.one * 1000f), Vector3.one * 4f, 64, out low, out high));
        });
        Check("Solid-cell penetration test does not flag partial surfaces", () =>
        {
            Assert(TerrainQuery.CellState(Vector3Int.zero, p => -1f) == TerrainCellState.Solid);
            Assert(TerrainQuery.CellState(Vector3Int.zero, p => p.y == 0 ? -1f : 1f) == TerrainCellState.Surface);
        });
        Finish("movement rules, not Unity physics or multiplayer");
    }

    [MenuItem(Menu + "Run Movement Physics Tests (Play Mode)")]
    public static void RunPhysics()
    {
        if (!EditorApplication.isPlaying)
        { Debug.LogWarning("Enter Play mode first. Tests use an isolated physics scene, not your terrain/save."); return; }
        _passed = _failed = 0;
        Scene scene = SceneManager.CreateScene("Gobmine Movement Tests " + Guid.NewGuid().ToString("N"),
            new CreateSceneParameters(LocalPhysicsMode.Physics3D));
        var objects = new List<GameObject>();
        var meshes = new List<Mesh>();
        try
        {
            Check("Actual motor lands on a floor", () =>
            {
                Box(scene, objects, new Vector3(0f, -0.5f, 0f), new Vector3(20f, 1f, 20f));
                PlayerMovement p = Motor(scene, objects, new Vector3(0f, 4f, 0f));
                Steps(p, Vector3.zero, 200); Assert(p.IsGrounded); Near(p.transform.position.y, 1f, 0.15f);
            }); Clear(objects, meshes);
            Check("Actual motor stops at a solid wall", () =>
            {
                Box(scene, objects, new Vector3(0f, -0.5f, 0f), new Vector3(20f, 1f, 20f));
                Box(scene, objects, new Vector3(2.5f, 2f, 0f), new Vector3(0.25f, 4f, 5f));
                PlayerMovement p = Motor(scene, objects, new Vector3(0f, 1.05f, 0f));
                Steps(p, Vector3.right * 3f, 250); Assert(p.transform.position.x > 1.5f && p.transform.position.x < 2.1f);
            }); Clear(objects, meshes);
            Check("Actual motor jumps and lands", () =>
            {
                Box(scene, objects, new Vector3(0f, -0.5f, 0f), new Vector3(20f, 1f, 20f));
                PlayerMovement p = Motor(scene, objects, new Vector3(0f, 1.05f, 0f));
                Steps(p, Vector3.zero, 40); float start = p.transform.position.y; PressJump(p);
                float peak = start;
                for (int i = 0; i < 120; i++) { Step(p, Vector3.zero); peak = Mathf.Max(peak, p.transform.position.y); }
                Near(peak - start, 1.1f, 0.15f); Assert(p.IsGrounded);
            }); Clear(objects, meshes);
            Check("Low ceiling cancels upward jump", () =>
            {
                Box(scene, objects, new Vector3(0f, -0.5f, 0f), new Vector3(20f, 1f, 20f));
                Box(scene, objects, new Vector3(0f, 2.6f, 0f), new Vector3(20f, 0.4f, 20f));
                PlayerMovement p = Motor(scene, objects, new Vector3(0f, 1.05f, 0f));
                Steps(p, Vector3.zero, 40); PressJump(p); float peak = p.transform.position.y;
                for (int i = 0; i < 120; i++) { Step(p, Vector3.zero); peak = Mathf.Max(peak, p.transform.position.y); }
                Assert(peak < 1.6f && p.IsGrounded);
            }); Clear(objects, meshes);
            Check("Small step can be traversed", () =>
            {
                Box(scene, objects, new Vector3(0f, -0.5f, 0f), new Vector3(20f, 1f, 20f));
                Box(scene, objects, new Vector3(1f, 0.125f, 0f), new Vector3(2f, 0.25f, 5f));
                PlayerMovement p = Motor(scene, objects, new Vector3(-1f, 1.05f, 0f));
                Steps(p, Vector3.zero, 30); Steps(p, Vector3.right * 2f, 90);
                Assert(p.transform.position.x > 0.5f && p.transform.position.y > 1.08f);
            }); Clear(objects, meshes);
            Check("Removing a real floor permits falling", () =>
            {
                GameObject floor = Box(scene, objects, new Vector3(0f, -0.5f, 0f), new Vector3(20f, 1f, 20f));
                PlayerMovement p = Motor(scene, objects, new Vector3(0f, 1.05f, 0f));
                Steps(p, Vector3.zero, 40); floor.SetActive(false); Physics.SyncTransforms();
                Steps(p, Vector3.zero, 80); Assert(!p.IsGrounded && p.transform.position.y < -2f);
            }); Clear(objects, meshes);
            Check("Fast fall hits actual marching-cubes floor", () =>
            {
                TerrainFloor(scene, objects, meshes, new Vector3(-2f, -0.125f, -2f));
                PlayerMovement p = Motor(scene, objects, new Vector3(0f, 4f, 0f));
                Field(p, "_verticalVelocity", -25f); Steps(p, Vector3.zero, 60);
                Assert(p.IsGrounded); Near(p.transform.position.y, 1f, 0.15f);
            }); Clear(objects, meshes);
            Check("Walk across two marching-cubes collider seams", () =>
            {
                TerrainFloor(scene, objects, meshes, new Vector3(-4f, -0.125f, -2f));
                TerrainFloor(scene, objects, meshes, new Vector3(0f, -0.125f, -2f));
                PlayerMovement p = Motor(scene, objects, new Vector3(-1f, 1.05f, 0f));
                Steps(p, Vector3.zero, 30); Steps(p, Vector3.right * 2f, 100);
                Assert(p.transform.position.x > 0.8f && p.IsGrounded); Near(p.transform.position.y, 1f, 0.15f);
            });
        }
        finally { Clear(objects, meshes); if (scene.IsValid()) SceneManager.UnloadSceneAsync(scene); }
        Finish("isolated Unity motor/collision tests; does NOT validate streaming, networking, prefab setup or live mining");
    }

    private static GameObject Make(Scene scene, List<GameObject> objects, string name, Vector3 position)
    {
        var go = new GameObject(name); SceneManager.MoveGameObjectToScene(go, scene); go.transform.position = position;
        objects.Add(go); return go;
    }
    private static GameObject Box(Scene scene, List<GameObject> objects, Vector3 position, Vector3 size)
    {
        GameObject go = Make(scene, objects, "Temporary movement obstacle", position);
        go.AddComponent<BoxCollider>().size = size; Physics.SyncTransforms(); return go;
    }
    private static PlayerMovement Motor(Scene scene, List<GameObject> objects, Vector3 position)
    {
        GameObject go = Make(scene, objects, "Temporary movement motor", position);
        go.AddComponent<Mirror.NetworkIdentity>();
        CharacterController c = go.AddComponent<CharacterController>();
        c.height = 2f; c.radius = 0.4f; c.center = Vector3.zero; c.skinWidth = 0.04f;
        c.stepOffset = 0.3f; c.slopeLimit = 50f; c.minMoveDistance = 0f;
        c.detectCollisions = true; c.enableOverlapRecovery = true;
        PlayerMovement p = go.AddComponent<PlayerMovement>();
        p.guardTerrainStreaming = false; p.rollbackSolidPenetration = false;
        Field(p, "_controller", c); Field(p, "_configuredStepOffset", 0.3f); Field(p, "_collisionMask", ~0);
        Physics.SyncTransforms(); return p;
    }
    private static void TerrainFloor(Scene scene, List<GameObject> objects, List<Mesh> meshes, Vector3 position)
    {
        var size = new Vector3Int(16, 1, 16); var d = new float[17, 2, 17];
        for (int x = 0; x < 17; x++) for (int y = 0; y < 2; y++) for (int z = 0; z < 17; z++) d[x, y, z] = y == 0 ? -1f : 1f;
        TerrainMeshBuilder.Result result = TerrainMeshBuilder.Build(d, size, 0.25f, Vector3Int.zero, cell => 1);
        var mesh = new Mesh { name = "Temporary marching cubes collision test" }; meshes.Add(mesh);
        mesh.SetVertices(result.Vertices); mesh.SetTriangles(result.Triangles[1], 0); mesh.RecalculateBounds();
        GameObject go = Make(scene, objects, "Temporary voxel floor", position);
        go.AddComponent<MeshCollider>().sharedMesh = mesh; Physics.SyncTransforms();
    }
    private static void Field(PlayerMovement p, string name, object value)
        => typeof(PlayerMovement).GetField(name, PrivateInstance).SetValue(p, value);
    private static void PressJump(PlayerMovement p)
        => ((PlayerJumpBuffer)typeof(PlayerMovement).GetField("_jumpBuffer", PrivateInstance).GetValue(p)).Press(Time.time);
    private static void Step(PlayerMovement p, Vector3 velocity)
        => typeof(PlayerMovement).GetMethod("MoveStep", PrivateInstance).Invoke(p, new object[] { null, velocity, 0.01f, false });
    private static void Steps(PlayerMovement p, Vector3 velocity, int count) { for (int i = 0; i < count; i++) Step(p, velocity); }
    private static void Clear(List<GameObject> objects, List<Mesh> meshes)
    {
        foreach (GameObject go in objects) if (go != null) { go.SetActive(false); Object.Destroy(go); }
        objects.Clear(); Physics.SyncTransforms();
        foreach (Mesh mesh in meshes) if (mesh != null) Object.Destroy(mesh); meshes.Clear();
    }
    private static void Check(string name, Action action)
    {
        try { action(); _passed++; Debug.Log("[Gobmine Movement TEST PASS] " + name); }
        catch (Exception ex) { _failed++; Debug.LogError("[Gobmine Movement TEST FAIL] " + name + ": " + (ex.InnerException ?? ex)); }
    }
    private static void Assert(bool test) { if (!test) throw new InvalidOperationException("Assertion failed"); }
    private static void Near(float a, float b, float epsilon = 0.001f) { Assert(Mathf.Abs(a - b) <= epsilon); }
    private static void Finish(string scope) => Debug.Log($"[Gobmine Movement TESTS] {_passed} passed, {_failed} failed: {scope}");
}
#endif
