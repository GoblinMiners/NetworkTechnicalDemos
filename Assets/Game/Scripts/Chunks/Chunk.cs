using System;
using System.Collections.Generic;
using UnityEngine;
using Random = UnityEngine.Random;

public class Chunk : MonoBehaviour
{

    private Voxel[,,] _voxels;
    private int _chunkSize = 4;
    private Color _gizmoColor;
    
    private List<Vector3> _vertices = new List<Vector3>();
    private List<int> _triangles = new List<int>();
    private List<Vector2> _uvs = new List<Vector2>();
    
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {

       // _voxels = new Voxel[_chunkSize, _chunkSize, _chunkSize];
        
        //InitVoxels();

    }

    public void Init(int size)
    {
        
        this._chunkSize = size;
        _voxels = new Voxel[size, size, size];
        InitVoxels();
        _gizmoColor = new Color(Random.value, Random.value, Random.value, 0.4f);


    }

    private void InitVoxels()
    {

        for (int x = 0; x < _chunkSize; x++)
        {

            for (int y = 0; y < _chunkSize; y++)
            {

                for (int z = 0; z < _chunkSize; z++)
                {

                    _voxels[x, y, z] = new Voxel(transform.position + new Vector3(x, y, z), Color.white);

                }
                
                
            }
            
        }
        
    }
    
    
    //public void IterateVoxels


    private void OnDrawGizmos()
    {

        if (_voxels != null)
        {
            
            Gizmos.color = _gizmoColor;
            Gizmos.DrawCube(transform.position + new Vector3(_chunkSize / 2f, _chunkSize / 2f, _chunkSize / 2f), new Vector3(_chunkSize, _chunkSize, _chunkSize));
            
        }
        
        
    }
}
