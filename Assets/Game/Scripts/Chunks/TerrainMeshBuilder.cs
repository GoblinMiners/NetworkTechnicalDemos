using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>Pure-data marching cubes. Material selection is at the same CELL granularity as rewards.</summary>
public static class TerrainMeshBuilder
{
    public sealed class Result
    {
        public readonly List<Vector3> Vertices=new List<Vector3>();
        public readonly SortedDictionary<int,List<int>> Triangles=new SortedDictionary<int,List<int>>();
    }
    public static Result Build(float[,,] density,Vector3Int size,float spacing,Vector3Int globalOrigin,Func<Vector3Int,int> material)
    {
        var result=new Result();var map=new Dictionary<Vector3,int>();
        for(int x=0;x<size.x;x++)for(int y=0;y<size.y;y++)for(int z=0;z<size.z;z++)
        {
            int cube=0;
            for(int i=0;i<8;i++)
            {
                Vector3Int c=MarchingTable.Corners[i];
                if(density[x+c.x,y+c.y,z+c.z]<0f)cube|=1<<i;
            }
            if(MarchingTable.Triangles[cube,0]==-1)continue;
            int id=material(globalOrigin+new Vector3Int(x,y,z));
            if(id<=0)throw new InvalidOperationException("A surface cell needs a solid material ID, not Air.");
            List<int> triangles;
            if(!result.Triangles.TryGetValue(id,out triangles)) {triangles=new List<int>();result.Triangles.Add(id,triangles);}
            for(int i=0;MarchingTable.Triangles[cube,i]!=-1;i+=3)
            {
                Vector3 a=Vertex(x,y,z,MarchingTable.Triangles[cube,i],density,spacing);
                Vector3 b=Vertex(x,y,z,MarchingTable.Triangles[cube,i+1],density,spacing);
                Vector3 c=Vertex(x,y,z,MarchingTable.Triangles[cube,i+2],density,spacing);
                if(Vector3.Cross(b-a,c-a).sqrMagnitude<1e-14f)continue;
                triangles.Add(Index(c,result.Vertices,map));triangles.Add(Index(b,result.Vertices,map));triangles.Add(Index(a,result.Vertices,map));
            }
        }
        return result;
    }
    private static int Index(Vector3 v,List<Vector3> vertices,Dictionary<Vector3,int> map)
    {int index;if(map.TryGetValue(v,out index))return index;index=vertices.Count;vertices.Add(v);map.Add(v,index);return index;}
    private static Vector3 Vertex(int x,int y,int z,int edge,float[,,] d,float spacing)
    {
        Vector3 a=MarchingTable.Edges[edge,0],b=MarchingTable.Edges[edge,1];
        float da=d[x+(int)a.x,y+(int)a.y,z+(int)a.z],db=d[x+(int)b.x,y+(int)b.y,z+(int)b.z];
        Vector3 pa=(new Vector3(x,y,z)+a)*spacing,pb=(new Vector3(x,y,z)+b)*spacing;
        if(Mathf.Abs(da)<0.00001f)return pa;
        if(Mathf.Abs(db)<0.00001f)return pb;
        if(Mathf.Abs(da-db)<0.00001f)return pa;
        return pa+(pb-pa)*(-da/(db-da));
    }
}
