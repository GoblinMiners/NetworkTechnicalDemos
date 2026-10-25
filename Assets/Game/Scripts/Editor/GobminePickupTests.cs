#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

/// <summary>Editor tests/diagnostics only. Never attach this class to a prefab.</summary>
public static class GobminePickupTests
{
    private static int _passed, _failed;

    [MenuItem("Tools/Gobmine/Pickups/Log Nearby Pickup Reasons")]
    public static void LogReasons()
    {
        if (World.Instance == null) { Debug.LogWarning("[Gobmine Pickups] Start a host and enter the mining scene first."); return; }
        Debug.Log(World.Instance.DescribeNearbyResourcePickups());
    }

    [MenuItem("Tools/Gobmine/Pickups/Run Pickup Rule Tests")]
    public static void RunRules()
    {
        if (EditorApplication.isPlaying) { Debug.LogWarning("Stop Play mode before running the rule tests."); return; }
        _passed = _failed = 0;
        Check("Within range", () => Assert(ResourcePickupRules.WithinReach(Vector3.zero, Vector3.right, 2f)));
        Check("Exact reach boundary", () => Assert(ResourcePickupRules.WithinReach(Vector3.zero, Vector3.right * 2f, 2f)));
        Check("Outside range", () => Assert(!ResourcePickupRules.WithinReach(Vector3.zero, Vector3.right * 2.01f, 2f)));
        Check("Below zero coordinates", () => Assert(ResourcePickupRules.WithinReach(new Vector3(-5,-20,-8), new Vector3(-4,-20,-8), 2f)));
        Check("NaN player rejected", () => Assert(!ResourcePickupRules.WithinReach(new Vector3(float.NaN,0,0), Vector3.zero, 2f)));
        Check("Infinite pickup rejected", () => Assert(!ResourcePickupRules.WithinReach(Vector3.zero, new Vector3(0,float.PositiveInfinity,0), 2f)));
        Check("Infinite reach rejected", () => Assert(!ResourcePickupRules.WithinReach(Vector3.zero, Vector3.one, float.PositiveInfinity)));
        Check("Zero reach rejected", () => Assert(!ResourcePickupRules.WithinReach(Vector3.zero, Vector3.zero, 0f)));
        Check("Negative reach rejected", () => Assert(!ResourcePickupRules.WithinReach(Vector3.zero, Vector3.zero, -1f)));
        Check("Ordinary solid blocks", () => WithCollider(c => Assert(ResourcePickupRules.IsBlockingCollider(c))));
        Check("Name alone does not exempt a wall", () => WithCollider(c => { c.name = "Debris"; Assert(ResourcePickupRules.IsBlockingCollider(c)); }));
        Check("Disabled collider ignored", () => WithCollider(c => { c.enabled = false; Assert(!ResourcePickupRules.IsBlockingCollider(c)); }));
        Check("Trigger ignored", () => WithCollider(c => { c.isTrigger = true; Assert(!ResourcePickupRules.IsBlockingCollider(c)); }));
        Check("Rubble on Default ignored", () => WithCollider(c => { c.gameObject.AddComponent<DebrisCleanup>(); Assert(!ResourcePickupRules.IsBlockingCollider(c)); }));
        Check("Pickup own solid collider ignored", () => WithCollider(c => { c.gameObject.AddComponent<OrePickup>(); Assert(!ResourcePickupRules.IsBlockingCollider(c)); }));
        Check("Player collider ignored", () => WithCollider(c => { c.gameObject.AddComponent<Mirror.NetworkIdentity>(); c.gameObject.AddComponent<PlayerInventory>(); Assert(!ResourcePickupRules.IsBlockingCollider(c)); }));
        Check("Child rubble collider ignored", ChildCollider);
        Check("Resource removed once, fractional units preserved", ResourceAccounting);
        Finish("pickup rules (no network session or live physics simulation)");
    }

    private static void WithCollider(Action<BoxCollider> test)
    {
        var obj = new GameObject("Gobmine temporary pickup rule test");
        obj.hideFlags = HideFlags.HideAndDontSave;
        try { test(obj.AddComponent<BoxCollider>()); }
        finally { Object.DestroyImmediate(obj); }
    }
    private static void ChildCollider()
    {
        var root = new GameObject("Gobmine temporary rubble parent");
        root.hideFlags = HideFlags.HideAndDontSave;
        try
        {
            root.AddComponent<DebrisCleanup>();
            var child = new GameObject("Child collider");
            child.transform.SetParent(root.transform, false);
            Assert(!ResourcePickupRules.IsBlockingCollider(child.AddComponent<BoxCollider>()));
        }
        finally { Object.DestroyImmediate(root); }
    }
    private static void ResourceAccounting()
    {
        var store = new ResourcePileStore();
        long id = store.Add(2, 2, 0.375, Vector3.zero);
        ResourcePileStore.Pile pile;
        Assert(store.Remove(id, out pile) && Math.Abs(pile.Units - 0.375) < 1e-12);
        Assert(!store.TryGet(id, out pile) && !store.Remove(id, out pile));
    }

    [MenuItem("Tools/Gobmine/Pickups/Run Pickup Physics Tests (Play Mode)")]
    public static void RunPhysics()
    {
        if (!EditorApplication.isPlaying) { Debug.LogWarning("Enter Play mode first. Physics tests create a temporary isolated physics scene."); return; }
        _passed = _failed = 0;
        Scene testScene = SceneManager.CreateScene("Gobmine Pickup Tests " + Guid.NewGuid().ToString("N"),
            new CreateSceneParameters(LocalPhysicsMode.Physics3D));
        var objects = new List<GameObject>();
        PhysicsScene physics = testScene.GetPhysicsScene();
        Vector3 from = new Vector3(-3f, 0f, 0f), to = new Vector3(3f, 0f, 0f);
        RaycastHit[] hits = new RaycastHit[1];
        try
        {
            Check("Empty path", () => Assert(!ResourcePickupRules.HasSolidObstacle(physics, from, to, ref hits)));
            Check("Real wall blocks", () =>
            {
                Box(testScene, objects, Vector3.zero);
                Physics.SyncTransforms();
                Assert(ResourcePickupRules.HasSolidObstacle(physics, from, to, ref hits));
            });
            Clear(objects);
            Check("Default-layer rubble does not block", () =>
            {
                GameObject obj = Box(testScene, objects, Vector3.zero);
                obj.AddComponent<DebrisCleanup>(); obj.GetComponent<Rigidbody>().isKinematic = true;
                Physics.SyncTransforms();
                Assert(!ResourcePickupRules.HasSolidObstacle(physics, from, to, ref hits));
            });
            Clear(objects);
            Check("Wall behind rubble still blocks", () =>
            {
                GameObject rubble = Box(testScene, objects, new Vector3(-1f,0,0));
                rubble.AddComponent<DebrisCleanup>(); rubble.GetComponent<Rigidbody>().isKinematic = true;
                Box(testScene, objects, new Vector3(1f,0,0));
                hits = new RaycastHit[1]; Physics.SyncTransforms();
                Assert(ResourcePickupRules.HasSolidObstacle(physics, from, to, ref hits));
            });
            Clear(objects);
            Check("Full cosmetic-hit buffer grows and rechecks", () =>
            {
                for (int i=0;i<3;i++)
                {
                    GameObject rubble = Box(testScene, objects, new Vector3(i - 1f,0,0));
                    rubble.transform.localScale = Vector3.one * 0.3f;
                    rubble.AddComponent<DebrisCleanup>(); rubble.GetComponent<Rigidbody>().isKinematic = true;
                }
                hits = new RaycastHit[1]; Physics.SyncTransforms();
                Assert(!ResourcePickupRules.HasSolidObstacle(physics, from, to, ref hits));
                Assert(hits.Length >= 4);
            });
            Clear(objects);
            Check("Wall on Ignore Raycast still blocks", () =>
            {
                Box(testScene, objects, Vector3.zero).layer = 2;
                Physics.SyncTransforms();
                Assert(ResourcePickupRules.HasSolidObstacle(physics, from, to, ref hits));
            });
            Clear(objects);
            Check("Non-trigger ore box does not block", () =>
            {
                GameObject obj = Box(testScene, objects, Vector3.zero);
                obj.AddComponent<OrePickup>(); obj.GetComponent<Rigidbody>().isKinematic = true;
                Physics.SyncTransforms();
                Assert(!ResourcePickupRules.HasSolidObstacle(physics, from, to, ref hits));
            });
            Clear(objects);
            Check("Trigger volumes do not block", () =>
            {
                Box(testScene, objects, Vector3.zero).GetComponent<BoxCollider>().isTrigger = true;
                Physics.SyncTransforms();
                Assert(!ResourcePickupRules.HasSolidObstacle(physics, from, to, ref hits));
            });
        }
        finally
        {
            Clear(objects);
            if (testScene.IsValid()) SceneManager.UnloadSceneAsync(testScene);
        }
        Finish("isolated Unity pickup raycasts (not multiplayer/inventory integration)");
    }
    private static GameObject Box(Scene scene, List<GameObject> objects, Vector3 position)
    {
        var obj = new GameObject("Gobmine pickup test obstacle");
        SceneManager.MoveGameObjectToScene(obj, scene);
        obj.transform.position = position;
        obj.AddComponent<BoxCollider>();
        objects.Add(obj);
        return obj;
    }
    private static void Clear(List<GameObject> objects)
    {
        foreach (GameObject obj in objects) if (obj != null) { obj.SetActive(false); Object.Destroy(obj); }
        objects.Clear();
    }
    private static void Check(string name, Action test)
    {
        try { test(); _passed++; Debug.Log("PASS: " + name); }
        catch (Exception ex) { _failed++; Debug.LogError("FAIL: " + name + "\n" + ex); }
    }
    private static void Assert(bool value) { if (!value) throw new Exception("Assertion failed."); }
    private static void Finish(string kind)
    {
        Debug.Log($"[Gobmine Pickup Tests] {_passed} passed; {_failed} failed: {kind}.");
        if (_failed > 0) throw new Exception("Pickup regression failure. Copy the complete Console output.");
    }
}
#endif
