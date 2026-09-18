using System.Collections.Generic;
using System.Threading.Tasks; // NEW: Required for multithreading
using UnityEngine;

[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer), typeof(MeshCollider))]
public class Chunk : MonoBehaviour
{
    private float[,,] _densities;
    private Vector3Int _chunkSize;
    private float _voxelSize = 1f; 
    private float _isoLevel = 0f; 
    
    private MeshFilter _meshFilter;
    private MeshCollider _meshCollider;
    private MeshRenderer _meshRenderer;
    private Mesh _mesh; 

    private List<Vector3> _vertices = new List<Vector3>();
    private List<int> _triangles = new List<int>();

    // NEW: Prevents the chunk from trying to build its mesh twice at the exact same time
    private bool _isGenerating = false;
    private bool _needsRegeneration = false;

    public async void Init(Vector3Int size, float voxelSize, Vector3 globalPosition, float scale, int seed, float[,,] savedData)
    {
        _chunkSize = size;
        _voxelSize = voxelSize;
        
        if (!TryGetComponent(out _meshFilter)) _meshFilter = gameObject.AddComponent<MeshFilter>();
        if (!TryGetComponent(out _meshRenderer)) _meshRenderer = gameObject.AddComponent<MeshRenderer>();
        if (!TryGetComponent(out _meshCollider)) _meshCollider = gameObject.AddComponent<MeshCollider>();
        
        if (World.Instance != null) _meshRenderer.material = World.Instance.GetVoxelMaterial();

        if (_mesh == null)
        {
            _mesh = new Mesh();
            _mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            _meshFilter.sharedMesh = _mesh;
        }
        else 
        {
            _meshCollider.sharedMesh = null;
            _mesh.Clear();
        }

        if (savedData != null)
        {
            _densities = savedData;
        }
        else
        {
            _densities = new float[_chunkSize.x + 1, _chunkSize.y + 1, _chunkSize.z + 1];
            
            // OFF-LOAD THE DENSITY MATH TO A BACKGROUND THREAD!
            await Task.Run(() => GenerateDensities(globalPosition, scale, seed));
        }

        // Build the mesh asynchronously
        await GenerateMeshAsync();
    }

    public float[,,] GetDensities()
    {
        return _densities;
    }

    private void GenerateDensities(Vector3 globalPosition, float scale, int seed)
    {
        // NOTE: This now runs on a background thread, so NO Unity components can be touched here.
        for (int x = 0; x <= _chunkSize.x; x++)
        {
            for (int y = 0; y <= _chunkSize.y; y++)
            {
                for (int z = 0; z <= _chunkSize.z; z++)
                {
                    Vector3 worldPos = globalPosition + (new Vector3(x, y, z) * _voxelSize);
                    float density = -1f; 

                    if (worldPos.x > -4f && worldPos.x < 4f &&
                        worldPos.y > -2f && worldPos.y < 5f &&
                        worldPos.z > -5f && worldPos.z < 15f)
                    {
                        density = 1f; 
                    }

                    _densities[x, y, z] = density;
                }
            }
        }
    }

    // NEW: The Async Mesh Generator
   private async Task GenerateMeshAsync()
    {
        // THE FIX: If the chunk is already busy, flag it to rebuild AGAIN as soon as it finishes.
        if (_isGenerating) 
        {
            _needsRegeneration = true;
            return;
        }

        _isGenerating = true;

        // Loop until there is no more new damage to process
        do 
        {
            _needsRegeneration = false;

            // 1. DO ALL THE HEAVY MATH ON A BACKGROUND THREAD
            await Task.Run(() => 
            {
                _vertices.Clear();
                _triangles.Clear();
                Dictionary<Vector3, int> vertexMap = new Dictionary<Vector3, int>();

                for (int x = 0; x < _chunkSize.x; x++)
                {
                    for (int y = 0; y < _chunkSize.y; y++)
                    {
                        for (int z = 0; z < _chunkSize.z; z++)
                        {
                            int cubeIndex = 0;

                            for (int i = 0; i < 8; i++)
                            {
                                Vector3Int corner = MarchingTable.Corners[i];
                                if (_densities[x + corner.x, y + corner.y, z + corner.z] < _isoLevel)
                                {
                                    cubeIndex |= (1 << i);
                                }
                            }

                            for (int i = 0; MarchingTable.Triangles[cubeIndex, i] != -1; i += 3)
                            {
                                int a = MarchingTable.Triangles[cubeIndex, i];
                                int b = MarchingTable.Triangles[cubeIndex, i + 1];
                                int c = MarchingTable.Triangles[cubeIndex, i + 2];

                                Vector3 vertA = GetInterpolatedVertex(x, y, z, a);
                                Vector3 vertB = GetInterpolatedVertex(x, y, z, b);
                                Vector3 vertC = GetInterpolatedVertex(x, y, z, c);

                                _triangles.Add(GetOrAddVertex(vertC, vertexMap));
                                _triangles.Add(GetOrAddVertex(vertB, vertexMap));
                                _triangles.Add(GetOrAddVertex(vertA, vertexMap));
                            }
                        }
                    }
                }
            });

            // THE FIX: Unlock the chunk before aborting so it can safely load later!
            if (this == null || gameObject == null || !gameObject.activeInHierarchy) 
            {
                _isGenerating = false;
                _needsRegeneration = false;
                return;
            }

            // 2. COME BACK TO MAIN THREAD TO UPDATE UNITY COMPONENTS
            _mesh.Clear(); 
            _mesh.SetVertices(_vertices);
            _mesh.SetTriangles(_triangles, 0);
            
            _mesh.RecalculateNormals();
            _mesh.RecalculateBounds();
            
            _meshFilter.sharedMesh = _mesh;

            if (_vertices.Count >= 3 && _triangles.Count >= 3) _meshCollider.sharedMesh = _mesh; 
            else _meshCollider.sharedMesh = null; 

        } while (_needsRegeneration); // If you mined it while it was building, it loops and updates the mesh again!

        _isGenerating = false;
    }
    private int GetOrAddVertex(Vector3 vertex, Dictionary<Vector3, int> vertexMap)
    {
        if (vertexMap.TryGetValue(vertex, out int index)) return index;
        index = _vertices.Count;
        _vertices.Add(vertex);
        vertexMap.Add(vertex, index);
        return index;
    }

    private Vector3 GetInterpolatedVertex(int x, int y, int z, int edgeIndex)
    {
        Vector3 edgeStart = MarchingTable.Edges[edgeIndex, 0];
        Vector3 edgeEnd = MarchingTable.Edges[edgeIndex, 1];

        float val1 = _densities[x + (int)edgeStart.x, y + (int)edgeStart.y, z + (int)edgeStart.z];
        float val2 = _densities[x + (int)edgeEnd.x, y + (int)edgeEnd.y, z + (int)edgeEnd.z];

        Vector3 pos1 = (new Vector3(x, y, z) + edgeStart) * _voxelSize;
        Vector3 pos2 = (new Vector3(x, y, z) + edgeEnd) * _voxelSize;

        if (Mathf.Abs(_isoLevel - val1) < 0.00001f) return pos1;
        if (Mathf.Abs(_isoLevel - val2) < 0.00001f) return pos2;
        if (Mathf.Abs(val1 - val2) < 0.00001f) return pos1;

        float t = (_isoLevel - val1) / (val2 - val1);
        return pos1 + t * (pos2 - pos1);
    }

    public void EditTerrain(Vector3 worldHitPoint, float radius, float amount)
    {
        bool updated = false;
        Vector3 localHit = transform.InverseTransformPoint(worldHitPoint);

        int minX = Mathf.Max(0, Mathf.FloorToInt((localHit.x - radius) / _voxelSize));
        int maxX = Mathf.Min(_chunkSize.x, Mathf.CeilToInt((localHit.x + radius) / _voxelSize));
        int minY = Mathf.Max(0, Mathf.FloorToInt((localHit.y - radius) / _voxelSize));
        int maxY = Mathf.Min(_chunkSize.y, Mathf.CeilToInt((localHit.y + radius) / _voxelSize));
        int minZ = Mathf.Max(0, Mathf.FloorToInt((localHit.z - radius) / _voxelSize));
        int maxZ = Mathf.Min(_chunkSize.z, Mathf.CeilToInt((localHit.z + radius) / _voxelSize));

        for (int x = minX; x <= maxX; x++)
        {
            for (int y = minY; y <= maxY; y++)
            {
                for (int z = minZ; z <= maxZ; z++)
                {
                    Vector3 voxelWorldPos = transform.position + (new Vector3(x, y, z) * _voxelSize);
                    float distance = Vector3.Distance(voxelWorldPos, worldHitPoint);
                    
                    if (distance <= radius)
                    {
                        float falloff = 1f - (distance / radius);
                        
                        // Because this math runs on the main thread, no damage is ever lost!
                        _densities[x, y, z] += (amount * falloff);
                        updated = true;
                    }
                }
            }
        }
        
        if (updated) 
        {
            // Fire off the background thread without waiting
            _ = GenerateMeshAsync(); 
        }
    }
}