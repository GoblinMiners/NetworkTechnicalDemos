using System;
using UnityEngine;

public enum TerrainCellState : byte { Air, Surface, Solid }
public struct TerrainCellInfo
{
    public Vector3Int Cell;
    public TerrainCellState State;
    public int MaterialId;
}

public static class TerrainQuery
{
    public static TerrainCellState CellState(Vector3Int cell, Func<Vector3Int,float> density)
    {
        int solid = 0;
        for (int x=0;x<=1;x++) for(int y=0;y<=1;y++) for(int z=0;z<=1;z++)
            if (density(cell+new Vector3Int(x,y,z)) < 0f) solid++;
        return solid == 0 ? TerrainCellState.Air : solid == 8 ? TerrainCellState.Solid : TerrainCellState.Surface;
    }
    // Point query in the interpolated density field. Physics raycasts remain the authority
    // for actual mesh hits, especially in ambiguous marching-cubes cells.
    public static float SampleDensity(Vector3 latticePosition, Func<Vector3Int,float> density)
    {
        Vector3Int p=Vector3Int.FloorToInt(latticePosition);
        Vector3 t=latticePosition-(Vector3)p;
        float result=0;
        for(int x=0;x<=1;x++) for(int y=0;y<=1;y++) for(int z=0;z<=1;z++)
            result += density(p+new Vector3Int(x,y,z))*(x==0?1-t.x:t.x)*(y==0?1-t.y:t.y)*(z==0?1-t.z:t.z);
        return result;
    }
}
