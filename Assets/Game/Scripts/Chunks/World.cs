using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class World : MonoBehaviour
{
    public static World Instance { get; private set; }

    [Header("World Settings")]
    public int seed;
    public float noiseScale = 0.05f;
    [SerializeField] private Material voxelMaterial;
    
    public Vector3Int chunkSize = new Vector3Int(16, 16, 16);
    public float voxelSize = 0.5f; 
    
    [Header("Infinite Loading")]
    public Transform playerTarget;
    public int viewDistanceInChunks = 3;
    public int maxYChunk = 0; 

    private Dictionary<Vector3Int, Chunk> _activeChunks = new Dictionary<Vector3Int, Chunk>();
    private Queue<Chunk> _chunkPool = new Queue<Chunk>();
    private Queue<Vector3Int> _chunksToGenerate = new Queue<Vector3Int>();

    // NEW: This dictionary remembers the layout of chunks you've modified or visited
    private Dictionary<Vector3Int, float[,,]> _savedChunkData = new Dictionary<Vector3Int, float[,,]>();

    private void Awake()
    {
        if (Instance == null) Instance = this;
        else Destroy(gameObject);

        if (seed == 0) seed = Random.Range(1000, 9999);
    }

    private void Start()
    {
        StartCoroutine(ChunkManagerRoutine());
    }

    private IEnumerator ChunkManagerRoutine()
    {
        while (true)
        {
            if (_chunksToGenerate.Count > 0)
            {
                Vector3Int coord = _chunksToGenerate.Dequeue();
                if (!_activeChunks.ContainsKey(coord)) GenerateChunkAt(coord);
                yield return null; 
            }
            else
            {
                if (playerTarget != null) UpdateVisibleChunks();
                yield return new WaitForSeconds(0.25f);
            }
        }
    }

    private void UpdateVisibleChunks()
    {
        Vector3Int currentChunk = GetChunkCoordFromPosition(playerTarget.position);
        List<Vector3Int> chunksToRemove = new List<Vector3Int>();

        foreach (var chunk in _activeChunks)
        {
            if (Vector3Int.Distance(chunk.Key, currentChunk) > viewDistanceInChunks + 1)
            {
                // NEW: Save the chunk's density data into our dictionary before unloading it!
                _savedChunkData[chunk.Key] = chunk.Value.GetDensities();

                chunk.Value.gameObject.SetActive(false);
                _chunkPool.Enqueue(chunk.Value);
                chunksToRemove.Add(chunk.Key);
            }
        }
        foreach (var key in chunksToRemove) _activeChunks.Remove(key);

        for (int x = -viewDistanceInChunks; x <= viewDistanceInChunks; x++)
        {
            for (int y = -viewDistanceInChunks; y <= viewDistanceInChunks; y++) 
            {
                for (int z = -viewDistanceInChunks; z <= viewDistanceInChunks; z++)
                {
                    Vector3Int coord = new Vector3Int(currentChunk.x + x, currentChunk.y + y, currentChunk.z + z);
                    if (coord.y > maxYChunk) continue;

                    if (!_activeChunks.ContainsKey(coord) && !_chunksToGenerate.Contains(coord))
                    {
                        _chunksToGenerate.Enqueue(coord);
                    }
                }
            }
        }
    }

    private void GenerateChunkAt(Vector3Int coord)
    {
        Vector3 chunkPos = new Vector3(coord.x * chunkSize.x, coord.y * chunkSize.y, coord.z * chunkSize.z) * voxelSize;
        Chunk newChunk;

        if (_chunkPool.Count > 0)
        {
            newChunk = _chunkPool.Dequeue();
            newChunk.transform.position = chunkPos;
            newChunk.gameObject.SetActive(true);
        }
        else 
        {
            GameObject chunkObj = new GameObject();
            chunkObj.transform.SetParent(transform);
            chunkObj.transform.position = chunkPos;
            newChunk = chunkObj.AddComponent<Chunk>();
        }

        newChunk.gameObject.name = $"Chunk_{coord.x}_{coord.y}_{coord.z}";

        // NEW: Check if we have saved data for this chunk
        _savedChunkData.TryGetValue(coord, out float[,,] dataToLoad);

        // Pass the saved data into the Init function
        newChunk.Init(chunkSize, voxelSize, chunkPos, noiseScale, seed, dataToLoad);
        
        _activeChunks.Add(coord, newChunk);
    }

    public Vector3Int GetChunkCoordFromPosition(Vector3 pos)
    {
        return new Vector3Int(
            Mathf.FloorToInt(pos.x / (chunkSize.x * voxelSize)),
            Mathf.FloorToInt(pos.y / (chunkSize.y * voxelSize)),
            Mathf.FloorToInt(pos.z / (chunkSize.z * voxelSize))
        );
    }

   public void ModifyTerrain(Vector3 hitPoint, float radius, float amount)
    {
        Vector3Int minChunk = GetChunkCoordFromPosition(hitPoint - new Vector3(radius, radius, radius));
        Vector3Int maxChunk = GetChunkCoordFromPosition(hitPoint + new Vector3(radius, radius, radius));

        for (int x = minChunk.x; x <= maxChunk.x; x++)
        {
            for (int y = minChunk.y; y <= maxChunk.y; y++)
            {
                for (int z = minChunk.z; z <= maxChunk.z; z++)
                {
                    Vector3Int coord = new Vector3Int(x, y, z);
                    
                    if (_activeChunks.TryGetValue(coord, out Chunk chunk))
                    {
                        // The chunk is loaded! Update it normally.
                        chunk.EditTerrain(hitPoint, radius, amount);
                    }
                    else
                    {
                        // THE FIX: The chunk is unloaded! Save the damage to its memory so it loads correctly later.
                        EditUnloadedChunk(coord, hitPoint, radius, amount);
                    }
                }
            }
        }
    }

    // NEW HELPER METHOD: Modifies chunk data even when the chunk is invisible/unloaded
    private void EditUnloadedChunk(Vector3Int coord, Vector3 hitPoint, float radius, float amount)
    {
        // 1. If this unloaded chunk doesn't have saved memory yet, we must generate its default rock first!
        if (!_savedChunkData.TryGetValue(coord, out float[,,] densities))
        {
            densities = new float[chunkSize.x + 1, chunkSize.y + 1, chunkSize.z + 1];
            Vector3 chunkGlobalPos = new Vector3(coord.x * chunkSize.x, coord.y * chunkSize.y, coord.z * chunkSize.z) * voxelSize;

            for (int x = 0; x <= chunkSize.x; x++)
            {
                for (int y = 0; y <= chunkSize.y; y++)
                {
                    for (int z = 0; z <= chunkSize.z; z++)
                    {
                        Vector3 worldPos = chunkGlobalPos + (new Vector3(x, y, z) * voxelSize);
                        float density = -1f; 

                        // This must match your Chunk.cs starting hallway logic!
                        if (worldPos.x > -4f && worldPos.x < 4f &&
                            worldPos.y > -2f && worldPos.y < 5f &&
                            worldPos.z > -5f && worldPos.z < 15f)
                        {
                            density = 1f; 
                        }

                        densities[x, y, z] = density;
                    }
                }
            }
            // Save the newly generated default rock to memory
            _savedChunkData[coord] = densities; 
        }

        // 2. Now carve the spherical hole into this saved memory
        Vector3 chunkPos = new Vector3(coord.x * chunkSize.x, coord.y * chunkSize.y, coord.z * chunkSize.z) * voxelSize;
        Vector3 localHit = hitPoint - chunkPos;

        int minX = Mathf.Max(0, Mathf.FloorToInt((localHit.x - radius) / voxelSize));
        int maxX = Mathf.Min(chunkSize.x, Mathf.CeilToInt((localHit.x + radius) / voxelSize));
        int minY = Mathf.Max(0, Mathf.FloorToInt((localHit.y - radius) / voxelSize));
        int maxY = Mathf.Min(chunkSize.y, Mathf.CeilToInt((localHit.y + radius) / voxelSize));
        int minZ = Mathf.Max(0, Mathf.FloorToInt((localHit.z - radius) / voxelSize));
        int maxZ = Mathf.Min(chunkSize.z, Mathf.CeilToInt((localHit.z + radius) / voxelSize));

        for (int x = minX; x <= maxX; x++)
        {
            for (int y = minY; y <= maxY; y++)
            {
                for (int z = minZ; z <= maxZ; z++)
                {
                    Vector3 voxelWorldPos = chunkPos + (new Vector3(x, y, z) * voxelSize);
                    float distance = Vector3.Distance(voxelWorldPos, hitPoint);
                    
                    if (distance <= radius)
                    {
                        float falloff = 1f - (distance / radius);
                        densities[x, y, z] += (amount * falloff);
                    }
                }
            }
        }
    }

    public Material GetVoxelMaterial() { return voxelMaterial; }
}