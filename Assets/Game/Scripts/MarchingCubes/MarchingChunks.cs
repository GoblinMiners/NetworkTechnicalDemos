using UnityEngine;
using System.Collections.Generic;

[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer), typeof(MeshCollider))]
public class MarchingChunk : MonoBehaviour
{
    private int size;
    private int seed;
    private Vector3Int positionOffset;
    
    // The scalar field holding noise data
    public float[,,] densities;
    private float isoLevel = 0.5f;

    private MeshFilter meshFilter;
    private MeshCollider meshCollider;

    public void Initialize(Vector3Int pos, int chunkSize, int worldSeed, Material mat)
    {
        positionOffset = pos;
        size = chunkSize;
        seed = worldSeed;

        GetComponent<MeshRenderer>().material = mat;
        meshFilter = GetComponent<MeshFilter>();
        meshCollider = GetComponent<MeshCollider>();

        GenerateScalarField();
        RebuildMesh();
    }

    private void GenerateScalarField()
    {
        densities = new float[size + 1, size + 1, size + 1];

        for (int x = 0; x <= size; x++)
        {
            for (int y = 0; y <= size; y++)
            {
                for (int z = 0; z <= size; z++)
                {
                    float worldX = x + positionOffset.x;
                    float worldY = y + positionOffset.y;
                    float worldZ = z + positionOffset.z;

                    // Combine coordinates with the network seed so all players generate the exact same noise
                    float noise = Utils.fBM3D(worldX + seed, worldY, worldZ + seed, 0.03f, 3);
                    
                    // Force the bottom layer to be solid bedrock (preventing players falling out of the world)
                    if (worldY <= 0) densities[x, y, z] = 1.0f;
                    // Lower altitudes are solid, higher altitudes are air, carved out by the 3D noise
                    else densities[x, y, z] = (worldY < 40) ? noise + 0.5f : noise; 
                }
            }
        }
    }

    public void ModifyDensity(Vector3Int globalHit, float radius, float amount)
    {
        // Convert global hit coordinates to local array indices
        int localX = globalHit.x - positionOffset.x;
        int localY = globalHit.y - positionOffset.y;
        int localZ = globalHit.z - positionOffset.z;

        // Apply spherical carving
        for (int x = -Mathf.CeilToInt(radius); x <= Mathf.CeilToInt(radius); x++)
        {
            for (int y = -Mathf.CeilToInt(radius); y <= Mathf.CeilToInt(radius); y++)
            {
                for (int z = -Mathf.CeilToInt(radius); z <= Mathf.CeilToInt(radius); z++)
                {
                    if (x * x + y * y + z * z <= radius * radius) // Inside the sphere
                    {
                        int targetX = localX + x;
                        int targetY = localY + y;
                        int targetZ = localZ + z;

                        if (targetX >= 0 && targetX <= size && 
                            targetY >= 0 && targetY <= size && 
                            targetZ >= 0 && targetZ <= size)
                        {
                            densities[targetX, targetY, targetZ] += amount;
                        }
                    }
                }
            }
        }
        
        RebuildMesh();
    }

    public void RebuildMesh()
    {
        List<Vector3> vertices = new List<Vector3>();
        List<int> triangles = new List<int>();

        for (int x = 0; x < size; x++)
        {
            for (int y = 0; y < size; y++)
            {
                for (int z = 0; z < size; z++)
                {
                    // 1. Determine the cube index based on the 8 corners
                    int cubeIndex = 0;
                    for (int i = 0; i < 8; i++)
                    {
                        Vector3Int offset = MarchingTable.Corners[i];
                        if (densities[x + offset.x, y + offset.y, z + offset.z] < isoLevel)
                        {
                            cubeIndex |= 1 << i;
                        }
                    }

                    // 2. Lookup triangles for this specific configuration
                    for (int i = 0; i < 16; i += 3)
                    {
                        if (MarchingTable.Triangles[cubeIndex, i] == -1) break;

                        // 3. Create the 3 vertices for this triangle
                        for (int j = 0; j < 3; j++)
                        {
                            int edgeIndex = MarchingTable.Triangles[cubeIndex, i + j];

                            Vector3 p1Offset = MarchingTable.Edges[edgeIndex, 0];
                            Vector3 p2Offset = MarchingTable.Edges[edgeIndex, 1];

                            // Get the raw scalar density at those exact corners
                            float val1 = densities[x + (int)p1Offset.x, y + (int)p1Offset.y, z + (int)p1Offset.z];
                            float val2 = densities[x + (int)p2Offset.x, y + (int)p2Offset.y, z + (int)p2Offset.z];

                            Vector3 p1 = new Vector3(x, y, z) + p1Offset;
                            Vector3 p2 = new Vector3(x, y, z) + p2Offset;

                            // Interpolate along the edge for smooth terrain slopes
                            float mu = (isoLevel - val1) / (val2 - val1);
                            Vector3 vertexPosition = Vector3.Lerp(p1, p2, mu);

                            vertices.Add(vertexPosition);
                            triangles.Add(vertices.Count - 1);
                        }
                    }
                }
            }
        }

        Mesh newMesh = new Mesh();
        
        // Prevent crashes if a chunk generates more than 65,000 vertices
        newMesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32; 
        
        newMesh.vertices = vertices.ToArray();
        newMesh.triangles = triangles.ToArray();
        newMesh.RecalculateNormals();

        meshFilter.mesh = newMesh;
        meshCollider.sharedMesh = newMesh;
    }
}