#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class GobminePerformanceTools
{
    [MenuItem("Tools/Gobmine/Performance/Apply Balanced Scene Settings")]
    public static void ApplyBalancedSettings()
    {
        if (EditorApplication.isPlaying) throw new InvalidOperationException("Stop play mode first, then save the scene after applying settings.");
        if (!EditorUtility.DisplayDialog("Apply balanced mining settings?",
            "Changes World and DebrisPool settings in the loaded scenes. Sets view distance 4, density budget 1024, " +
            "64-sample batches, 1m rubble buckets (at 0.25m voxels), 128 active debris, an 8-second lifetime and oldest-rubble recycling. " +
            "Resources are not recycled. Does not change seeds, voxel size, mining hardness, saves, prefab assets, layers or transforms. Undo is available.",
            "Apply", "Cancel")) return;
        int worlds = 0, pools = 0;
        foreach (World world in UnityEngine.Object.FindObjectsByType<World>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (!world.gameObject.scene.IsValid() || !world.gameObject.scene.isLoaded) continue;
            Undo.RecordObject(world, "Gobmine performance settings");
            world.viewDistanceInChunks = 4; world.sleepDistanceInChunks = 6; world.chunkBuildsPerFrame = 1;
            world.densityChangesPerFrame = 1024; world.removalBatchSize = 64; world.removalMillisecondsPerFrame = 3f;
            world.structuralOperationsPerFrame = 2048; world.structuralMillisecondsPerFrame = 1.5f;
            world.meshRebuildsPerFrame = 4; world.meshMillisecondsPerFrame = 3f;
            world.debrisBucketSizeInCells = 4; world.debrisSpawnsPerFrame = 4; world.debrisBatchDelaySeconds = 0.06f;
            world.maxQueuedMiningRequestsPerPlayer = 4; world.maxVisibleResourcePickups = 32;
            PrefabUtility.RecordPrefabInstancePropertyModifications(world);
            EditorUtility.SetDirty(world); EditorSceneManager.MarkSceneDirty(world.gameObject.scene); worlds++;
            if (world.transform.position != Vector3.zero || world.transform.rotation != Quaternion.identity || world.transform.lossyScale != Vector3.one)
                Debug.LogWarning("World's expected transform is position/rotation zero and scale one. Reset it before testing; this tool does not move scene objects.", world);
        }
        foreach (DebrisPool pool in UnityEngine.Object.FindObjectsByType<DebrisPool>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (!pool.gameObject.scene.IsValid() || !pool.gameObject.scene.isLoaded) continue;
            Undo.RecordObject(pool, "Gobmine debris performance settings");
            var s = new SerializedObject(pool);
            s.FindProperty("initialPoolSize").intValue = 64;
            s.FindProperty("autoDespawnTime").floatValue = 8f;
            s.FindProperty("maxActiveDebris").intValue = 128;
            s.FindProperty("recycleOldestWhenFull").boolValue = true;
            s.FindProperty("prewarmPerFrame").intValue = 8;
            s.FindProperty("poolRemoteClients").boolValue = true;
            s.FindProperty("maxClientPooledDebris").intValue = 128;
            s.FindProperty("disableDebrisShadows").boolValue = true;
            s.ApplyModifiedProperties();
            PrefabUtility.RecordPrefabInstancePropertyModifications(pool);
            EditorUtility.SetDirty(pool); EditorSceneManager.MarkSceneDirty(pool.gameObject.scene); pools++;
        }
        Debug.Log($"[Gobmine Performance] Updated {worlds} World and {pools} DebrisPool component(s). Save the scene. No scenes, prefabs or world saves were replaced.");
    }

    [MenuItem("Tools/Gobmine/Performance/Log Runtime Counters")]
    public static void LogCounters()
    {
        if (!EditorApplication.isPlaying) throw new InvalidOperationException("Enter play mode and start a host/client first.");
        World w = World.Instance; DebrisPool p = DebrisPool.Instance;
        Debug.Log("[Gobmine Performance]\n" +
            $"FPS estimate: {(Time.unscaledDeltaTime > 0f ? 1f / Time.unscaledDeltaTime : 0f):F1}; Server: {Mirror.NetworkServer.active}\n" +
            (w == null ? "World: missing\n" :
                $"Mining requests: {w.PendingMiningRequests}; rejected overload requests: {w.RejectedMiningRequests}\n" +
                $"Dirty meshes: {w.PendingMeshRebuilds}; structural work pending: {w.HasPendingStructuralWork}\n" +
                $"Pending rubble buckets: {w.PendingDebrisBuckets}; active chunks: {w.ActiveChunkCount}; queued chunks: {w.PendingChunkBuilds}\n" +
                $"Last removal: {w.LastRemovalMilliseconds:F2} ms; last dirty-mesh work: {w.LastMeshMilliseconds:F2} ms\n" +
                $"Uncollected resource records: {w.UncollectedResourcePiles}\n") +
            (p == null ? "DebrisPool: missing" :
                $"Server active rubble: {p.ActiveCount}; server inactive pool: {p.InactiveCount}; client inactive pool: {p.ClientInactiveCount}\n" +
                $"Server instances created: {p.ServerInstancesCreated}; client instances created: {p.ClientInstancesCreated}; capacity recycles: {p.RecycledForCapacity}"));
    }
}
#endif
