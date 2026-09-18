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
    
    // NEW: How far away a chunk can be before we permanently delete its mesh
    public int sleepDistanceInChunks = 6; 
    public int maxYChunk = 0; 

    private Dictionary<Vector3Int, Chunk> _activeChunks = new Dictionary<Vector3Int, Chunk>();
    
    // NEW: The cache that holds invisible chunks with fully intact meshes
    private Dictionary<Vector3Int, Chunk> _sleepingChunks = new Dictionary<Vector3Int, Chunk>();
    
    private Queue<Chunk> _chunkPool = new Queue<Chunk>();
    private Queue<Vector3Int> _chunksToGenerate = new Queue<Vector3Int>();

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
                int chunksProcessedThisFrame = 0;
                
                // Get the player's current location so we can check distances
                Vector3Int currentChunk = GetChunkCoordFromPosition(playerTarget.position);

                while (_chunksToGenerate.Count > 0 && chunksProcessedThisFrame < 4)
                {
                    Vector3Int coord = _chunksToGenerate.Dequeue();
                    
                    // THE FIX: If we ran really fast and this chunk is now miles behind us, skip it!
                    if (Vector3Int.Distance(coord, currentChunk) > viewDistanceInChunks + 1)
                    {
                        continue; 
                    }

                    if (!_activeChunks.ContainsKey(coord) && !_sleepingChunks.ContainsKey(coord)) 
                    {
                        GenerateChunkAt(coord);
                    }
                    chunksProcessedThisFrame++;
                }
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
        
        // 1. Check Active Chunks to see if they should go to Sleep
        List<Vector3Int> activeToRemove = new List<Vector3Int>();
        foreach (var chunk in _activeChunks)
        {
            float dist = Vector3Int.Distance(chunk.Key, currentChunk);
            if (dist > viewDistanceInChunks + 1)
            {
                chunk.Value.gameObject.SetActive(false); // Turn invisible
                activeToRemove.Add(chunk.Key);

                if (dist > sleepDistanceInChunks + 1)
                {
                    // It's way too far away. Save it and fully recycle it.
                    _savedChunkData[chunk.Key] = chunk.Value.GetDensities();
                    _chunkPool.Enqueue(chunk.Value);
                }
                else
                {
                    // It's just out of view. Put it to sleep (keep mesh instantly ready).
                    _sleepingChunks.Add(chunk.Key, chunk.Value);
                }
            }
        }
        foreach (var key in activeToRemove) _activeChunks.Remove(key);

        // 2. Check Sleeping Chunks to see if they should be Recycled
        List<Vector3Int> sleepingToRemove = new List<Vector3Int>();
        foreach (var chunk in _sleepingChunks)
        {
            float dist = Vector3Int.Distance(chunk.Key, currentChunk);
            if (dist > sleepDistanceInChunks + 1)
            {
                // It drifted too far while sleeping. Recycle it.
                _savedChunkData[chunk.Key] = chunk.Value.GetDensities();
                _chunkPool.Enqueue(chunk.Value);
                sleepingToRemove.Add(chunk.Key);
            }
        }
        foreach (var key in sleepingToRemove) _sleepingChunks.Remove(key);

        // 3. Load or Wake Up chunks around the player
        for (int x = -viewDistanceInChunks; x <= viewDistanceInChunks; x++)
        {
            for (int y = -viewDistanceInChunks; y <= viewDistanceInChunks; y++) 
            {
                for (int z = -viewDistanceInChunks; z <= viewDistanceInChunks; z++)
                {
                    Vector3Int coord = new Vector3Int(currentChunk.x + x, currentChunk.y + y, currentChunk.z + z);
                    if (coord.y > maxYChunk) continue;

                    if (_activeChunks.ContainsKey(coord)) continue;

                    // THE MAGIC: If it is sleeping, wake it up INSTANTLY without generating!
                    if (_sleepingChunks.TryGetValue(coord, out Chunk sleepingChunk))
                    {
                        sleepingChunk.gameObject.SetActive(true);
                        _activeChunks.Add(coord, sleepingChunk);
                        _sleepingChunks.Remove(coord);
                    }
                    else if (!_chunksToGenerate.Contains(coord))
                    {
                        // It doesn't exist anywhere, add it to the math queue
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

        _savedChunkData.TryGetValue(coord, out float[,,] dataToLoad);
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
                        chunk.EditTerrain(hitPoint, radius, amount);
                    }
                    // NEW: If you mine a sleeping chunk, instantly wake it up!
                    else if (_sleepingChunks.TryGetValue(coord, out Chunk sleepingChunk))
                    {
                        sleepingChunk.gameObject.SetActive(true);
                        _activeChunks.Add(coord, sleepingChunk);
                        _sleepingChunks.Remove(coord);
                        sleepingChunk.EditTerrain(hitPoint, radius, amount);
                    }
                    else
                    {
                        EditUnloadedChunk(coord, hitPoint, radius, amount);
                    }
                }
            }
        }
    }

    private void EditUnloadedChunk(Vector3Int coord, Vector3 hitPoint, float radius, float amount)
    {
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
            _savedChunkData[coord] = densities; 
        }

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