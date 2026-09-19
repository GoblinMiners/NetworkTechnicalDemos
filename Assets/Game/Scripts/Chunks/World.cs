using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Mirror;

[RequireComponent(typeof(NetworkIdentity))]
public class World : NetworkBehaviour
{
    public static World Instance { get; private set; }

    [Header("World Settings")]
    [Tooltip("Leave at 0 for a random seed, or set a specific seed for the host.")]
    [SerializeField]
    public int customSeed = 0;

    // Synchronized across all clients automatically by Mirror
    [SyncVar(hook = nameof(OnSeedSynced))]
    public int seed;

    public float noiseScale = 0.05f;
    [SerializeField] private Material voxelMaterial;
    
    public Vector3Int chunkSize = new Vector3Int(16, 16, 16);
    public float voxelSize = 0.5f; 
    
    [Header("Infinite Loading")]
    public Transform playerTarget;
    public int viewDistanceInChunks = 3;
    public int sleepDistanceInChunks = 6; 
    public int maxYChunk = 0; 

    private Dictionary<Vector3Int, Chunk> _activeChunks = new Dictionary<Vector3Int, Chunk>();
    private Dictionary<Vector3Int, Chunk> _sleepingChunks = new Dictionary<Vector3Int, Chunk>();
    private Queue<Chunk> _chunkPool = new Queue<Chunk>();
    private Queue<Vector3Int> _chunksToGenerate = new Queue<Vector3Int>();
    private Dictionary<Vector3Int, float[,,]> _savedChunkData = new Dictionary<Vector3Int, float[,,]>();

    private Coroutine _chunkManagerCoroutine;
    private bool _worldInitialized = false;

    private void Awake()
    {
        if (Instance == null) Instance = this;
        else Destroy(gameObject);
    }

    #region Mirror Initialization & Seed Sync

    public override void OnStartServer()
    {
        base.OnStartServer();

        // If the host left customSeed at 0, pick a random seed; otherwise use the specified one
        if (customSeed == 0)
        {
            seed = Random.Range(1000, 99999);
        }
        else
        {
            seed = customSeed;
        }
    }

    // Called automatically whenever the SyncVar 'seed' changes on clients or host
    private void OnSeedSynced(int oldSeed, int newSeed)
    {
        StartWorldGeneration();
    }

    public override void OnStartClient()
    {
        base.OnStartClient();

        // Fallback in case the seed synced prior to full client startup
        if (seed != 0 && !_worldInitialized)
        {
            StartWorldGeneration();
        }
    }

    private void StartWorldGeneration()
    {
        if (_worldInitialized) return;
        _worldInitialized = true;

        if (_chunkManagerCoroutine != null) StopCoroutine(_chunkManagerCoroutine);
        _chunkManagerCoroutine = StartCoroutine(ChunkManagerRoutine());
    }

    #endregion

    #region Chunk Management Loop

    private IEnumerator ChunkManagerRoutine()
    {
        while (true)
        {
            if (_chunksToGenerate.Count > 0)
            {
                int chunksProcessedThisFrame = 0;
                Vector3Int currentChunk = playerTarget != null 
                    ? GetChunkCoordFromPosition(playerTarget.position) 
                    : Vector3Int.zero;

                while (_chunksToGenerate.Count > 0 && chunksProcessedThisFrame < 4)
                {
                    Vector3Int coord = _chunksToGenerate.Dequeue();
                    
                    if (playerTarget != null && Vector3Int.Distance(coord, currentChunk) > viewDistanceInChunks + 1)
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
        
        List<Vector3Int> activeToRemove = new List<Vector3Int>();
        foreach (var chunk in _activeChunks)
        {
            float dist = Vector3Int.Distance(chunk.Key, currentChunk);
            if (dist > viewDistanceInChunks + 1)
            {
                chunk.Value.gameObject.SetActive(false);
                activeToRemove.Add(chunk.Key);

                if (dist > sleepDistanceInChunks + 1)
                {
                    _savedChunkData[chunk.Key] = chunk.Value.GetDensities();
                    _chunkPool.Enqueue(chunk.Value);
                }
                else
                {
                    _sleepingChunks.Add(chunk.Key, chunk.Value);
                }
            }
        }
        foreach (var key in activeToRemove) _activeChunks.Remove(key);

        List<Vector3Int> sleepingToRemove = new List<Vector3Int>();
        foreach (var chunk in _sleepingChunks)
        {
            float dist = Vector3Int.Distance(chunk.Key, currentChunk);
            if (dist > sleepDistanceInChunks + 1)
            {
                _savedChunkData[chunk.Key] = chunk.Value.GetDensities();
                _chunkPool.Enqueue(chunk.Value);
                sleepingToRemove.Add(chunk.Key);
            }
        }
        foreach (var key in sleepingToRemove) _sleepingChunks.Remove(key);

        for (int x = -viewDistanceInChunks; x <= viewDistanceInChunks; x++)
        {
            for (int y = -viewDistanceInChunks; y <= viewDistanceInChunks; y++) 
            {
                for (int z = -viewDistanceInChunks; z <= viewDistanceInChunks; z++)
                {
                    Vector3Int coord = new Vector3Int(currentChunk.x + x, currentChunk.y + y, currentChunk.z + z);
                    if (coord.y > maxYChunk) continue;

                    if (_activeChunks.ContainsKey(coord)) continue;

                    if (_sleepingChunks.TryGetValue(coord, out Chunk sleepingChunk))
                    {
                        sleepingChunk.gameObject.SetActive(true);
                        _activeChunks.Add(coord, sleepingChunk);
                        _sleepingChunks.Remove(coord);
                    }
                    else if (!_chunksToGenerate.Contains(coord))
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

        _savedChunkData.TryGetValue(coord, out float[,,] dataToLoad);
        newChunk.Init(chunkSize, voxelSize, chunkPos, noiseScale, seed, dataToLoad);
        
        _activeChunks.Add(coord, newChunk);
    }

    #endregion

    #region Networked Mining

    // Called locally by a player's miner script
    public void RequestModifyTerrain(Vector3 hitPoint, float radius, float amount)
    {
        if (isServer)
        {
            RpcModifyTerrain(hitPoint, radius, amount);
        }
        else
        {
            CmdRequestModifyTerrain(hitPoint, radius, amount);
        }
    }

    // Client informs the server of a modification
    [Command(requiresAuthority = false)]
    private void CmdRequestModifyTerrain(Vector3 hitPoint, float radius, float amount)
    {
        RpcModifyTerrain(hitPoint, radius, amount);
    }

    // Server broadcasts the modification to all connected clients
    [ClientRpc]
    private void RpcModifyTerrain(Vector3 hitPoint, float radius, float amount)
    {
        ExecuteTerrainModification(hitPoint, radius, amount);
    }

    private void ExecuteTerrainModification(Vector3 hitPoint, float radius, float amount)
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
                        
                        if (!_chunksToGenerate.Contains(coord))
                        {
                            _chunksToGenerate.Enqueue(coord);
                        }
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

    #endregion

    public Vector3Int GetChunkCoordFromPosition(Vector3 pos)
    {
        return new Vector3Int(
            Mathf.FloorToInt(pos.x / (chunkSize.x * voxelSize)),
            Mathf.FloorToInt(pos.y / (chunkSize.y * voxelSize)),
            Mathf.FloorToInt(pos.z / (chunkSize.z * voxelSize))
        );
    }

    public Material GetVoxelMaterial() { return voxelMaterial; }
}