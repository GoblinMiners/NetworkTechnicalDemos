using System;
using System.Collections.Generic;
using UnityEngine;
using Random = UnityEngine.Random;

public class Chunk : MonoBehaviour
{

    private Voxel[,,] _voxels;
    private Vector3Int _chunkSize = new Vector3Int(4, 4, 4);
    private Color _gizmoColor;
    
    private MeshFilter _meshFilter;
    private MeshRenderer _meshRenderer;
    private MeshCollider _meshCollider;
    
    
    private List<Vector3> _vertices = new List<Vector3>();
    private List<int> _triangles = new List<int>();
    private List<Vector2> _uvs = new List<Vector2>();
    
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {

     
        _meshFilter = gameObject.AddComponent<MeshFilter>();
        _meshRenderer = gameObject.AddComponent<MeshRenderer>();
        _meshCollider = gameObject.AddComponent<MeshCollider>();
        
        _voxels = new Voxel[_chunkSize.x, _chunkSize.y, _chunkSize.z];
        
        GenerateMesh();

        // _voxels = new Voxel[_chunkSize, _chunkSize, _chunkSize];
        
        //InitVoxels();

    }

    private void GenerateMesh()
    {
        
        InitVoxels();
        
        Mesh mesh = new Mesh();
        mesh.vertices = _vertices.ToArray();
        mesh.triangles = _triangles.ToArray();
        mesh.uv = _uvs.ToArray();
        
        mesh.RecalculateNormals();
        
        _meshFilter.mesh = mesh;
        _meshCollider.sharedMesh = mesh;
        
        //Apply a material or texture if needed
        _meshRenderer.material = World.Instance.GetVoxelMaterial();

        if (World.Instance != null)
        {
            
            Material voxelMaterial = World.Instance.GetVoxelMaterial();
            
            if(voxelMaterial != null)
            {
                _meshRenderer.material = voxelMaterial;
            }
            else
            {
                Debug.LogWarning("Voxel material is not assigned in the World script.");
            }
            
        }
        
        
    }

    public void Init(Vector3Int size)
    {
        
        _chunkSize = size;
        
        _voxels = new Voxel[size.x, size.y, size.z];
        InitVoxels();
        _gizmoColor = new Color(Random.value, Random.value, Random.value, 0.4f);


    }

    private void InitVoxels()
    {

        for (int x = 0; x < _chunkSize.x; x++)
        {

            for (int y = 0; y < _chunkSize.y; y++)
            {

                for (int z = 0; z < _chunkSize.z; z++)
                {

                    _voxels[x, y, z] = new Voxel(new Vector3(x, y, z), Color.white, true);
                    
                    ProcessVoxel(x, y, z);

                }
                
                
            }
            
        }
        
    }

    public void ProcessVoxel(int x, int y, int z)
    {

        if (_voxels == null || x < 0 || x >= _voxels.GetLength(0) || y < 0 ||
            y >= _voxels.GetLength(1) || z < 0 || z >= _voxels.GetLength(2)) return;
        
        
        Voxel vox = _voxels[x, y, z];

        if (vox.isActive)
        {
            
            bool[] facesVisible = new bool[6];
            
            facesVisible[0] = IsFaceVisible(x, y + 1, z); // Top
            facesVisible[1] = IsFaceVisible(x, y - 1, z);
            facesVisible[2] = IsFaceVisible(x - 1, y, z); // Left
            facesVisible[3] = IsFaceVisible(x + 1, y, z);
            facesVisible[4] = IsFaceVisible(x, y, z + 1);
            facesVisible[5] = IsFaceVisible(x, y, z - 1);
            
            
            for(int i = 0; i < facesVisible.Length; i++)
            {
                if(facesVisible[i])
                {
                    AddFaceData(x, y, z, i);
                }
            }
            
            
        }
        
        
       
        
    }

    private bool IsFaceVisible(int x, int y, int z)
    {
        
        Vector3 globalPos = transform.position + new Vector3(x, y, z);
        
        return IsVoxelHiddenInChunk(x, y, z) && IsVoxelHiddenInWorld(globalPos);
        
        
    }
    
    
    private void AddFaceData(int x, int y, int z, int faceIndex)
    {
        // Add vertices, triangles, and UVs for the face at (x, y, z) based on faceIndex

        if (faceIndex == 0) // Top face
        {
            
            _vertices.Add(new Vector3(x, y + 1, z));
            _vertices.Add(new Vector3(x , y + 1, z + 1));
            _vertices.Add(new Vector3(x + 1, y + 1, z + 1));
            _vertices.Add(new Vector3(x + 1, y + 1, z));
            _uvs.Add(new Vector2(0, 0));
            _uvs.Add(new Vector2(1, 0));
            _uvs.Add(new Vector2(1, 1));
            _uvs.Add(new Vector2(0, 1));

        }
        
        if(faceIndex == 1) // Bottom face
        {
            _vertices.Add(new Vector3(x, y, z));
            _vertices.Add(new Vector3(x + 1, y, z));
            _vertices.Add(new Vector3(x + 1, y, z + 1));
            _vertices.Add(new Vector3(x, y, z + 1));
            _uvs.Add(new Vector2(0, 0));
            _uvs.Add(new Vector2(0, 1));
            _uvs.Add(new Vector2(1, 1));
            _uvs.Add(new Vector2(1, 0));

        }
        
        if(faceIndex == 2) // Left face
        {
            _vertices.Add(new Vector3(x, y, z));
            _vertices.Add(new Vector3(x, y, z + 1));
            _vertices.Add(new Vector3(x, y + 1, z + 1));
            _vertices.Add(new Vector3(x, y + 1, z));
            _uvs.Add(new Vector2(0, 0));
            _uvs.Add(new Vector2(0, 0));
            _uvs.Add(new Vector2(0, 1));
            _uvs.Add(new Vector2(0, 1));

        }
        
        if(faceIndex == 3) // Right face
        {
            _vertices.Add(new Vector3(x + 1, y, z + 1));
            _vertices.Add(new Vector3(x + 1, y, z));
            _vertices.Add(new Vector3(x + 1, y + 1, z));
            _vertices.Add(new Vector3(x + 1, y + 1, z + 1));
            _uvs.Add(new Vector2(1, 0));
            _uvs.Add(new Vector2(1, 1));
            _uvs.Add(new Vector2(1, 1));
            _uvs.Add(new Vector2(1, 0));

        }

        if (faceIndex == 4)
        {
            
            _vertices.Add(new Vector3(x, y, z + 1));
            _vertices.Add(new Vector3(x + 1, y, z + 1));
            _vertices.Add(new Vector3(x + 1, y + 1, z + 1));
            _vertices.Add(new Vector3(x, y + 1, z + 1));
            _uvs.Add(new Vector2(0, 1));
            _uvs.Add(new Vector2(0, 1));
            _uvs.Add(new Vector2(1, 1));
            _uvs.Add(new Vector2(1, 1));
            
        }
        
        if (faceIndex == 5)
        {
            
            _vertices.Add(new Vector3(x + 1, y, z));
            _vertices.Add(new Vector3(x, y, z));
            _vertices.Add(new Vector3(x, y + 1, z));
            _vertices.Add(new Vector3(x + 1, y + 1, z));
            _uvs.Add(new Vector2(0, 0));
            _uvs.Add(new Vector2(1, 0));
            _uvs.Add(new Vector2(1, 0));
            _uvs.Add(new Vector2(0, 0));
            
        }
        
        AddTriangleIndices();

    }


    private void AddTriangleIndices()
    {
        
        int vertCount = _vertices.Count;
        
        _triangles.Add(vertCount - 4);
        _triangles.Add(vertCount - 3);
        _triangles.Add(vertCount - 2);
        
        _triangles.Add(vertCount - 4);
        _triangles.Add(vertCount - 2);
        _triangles.Add(vertCount - 1);
        
        
        
    }
    
    
    public bool IsVoxelActive(Vector3 localPos)
    {
     
        
        int x = Mathf.RoundToInt(localPos.x);
        int y = Mathf.RoundToInt(localPos.y);
        int z = Mathf.RoundToInt(localPos.z);
        
        if(x >= 0 && x < _chunkSize.x && y >= 0 && y < _chunkSize.y && z >= 0 && z < _chunkSize.z)
        {
            
            return _voxels[x, y, z].isActive;
            
        }
        
        
        return false;
     
    }

    private bool IsVoxelHiddenInWorld(Vector3 globalPos)
    {
        
        Chunk neighborChunk = World.Instance.GetChunkAt(globalPos);
        if (neighborChunk == null) return true;
        
        Vector3 localPos = neighborChunk.transform.InverseTransformPoint(globalPos);
        
        return !neighborChunk.IsVoxelActive(localPos);
        
    }
    
    
    private bool IsVoxelHiddenInChunk(int x, int y, int z)
    {
     
        if(x < 0 || x >= _chunkSize.x || y < 0 || y >= _chunkSize.y || z < 0 || z >= _chunkSize.z)
        {
            return true;
        }
        
        return !_voxels[x, y, z].isActive;
        
    }

    
    //public void IterateVoxels
    
}
