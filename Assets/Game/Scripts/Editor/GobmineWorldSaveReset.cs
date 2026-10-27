#if UNITY_EDITOR
using System;
using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Resets only the CURRENT scene's material-world save files. It deliberately
/// does not change scene configuration, expedition assets or generation rules.
/// </summary>
public static class GobmineWorldSaveReset
{
    [MenuItem("Tools/Gobmine/World Saves/Archive And Start Fresh World (Open Scene)")]
    public static void ArchiveAndReset()
    {
        if (EditorApplication.isPlaying || Application.isPlaying)
        {
            Debug.LogError("[Gobmine Saves] Stop Play mode and the server before resetting a save.");
            return;
        }
        World[] worlds = UnityEngine.Object.FindObjectsByType<World>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        if (worlds.Length != 1)
        {
            Debug.LogError("[Gobmine Saves] Open DemoLvl (or another gameplay scene) containing exactly one World before resetting its save.");
            return;
        }
        World world = worlds[0];
        string file = world.materialSaveFileName;
        if (string.IsNullOrWhiteSpace(file) || Path.GetFileName(file) != file ||
            file.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            Debug.LogError("[Gobmine Saves] World has an invalid save filename; aborting.");
            return;
        }
        string path = Path.Combine(Application.persistentDataPath, file);
        string[] candidates = { path, path + ".bak", path + ".tmp" };
        int found = 0;
        foreach (string candidate in candidates) if (File.Exists(candidate)) found++;
        if (found == 0)
        {
            Debug.Log("[Gobmine Saves] No saved terrain file/backup exists for " + file +
                ". The configured world already starts fresh next time. No scene setup is required.", world);
            return;
        }
        if (!EditorUtility.DisplayDialog("Start fresh Gobmine world?",
            "Archive " + found + " existing save file(s) for:\n" + file +
            "\n\nThe terrain, resources, researched equipment and fluid state stored in this save will reset. " +
            "This does NOT change DemoLvl's Expedition settings or touch other save slots. " +
            "The next server start generates a fresh world.",
            "Archive and reset", "Cancel")) return;
        try
        {
            string root = Path.Combine(Application.persistentDataPath, "GobmineWorldSaveBackups");
            string folder = Path.Combine(root, DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff") + "_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(folder);
            foreach (string candidate in candidates)
                if (File.Exists(candidate)) File.Move(candidate, Path.Combine(folder, Path.GetFileName(candidate)));
            Debug.Log("[Gobmine Saves] Archived saved world to " + folder +
                ". Launch the game from Menu as usual. The scene stays configured for Expedition.", world);
        }
        catch (Exception e)
        {
            Debug.LogError("[Gobmine Saves] Could not archive the save. Check this exception; no generator settings were changed: " + e);
        }
    }
}
#endif
