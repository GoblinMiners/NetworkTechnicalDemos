#if UNITY_EDITOR
using UnityEditor;
using System.IO;
using UnityEngine;

public static class WorldSaveEditor
{
    [MenuItem("Tools/World/Delete World Save Data")]
    public static void DeleteWorldSave()
    {
        string path = Path.Combine(Application.persistentDataPath, "world_save.dat");
        string tmpPath = Path.ChangeExtension(path, ".tmp");
        string bakPath = Path.ChangeExtension(path, ".bak");

        bool deletedAny = false;

        if (File.Exists(path))
        {
            File.Delete(path);
            deletedAny = true;
        }
        if (File.Exists(tmpPath))
        {
            File.Delete(tmpPath);
            deletedAny = true;
        }
        if (File.Exists(bakPath))
        {
            File.Delete(bakPath);
            deletedAny = true;
        }

        if (deletedAny)
        {
            Debug.Log("<color=green>[WorldSaveEditor]</color> World save files successfully deleted from: " + Application.persistentDataPath);
        }
        else
        {
            Debug.LogWarning("<color=yellow>[WorldSaveEditor]</color> No world save files found to delete at: " + Application.persistentDataPath);
        }
    }

    [MenuItem("Tools/World/Open Save Folder in Explorer")]
    public static void OpenSavePath()
    {
        if (Directory.Exists(Application.persistentDataPath))
        {
            EditorUtility.RevealInFinder(Application.persistentDataPath);
        }
        else
        {
            Debug.LogWarning("[WorldSaveEditor] Save directory does not exist yet. Save the world at least once first.");
        }
    }
}
#endif