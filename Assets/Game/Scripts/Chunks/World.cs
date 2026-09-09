using System.Collections.Generic;
using UnityEngine;

public class World : MonoBehaviour
{
    
    public int worldSize = 5; // world size defined by the number of chunks
    
    [SerializeField] private int chunkSize = 4; // This defines the chunk size, which is the number of voxels in each dimension of a chunk

    private Dictionary<Vector3, Chunk> _chunks;
    
    
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {

        _chunks = new Dictionary<Vector3, Chunk>();
        WorldGenerator();



    }

    private void WorldGenerator()
    {

        for (int x = 0; x < worldSize; x++)
        {

            for (int y = 0; y < worldSize; y++)
            {

                for (int z = 0; z < worldSize; z++)
                {
                    
                    Vector3 chunkPos = new Vector3(x * chunkSize, y * chunkSize, z * chunkSize);
                    GameObject newChunkObject = new GameObject($"Chunk_{x}_{y}_{z}");
                    newChunkObject.transform.position = chunkPos;
                    newChunkObject.transform.parent = this.transform;
                    
                    Chunk newChunk = newChunkObject.AddComponent<Chunk>();
                    newChunk.Init(chunkSize);
                    _chunks.Add(chunkPos, newChunk);
                    
                    
                }
                
                
            }
            
            
        }
        
        
    }
    
}
