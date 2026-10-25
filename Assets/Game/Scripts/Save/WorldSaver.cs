using System;
using System.IO;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

/// <summary>Compatible with the supplied chunk-based world_save.dat format.</summary>
public static class WorldSaver
{
    private static readonly object SaveLock = new object();
    private static string SavePath => Path.Combine(Application.persistentDataPath, "world_save.dat");

    public static void SaveWorld(Dictionary<Vector3Int, float[,,]> data, Vector3Int size)
    {
        string path = SavePath; // Resolve Unity's application path on the main thread.
        try { Write(data, size, path); }
        catch (Exception ex) { Debug.LogError("[WorldSaver] Save failed: " + ex.Message); }
    }

    public static async Task SaveWorldAsync(Dictionary<Vector3Int, float[,,]> data, Vector3Int size)
    {
        string path = SavePath;
        var snapshot = new Dictionary<Vector3Int, float[,,]>();
        foreach (var entry in data) snapshot[entry.Key] = (float[,,])entry.Value.Clone();
        try { await Task.Run(() => Write(snapshot, size, path)); }
        catch (Exception ex) { Debug.LogError("[WorldSaver] Save failed: " + ex.Message); }
    }

    private static void Write(Dictionary<Vector3Int, float[,,]> data, Vector3Int size, string path)
    {
        lock (SaveLock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temp = Path.ChangeExtension(path, ".tmp");
            string backup = Path.ChangeExtension(path, ".bak");
            using (var writer = new BinaryWriter(File.Open(temp, FileMode.Create, FileAccess.Write)))
            {
                writer.Write(data.Count);
                foreach (var entry in data)
                {
                    writer.Write(entry.Key.x); writer.Write(entry.Key.y); writer.Write(entry.Key.z);
                    for (int x = 0; x <= size.x; x++)
                        for (int y = 0; y <= size.y; y++)
                            for (int z = 0; z <= size.z; z++) writer.Write(entry.Value[x, y, z]);
                }
                writer.Flush();
            }
            if (File.Exists(path))
            {
                if (File.Exists(backup)) File.Delete(backup);
                File.Move(path, backup);
            }
            File.Move(temp, path);
        }
    }

    public static Dictionary<Vector3Int, float[,,]> LoadWorld(Vector3Int size)
    {
        string path = SavePath;
        foreach (string candidate in new[] { path, Path.ChangeExtension(path, ".bak") })
        {
            if (!File.Exists(candidate)) continue;
            try
            {
                var result = new Dictionary<Vector3Int, float[,,]>();
                using (var reader = new BinaryReader(File.OpenRead(candidate)))
                {
                    int count = reader.ReadInt32();
                    long floats = (long)(size.x + 1) * (size.y + 1) * (size.z + 1);
                    long expected = 4L + (12L + 4L * floats) * count;
                    if (count < 0 || expected != reader.BaseStream.Length)
                        throw new InvalidDataException("Corrupt save or changed chunk dimensions. Back up saves before changing world settings.");
                    for (int i = 0; i < count; i++)
                    {
                        Vector3Int c = new Vector3Int(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32());
                        float[,,] d = new float[size.x + 1, size.y + 1, size.z + 1];
                        for (int x = 0; x <= size.x; x++)
                            for (int y = 0; y <= size.y; y++)
                                for (int z = 0; z <= size.z; z++) d[x, y, z] = reader.ReadSingle();
                        result[c] = d;
                    }
                }
                return result; // Never recurse indefinitely when both copies are corrupt.
            }
            catch (Exception ex) { Debug.LogWarning("[WorldSaver] Cannot load " + candidate + ": " + ex.Message); }
        }
        return new Dictionary<Vector3Int, float[,,]>();
    }
}
