#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Scene-only settings and source-level regression checks. Does not overwrite a
/// World prefab, save file, network manager, ore catalogue, or gameplay scene.
/// </summary>
public static class GobmineFallingShapesResponsiveness
{
    [MenuItem("Tools/Gobmine/Falling Shapes/Apply Responsive Lifetimes (Open Scene)")]
    private static void ApplyRecommended()
    {
        if (Application.isPlaying)
        {
            Debug.LogError("[Gobmine Shapes] Leave Play mode before changing serialized settings.");
            return;
        }
        World[] worlds = UnityEngine.Object.FindObjectsByType<World>(FindObjectsSortMode.None);
        DebrisPool[] pools = UnityEngine.Object.FindObjectsByType<DebrisPool>(FindObjectsSortMode.None);
        if (worlds.Length == 0 || pools.Length == 0)
        {
            Debug.LogError("[Gobmine Shapes] Open the real mining scene (DemoLvl) containing World and DebrisPool first.");
            return;
        }
        foreach (World world in worlds)
        {
            Undo.RecordObject(world,"Set responsive falling-shape lifetimes");
            world.preserveDisconnectedShapes = true;
            world.ceilingShapeLifetime = 10f;
            world.detachedShapeLifetime = 20f;
            world.settledShapeLifetime = 5f;
            EditorUtility.SetDirty(world);
            EditorSceneManager.MarkSceneDirty(world.gameObject.scene);
            Debug.Log("[Gobmine Shapes] " + world.name +
                ": ceiling pieces 10 s, detached shapes 20 s maximum / 5 s after resting (0 keeps sculptures forever).",world);
        }
        foreach (DebrisPool pool in pools)
        {
            Undo.RecordObject(pool,"Set shorter pooled-debris lifetime");
            SerializedObject serialized = new SerializedObject(pool);
            SetFloat(serialized,"autoDespawnTime",3f);
            SetInt(serialized,"maxActiveDebris",96);
            SetBool(serialized,"recycleOldestWhenFull",true);
            serialized.ApplyModifiedProperties();
            EditorUtility.SetDirty(pool);
            EditorSceneManager.MarkSceneDirty(pool.gameObject.scene);
            Debug.Log("[Gobmine Shapes] " + pool.name +
                ": pooled cosmetic debris 3 s, 96 active maximum, recycle oldest on saturation.",pool);
        }
        Debug.Log("[Gobmine Shapes] Save the open scene with Ctrl+S. These changes do not alter your current world save.");
    }

    private static void SetFloat(SerializedObject obj,string name,float value)
    {
        SerializedProperty p = obj.FindProperty(name);
        if (p == null) throw new InvalidOperationException("Missing DebrisPool serialized field: " + name);
        p.floatValue = value;
    }
    private static void SetInt(SerializedObject obj,string name,int value)
    {
        SerializedProperty p = obj.FindProperty(name);
        if (p == null) throw new InvalidOperationException("Missing DebrisPool serialized field: " + name);
        p.intValue = value;
    }
    private static void SetBool(SerializedObject obj,string name,bool value)
    {
        SerializedProperty p = obj.FindProperty(name);
        if (p == null) throw new InvalidOperationException("Missing DebrisPool serialized field: " + name);
        p.boolValue = value;
    }

    [MenuItem("Tools/Gobmine/Falling Shapes/Log Live Shape And Debris Status")]
    private static void LogStatus()
    {
        World[] worlds = UnityEngine.Object.FindObjectsByType<World>(FindObjectsSortMode.None);
        foreach (World world in worlds)
        {
            world.LogFallingShapeCounters();
            Debug.Log("[Gobmine Debris] pendingBuckets=" + world.PendingDebrisBuckets +
                ", pendingOldestSeconds=" + world.OldestPendingDebrisSeconds.ToString("0.00") +
                ", discardedCosmetic=" + world.CosmeticDebrisCulled +
                ", lastSpawnDelaySeconds=" + world.LastDebrisSpawnLatencySeconds.ToString("0.00"),world);
        }
        foreach (DebrisPool pool in UnityEngine.Object.FindObjectsByType<DebrisPool>(FindObjectsSortMode.None))
        {
            SerializedObject s = new SerializedObject(pool);
            Debug.Log("[Gobmine DebrisPool] active=" + pool.ActiveCount + ", inactive=" + pool.InactiveCount +
                ", created=" + pool.ServerInstancesCreated + ", recycled=" + pool.RecycledForCapacity +
                ", configured=" + pool.IsConfigured + ", lifetime=" + s.FindProperty("autoDespawnTime").floatValue +
                " s, cap=" + s.FindProperty("maxActiveDebris").intValue,pool);
        }
    }

    [MenuItem("Tools/Gobmine/Falling Shapes/Run Lifetime And Collider Checks")]
    private static void RunChecks()
    {
        int tests = 0;
        try
        {
            Assert(!World.ShouldExpireShape(false,1000f,15f,0f),"permanent sculptures do not expire"); tests++;
            Assert(World.ShouldExpireShape(false,20f,10f,20f),"detached shape expires at limit"); tests++;
            Assert(!World.ShouldExpireShape(false,19.9f,10f,20f),"detached shape not early"); tests++;
            Assert(World.ShouldExpireShape(true,10f,10f,20f),"ceiling expires independently"); tests++;
            Assert(!World.ShouldExpireShape(true,500f,0f,20f),"zero ceiling lifetime is permanent"); tests++;
            Assert(World.ShouldExpireRestingShape(false,5f,10f,20f,5f),"resting chunk expires early"); tests++;
            Assert(!World.ShouldExpireRestingShape(false,600f,10f,0f,5f),"permanent sculpture not removed by sleep"); tests++;
            Assert(!World.ShouldExpireRestingShape(true,6f,10f,20f,0f),"resting cleanup can be disabled"); tests++;
            var fence = new TerrainMeshFence();
            Vector3Int location = new Vector3Int(2,-3,4);
            fence.MarkDirty(location);
            long pending = fence.Capture(location);
            Assert(!fence.IsSatisfied(location,pending),"pending collider version waits"); tests++;
            fence.MarkDirty(location); // Additional mining must not update our original target.
            fence.MarkBuilt(location);
            Assert(fence.IsSatisfied(location,pending),"later mesh build satisfies earlier shape fence"); tests++;
            var record = new TerrainShapeRecord();
            record.Samples.Add(new TerrainShapeSample(new Vector3Int(0,0,0),-1));
            record.Samples.Add(new TerrainShapeSample(new Vector3Int(1,0,0),-1));
            record.Samples.Add(new TerrainShapeSample(new Vector3Int(3,0,0),-1));
            var job = new TerrainShapeMeshJob(record,0.25f,grid=>1);
            List<Bounds> boxes = job.CreateCompoundBoxes(8);
            Assert(boxes.Count > 0 && boxes.Count <= 8,"bounded collider group count"); tests++;
            foreach (Bounds b in boxes)
                Assert(b.size.x > 0 && b.size.y > 0 && b.size.z > 0 && b.size.x < 2f,
                    "nonzero tight collider boxes"); tests++;
            Debug.Log("[Gobmine Shapes] " + tests + " isolated lifetime/collider/fence checks passed. " +
                "Unity host/server play-mode physics and Mirror replication still need testing.");
        }
        catch (Exception error)
        {
            Debug.LogError("[Gobmine Shapes] Check failed after " + tests + " passes: " + error);
        }
    }
    private static void Assert(bool pass,string message)
    {
        if (!pass) throw new Exception(message);
    }
}
#endif
