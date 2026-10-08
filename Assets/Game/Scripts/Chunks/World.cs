using System.Collections;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Threading.Tasks;
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

    [Header("Physics Settings")]
    public GameObject debrisPrefab; 
    public int maxCollapseVolume = 80000; // Increased to allow large boulders to collapse fully
    public int minCeilingThickness = 2; 

    private Dictionary<Vector3Int, Chunk> _activeChunks = new Dictionary<Vector3Int, Chunk>();
    private Dictionary<Vector3Int, Chunk> _sleepingChunks = new Dictionary<Vector3Int, Chunk>();
    private Queue<Chunk> _chunkPool = new Queue<Chunk>();
    private Queue<Vector3Int> _chunksToGenerate = new Queue<Vector3Int>();
    private Dictionary<Vector3Int, float[,,]> _savedChunkData = new Dictionary<Vector3Int, float[,,]>();

    private ConcurrentQueue<List<Vector3Int>> _collapseQueue = new ConcurrentQueue<List<Vector3Int>>();
    
    private bool _isCheckingPhysics = false;
    private float _physicsTimer = 0f;

    private Coroutine _chunkManagerCoroutine;
    private bool _worldInitialized = false;

    private void Awake()
    {
        if (Instance == null) Instance = this;
        else Destroy(gameObject);
    }

    private void Update()
    {
        if (isServer)
        {
            while (_collapseQueue.TryDequeue(out List<Vector3Int> cluster))
            {
                CollapseCluster(cluster);
            }

            // Continuously scan for any orphaned floating pieces every 0.3 seconds in the background
            _physicsTimer += Time.deltaTime;
            if (_physicsTimer >= 0.01f && !_isCheckingPhysics) //&& !_isCheckingPhysics
            {
                _physicsTimer = 0f;
                CheckForFloatingIslands();
            }
        }
    }

    #region Mirror Initialization & Seed Sync

    public override void OnStartServer()
    {
        base.OnStartServer();
        if (customSeed == 0) seed = Random.Range(1000, 99999);
        else seed = customSeed;
    }

    private void OnSeedSynced(int oldSeed, int newSeed)
    {
        StartWorldGeneration();
    }

    public override void OnStartClient()
    {
        base.OnStartClient();
        if (seed != 0 && !_worldInitialized) StartWorldGeneration();
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

    public void RequestModifyTerrain(Vector3 hitPoint, float radius, float amount)
    {
        if (isServer) RpcModifyTerrain(hitPoint, radius, amount);
        else CmdRequestModifyTerrain(hitPoint, radius, amount);
    }

    [Command(requiresAuthority = false)]
    private void CmdRequestModifyTerrain(Vector3 hitPoint, float radius, float amount)
    {
        RpcModifyTerrain(hitPoint, radius, amount);
    }

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
                        if (!_chunksToGenerate.Contains(coord)) _chunksToGenerate.Enqueue(coord);
                    }
                }
            }
        }

        if (isServer) 
        {
            CheckForThinCeilings(hitPoint, radius);
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

    #region Structural Integrity Physics

    private void CheckForThinCeilings(Vector3 hitPoint, float radius)
    {
        Vector3Int startNode = new Vector3Int(
            Mathf.RoundToInt(hitPoint.x / voxelSize),
            Mathf.RoundToInt((hitPoint.y + radius) / voxelSize) + 1,
            Mathf.RoundToInt(hitPoint.z / voxelSize)
        );

        int maxScanHeight = 6; 
        int solidThicknessCount = 0;
        List<Vector3Int> thinCeilingNodes = new List<Vector3Int>();

        for (int y = 0; y < maxScanHeight; y++)
        {
            Vector3Int scanCoord = startNode + new Vector3Int(0, y, 0);
            float density = GetDensityAtGlobalCoord(scanCoord);

            if (density < 0f) 
            {
                solidThicknessCount++;
                thinCeilingNodes.Add(scanCoord);
                thinCeilingNodes.Add(scanCoord + Vector3Int.left);
                thinCeilingNodes.Add(scanCoord + Vector3Int.right);
                thinCeilingNodes.Add(scanCoord + Vector3Int.forward);
                thinCeilingNodes.Add(scanCoord + Vector3Int.back);
            }
            else if (solidThicknessCount > 0) 
            {
                if (solidThicknessCount <= minCeilingThickness)
                {
                    _collapseQueue.Enqueue(thinCeilingNodes);
                }
                break; 
            }
        }
    }

   private async void CheckForFloatingIslands()
    {
        if (_isCheckingPhysics) return;
        _isCheckingPhysics = true;

        // STEP 1: Safely copy active chunk data references on the MAIN thread to avoid thread crashes
        Dictionary<Vector3Int, float[,,]> chunkDataSnapshot = new Dictionary<Vector3Int, float[,,]>();
        foreach (var kvp in _activeChunks)
        {
            if (kvp.Value != null)
            {
                chunkDataSnapshot[kvp.Key] = kvp.Value.GetDensities();
            }
        }

        Vector3Int localChunkSize = chunkSize;

        // STEP 2: Run the heavy calculations safely on the background thread using our snapshot
        await Task.Run(() =>
        {
            HashSet<Vector3Int> processedNodes = new HashSet<Vector3Int>();

            foreach (var chunkPair in chunkDataSnapshot)
            {
                Vector3Int chunkCoord = chunkPair.Key;
                float[,,] densities = chunkPair.Value;
                if (densities == null) continue;

                Vector3Int minWorldCoord = chunkCoord * localChunkSize;

                for (int x = 0; x < localChunkSize.x; x++)
                {
                    for (int y = 0; y < localChunkSize.y; y++)
                    {
                        for (int z = 0; z < localChunkSize.z; z++)
                        {
                            Vector3Int checkCoord = minWorldCoord + new Vector3Int(x, y, z);

                            if (processedNodes.Contains(checkCoord)) continue;

                            // Check density from our local array snapshot safely
                            if (densities[x, y, z] < 0f) 
                            {
                                // Optimization: Skip deeply buried voxels with no air neighbors
                                if (!HasAirNeighborSnapshot(checkCoord, chunkDataSnapshot, localChunkSize))
                                {
                                    continue;
                                }

                                List<Vector3Int> cluster = RunFloodFillWithSnapshot(checkCoord, chunkDataSnapshot, localChunkSize, out bool isAnchored);

                                if (!isAnchored && cluster.Count > 0)
                                {
                                    _collapseQueue.Enqueue(cluster);
                                }

                                foreach (var node in cluster)
                                {
                                    processedNodes.Add(node);
                                }
                            }
                        }
                    }
                }
            }
        });

        _isCheckingPhysics = false;
    }

    private bool HasAirNeighborSnapshot(Vector3Int globalCoord, Dictionary<Vector3Int, float[,,]> snapshot, Vector3Int size)
    {
        Vector3Int[] directions = {
            Vector3Int.up, Vector3Int.down, Vector3Int.left, 
            Vector3Int.right, Vector3Int.forward, Vector3Int.back
        };

        foreach (var dir in directions)
        {
            if (GetDensityFromSnapshot(globalCoord + dir, snapshot, size) >= 0f) 
            {
                return true; 
            }
        }
        return false; 
    }

    private List<Vector3Int> RunFloodFillWithSnapshot(Vector3Int startNode, Dictionary<Vector3Int, float[,,]> snapshot, Vector3Int size, out bool isAnchored)
    {
        HashSet<Vector3Int> visited = new HashSet<Vector3Int>();
        Queue<Vector3Int> queue = new Queue<Vector3Int>();
        List<Vector3Int> cluster = new List<Vector3Int>();
        
        queue.Enqueue(startNode);
        visited.Add(startNode);
        isAnchored = false;

        Vector3Int[] directions = {
            Vector3Int.up, Vector3Int.down, Vector3Int.left, 
            Vector3Int.right, Vector3Int.forward, Vector3Int.back
        };

        while (queue.Count > 0)
        {
            Vector3Int current = queue.Dequeue();
            cluster.Add(current);

            if (current.y <= 0) 
            {
                isAnchored = true;
                break;
            }

            if (cluster.Count >= maxCollapseVolume) 
            {
                isAnchored = true;
                break;
            }

            foreach (Vector3Int dir in directions)
            {
                Vector3Int neighbor = current + dir;

                if (!visited.Contains(neighbor))
                {
                    visited.Add(neighbor);
                    
                    if (GetDensityFromSnapshot(neighbor, snapshot, size) < 0f)
                    {
                        queue.Enqueue(neighbor);
                    }
                }
            }
        }

        return cluster;
    }

    private float GetDensityFromSnapshot(Vector3Int globalCoord, Dictionary<Vector3Int, float[,,]> snapshot, Vector3Int size)
    {
        Vector3Int chunkCoord = new Vector3Int(
            Mathf.FloorToInt((float)globalCoord.x / size.x),
            Mathf.FloorToInt((float)globalCoord.y / size.y),
            Mathf.FloorToInt((float)globalCoord.z / size.z)
        );

        if (snapshot.TryGetValue(chunkCoord, out float[,,] densities))
        {
            Vector3Int localCoord = globalCoord - (chunkCoord * size);
            
            if (localCoord.x >= 0 && localCoord.x <= size.x &&
                localCoord.y >= 0 && localCoord.y <= size.y &&
                localCoord.z >= 0 && localCoord.z <= size.z)
            {
                return densities[localCoord.x, localCoord.y, localCoord.z];
            }
        }
        return -1f; 
    }
    private void CollapseCluster(List<Vector3Int> cluster)
    {
        if (cluster.Count == 0) return;

        Vector3 minBounds = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
        Vector3 maxBounds = new Vector3(float.MinValue, float.MinValue, float.MinValue);
        HashSet<Vector3Int> affectedChunks = new HashSet<Vector3Int>();

        foreach (Vector3Int node in cluster)
        {
            SetDensityAtGlobalCoord(node, 1f); // 1f = Air
            
            Vector3 worldPos = new Vector3(node.x, node.y, node.z) * voxelSize;
            minBounds = Vector3.Min(minBounds, worldPos);
            maxBounds = Vector3.Max(maxBounds, worldPos);

            affectedChunks.Add(GetChunkCoordFromPosition(worldPos));
        }

        if (debrisPrefab != null) 
        {
            Vector3 boundingCenter = (minBounds + maxBounds) / 2f;
            Vector3 exactSize = maxBounds - minBounds;
            exactSize = Vector3.Max(exactSize, new Vector3(voxelSize, voxelSize, voxelSize));

            GameObject debris = Instantiate(debrisPrefab, boundingCenter, Random.rotation);
            debris.transform.localScale = exactSize;
            NetworkServer.Spawn(debris);
        }

        RpcRebuildSpecificChunks(new List<Vector3Int>(affectedChunks));
    }

    [ClientRpc]
    private void RpcRebuildSpecificChunks(List<Vector3Int> chunkCoords)
    {
        foreach (var coord in chunkCoords)
        {
            if (_activeChunks.TryGetValue(coord, out Chunk chunk))
            {
                chunk.EditTerrain(chunk.transform.position, 0.1f, 0f);
            }
        }
    }

    private float GetDensityAtGlobalCoord(Vector3Int globalCoord)
    {
        Vector3 globalPos = new Vector3(globalCoord.x, globalCoord.y, globalCoord.z) * voxelSize;
        Vector3Int chunkCoord = GetChunkCoordFromPosition(globalPos);

        if (_activeChunks.TryGetValue(chunkCoord, out Chunk chunk))
        {
            Vector3Int localCoord = globalCoord - new Vector3Int(chunkCoord.x * chunkSize.x, chunkCoord.y * chunkSize.y, chunkCoord.z * chunkSize.z);
            
            if (localCoord.x >= 0 && localCoord.x <= chunkSize.x &&
                localCoord.y >= 0 && localCoord.y <= chunkSize.y &&
                localCoord.z >= 0 && localCoord.z <= chunkSize.z)
            {
                return chunk.GetDensities()[localCoord.x, localCoord.y, localCoord.z];
            }
        }
        return -1f; 
    }

    private void SetDensityAtGlobalCoord(Vector3Int globalCoord, float density)
    {
        Vector3 globalPos = new Vector3(globalCoord.x, globalCoord.y, globalCoord.z) * voxelSize;
        Vector3Int chunkCoord = GetChunkCoordFromPosition(globalPos);

        if (_activeChunks.TryGetValue(chunkCoord, out Chunk chunk))
        {
            Vector3Int localCoord = globalCoord - new Vector3Int(chunkCoord.x * chunkSize.x, chunkCoord.y * chunkSize.y, chunkCoord.z * chunkSize.z);
            
            if (localCoord.x >= 0 && localCoord.x <= chunkSize.x &&
                localCoord.y >= 0 && localCoord.y <= chunkSize.y &&
                localCoord.z >= 0 && localCoord.z <= chunkSize.z)
            {
                chunk.GetDensities()[localCoord.x, localCoord.y, localCoord.z] = density;
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