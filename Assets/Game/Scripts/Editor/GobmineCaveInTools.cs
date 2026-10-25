#if UNITY_EDITOR
using System;
using System.IO;
using Mirror;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class GobmineCaveInTools
{
    private const string Root = "Tools/Gobmine/Cave-ins/";
    [MenuItem(Root + "Install Cave-in Settings and Player Health")]
    public static void Install()
    {
        if (EditorApplication.isPlaying) throw new InvalidOperationException("Stop Play mode first.");
        string playerPath = "Assets/Game/Prefabs/Player.prefab";
        foreach (NetworkManager manager in UnityEngine.Object.FindObjectsByType<NetworkManager>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (manager.playerPrefab == null) continue;
            string path = AssetDatabase.GetAssetPath(manager.playerPrefab);
            if (!string.IsNullOrEmpty(path)) { playerPath = path; break; }
        }
        if (AssetDatabase.LoadAssetAtPath<GameObject>(playerPath) == null)
            throw new InvalidOperationException("Player prefab not found. Open the scene containing your NetworkManager and run this tool again.");
        if (!EditorUtility.DisplayDialog("Install cave-ins and player health?",
            "Adds GobminePlayerHealth once to " + playerPath + ". A prefab backup is written outside Assets. " +
            "Sets loaded Worlds to a 1m weak-layer threshold and responsive bounded cosmetic rubble; enables server cave-in damage. " +
            "Keeps current scenes, input/camera references, materials and world saves. Stop all clients and update every build before reconnecting.",
            "Install", "Cancel")) return;
        GameObject root = PrefabUtility.LoadPrefabContents(playerPath);
        try
        {
            foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
                if (GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(child.gameObject) > 0)
                    throw new InvalidOperationException("Player prefab still has a missing script on " + child.name + ". Repair it first; nothing was saved by this tool.");
            string backupFolder = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "GobmineBackups");
            Directory.CreateDirectory(backupFolder);
            string backup = Path.Combine(backupFolder, DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "-Player.prefab");
            File.Copy(playerPath, backup, false);
            if (File.Exists(playerPath + ".meta")) File.Copy(playerPath + ".meta", backup + ".meta", false);
            GobminePlayerHealth health = root.GetComponent<GobminePlayerHealth>();
            if (health == null) health = root.AddComponent<GobminePlayerHealth>();
            if (root.GetComponent<Collider>() == null)
                Debug.LogWarning("Player has no root collider. Health uses a fallback damage bounds; configure the actual player collider before final testing.");
            bool success;
            PrefabUtility.SaveAsPrefabAsset(root, playerPath, out success);
            if (!success) throw new InvalidOperationException("Unity could not save the Player prefab. Inspect the Console; backup: " + backup);
            Debug.Log("[Gobmine Cave-ins] Player health installed. Prefab backup: " + backup);
        }
        finally { PrefabUtility.UnloadPrefabContents(root); }
        int worlds = 0, pools = 0;
        foreach (World world in UnityEngine.Object.FindObjectsByType<World>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (!world.gameObject.scene.IsValid() || !world.gameObject.scene.isLoaded) continue;
            Undo.RecordObject(world, "Gobmine cave-in settings");
            world.ceilingCollapseThicknessMetres = 1f;
            world.ceilingChecksPerFrame = 256; world.ceilingMillisecondsPerFrame = 1f;
            world.collapseDamagesPlayers = true; world.maximumCollapseDamagePerHit = 40f;
            world.collapseDamagePerCubicMetre = 100f; world.collapseDamageCooldown = 0.5f;
            world.debrisBucketSizeInCells = 4; world.debrisSpawnsPerFrame = 4; world.debrisBatchDelaySeconds = 0.02f;
            world.maximumCosmeticDebrisAgeSeconds = 0.5f; world.maximumPendingCosmeticDebris = 256;
            world.maximumDebrisPieceSizeMetres = 0.35f;
            PrefabUtility.RecordPrefabInstancePropertyModifications(world);
            EditorUtility.SetDirty(world); EditorSceneManager.MarkSceneDirty(world.gameObject.scene); worlds++;
        }
        foreach (DebrisPool pool in UnityEngine.Object.FindObjectsByType<DebrisPool>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (!pool.gameObject.scene.IsValid() || !pool.gameObject.scene.isLoaded) continue;
            Undo.RecordObject(pool, "Gobmine responsive rubble");
            var serialized = new SerializedObject(pool);
            SetBool(serialized, "recycleOldestWhenFull", true);
            SetInt(serialized, "maxActiveDebris", 128);
            SetInt(serialized, "initialPoolSize", 64);
            SerializedProperty lifetime = serialized.FindProperty("autoDespawnTime");
            if (lifetime != null) lifetime.floatValue = 8f;
            serialized.ApplyModifiedProperties();
            PrefabUtility.RecordPrefabInstancePropertyModifications(pool);
            EditorUtility.SetDirty(pool); EditorSceneManager.MarkSceneDirty(pool.gameObject.scene); pools++;
        }
        Debug.Log($"[Gobmine Cave-ins] Updated {worlds} World and {pools} DebrisPool component(s). SAVE THE SCENE. Run the regression tests, then start a host. No world saves were deleted.");
        if (worlds == 0) Debug.LogWarning("No World in the loaded scene. Open the mining scene and rerun to apply its scene settings.");
    }
    private static void SetBool(SerializedObject s, string name, bool value)
    {
        SerializedProperty p = s.FindProperty(name);
        if (p == null) throw new InvalidOperationException("Missing " + name + ": install the earlier Performance Patch first.");
        p.boolValue = value;
    }
    private static void SetInt(SerializedObject s, string name, int value)
    {
        SerializedProperty p = s.FindProperty(name);
        if (p == null) throw new InvalidOperationException("Missing DebrisPool field: " + name);
        p.intValue = value;
    }

    [MenuItem(Root + "Recheck Saved Ceilings (Host)")]
    public static void Recheck()
    {
        if (!EditorApplication.isPlaying || !NetworkServer.active || World.Instance == null)
            throw new InvalidOperationException("Enter Play mode and start the HOST first.");
        World.Instance.RecheckSavedCeilings();
        Debug.Log("[Gobmine Cave-ins] Queued saved terrain for a ceiling recheck at " + World.Instance.ceilingCollapseThicknessMetres + " metres. Work continues within the frame budgets.");
    }

    [MenuItem(Root + "Log Cave-in and Debris Counters")]
    public static void LogCounters()
    {
        World w = World.Instance;
        if (!EditorApplication.isPlaying || w == null) throw new InvalidOperationException("Start a host first.");
        Debug.Log($"[Gobmine Cave-ins] Server={NetworkServer.active}; ceiling threshold={w.ceilingCollapseThicknessMetres:F2}m\n" +
            $"Ceiling bands={w.PendingCeilingBands}; pending collapse columns={w.PendingCeilingColumns}; scheduled ceiling samples={w.CeilingSamplesScheduled}\n" +
            $"Last ceiling work={w.LastCeilingMilliseconds:F2}ms; mining requests={w.PendingMiningRequests}; dirty meshes={w.PendingMeshRebuilds}; structural work={w.HasPendingStructuralWork}\n" +
            $"Rubble pending={w.PendingDebrisBuckets}; oldest pending={w.OldestPendingDebrisSeconds:F3}s; last spawn latency={w.LastDebrisSpawnLatencySeconds:F3}s; cosmetic culled={w.CosmeticDebrisCulled}\n" +
            $"Active fall patches={w.ActiveCollapseHazards}; damage hits={w.CollapseDamageHits}");
        foreach (GobminePlayerHealth health in UnityEngine.Object.FindObjectsByType<GobminePlayerHealth>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            Debug.Log($"[Gobmine Health] {health.name}; server={health.isServer}; health={health.CurrentHealth:F1}; dead={health.IsDead}", health);
    }
}
#endif
