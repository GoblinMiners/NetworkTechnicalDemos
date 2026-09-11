using System;
using System.Collections.Generic;
using UnityEngine;

public class World : MonoBehaviour
{
    
    public static World Instance { get; private set; }
    
    [SerializeField] private Material voxelMaterial;
    
    [SerializeField] private Vector3Int worldSize = new Vector3Int(4, 4, 4); // world size defined by the number of chunks
    
    [SerializeField] private Vector3Int _chunkSize = new Vector3Int(10, 7, 10); // This defines the chunk size, which is the number of voxels in each dimension of a chunk

    private Dictionary<Vector3, Chunk> _chunks;
    
    
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {

        _chunks = new Dictionary<Vector3, Chunk>();
        WorldGenerator();
        
    }

    public void Awake()
    {

        if (Instance == null)
        {

            Instance = this;
            DontDestroyOnLoad(gameObject);

        }
        else
        {
            
            Destroy(gameObject);
            
        }
        
        
    }

    private void WorldGenerator()
    {

        for (int x = 0; x < worldSize.x; x++)
        {

            for (int y = 0; y < worldSize.y; y++)
            {

                for (int z = 0; z < worldSize.z; z++)
                {

                    Vector3Int chunkCoord = new Vector3Int(x, y, z);
                    
                    Vector3 chunkPos = new Vector3(x * _chunkSize.x, y * _chunkSize.y, z * _chunkSize.z);
                    
                    GameObject newChunkObject = new GameObject($"Chunk_{x}_{y}_{z}");
                    
                    newChunkObject.transform.position = chunkPos;
                    newChunkObject.transform.SetParent(transform);
                    
                    Chunk newChunk = newChunkObject.AddComponent<Chunk>();
                    
                    newChunk.Init(_chunkSize);
                    
                    _chunks.Add(chunkCoord, newChunk);
                    
                    
                }
                
                
            }
            
            
        }
        
        
    }
    
    
    public Chunk GetChunkAt(Vector3 globalPos)
    {
        
        if (_chunks == null) return null;
        
        Vector3Int chunkCoord = new Vector3Int(
            Mathf.FloorToInt(globalPos.x / _chunkSize.x),
            Mathf.FloorToInt(globalPos.y / _chunkSize.y),
            Mathf.FloorToInt(globalPos.z / _chunkSize.z)
        );
        
        if (_chunks.TryGetValue(chunkCoord, out Chunk chunk))
        {
            return chunk;
        }
        
        return null;
    }
    
    public Material GetVoxelMaterial()
    {
        return voxelMaterial;
    }

    
}
