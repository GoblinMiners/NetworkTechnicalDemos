#if UNITY_EDITOR
using UnityEditor;
using System.IO;
using UnityEngine;

public static class MaterialSaveEditor
{
    [MenuItem("Tools/World/Open Material Save Folder")]
    public static void OpenPath()
    {
        string path = Path.GetDirectoryName(MaterialWorldPersistence.DefaultPath);
        if (Directory.Exists(path))
        {
            EditorUtility.RevealInFinder(path);
        }
        else
        {
            Debug.LogWarning("[MaterialSaveEditor] Directory does not exist yet. Save the world at least once first.");
        }
    }
}
#endif