using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Deterministic, global-cell deposits. Integer hashing and fixed-point ellipsoids
/// avoid Unity Random, chunk-order dependence and platform-specific noise at ore boundaries.
/// No air is generated here: support remains solid-rock-minus-excavation.
/// </summary>
public sealed class TerrainMaterialGenerator
{
    [Serializable]
    public struct Rule
    {
        public int Id, Grid, Chance, MinRadius, MaxRadius, MinY, MaxY;
    }
    private readonly int _seed, _stone;
    private readonly Rule[] _rules;
    private const long Q = 1024;

    public TerrainMaterialGenerator(int seed, int stoneId, Rule[] rules)
    {
        _seed = seed; _stone = stoneId; _rules = rules == null ? new Rule[0] : (Rule[])rules.Clone();
        foreach (Rule r in _rules)
            if (r.Grid < 4 || r.Grid > 1024 || r.MinRadius < 1 || r.MaxRadius < r.MinRadius || r.MaxRadius > r.Grid / 2 ||
                r.MinY > r.MaxY || r.Chance < 0 || r.Chance > 1000)
                throw new ArgumentException("Invalid deposit rule.");
    }
    public TerrainMaterialGenerator(int seed, TerrainMaterialCatalog catalogue)
        : this(seed, catalogue.baseStoneId, RulesFrom(catalogue)) { }

    private static Rule[] RulesFrom(TerrainMaterialCatalog catalogue)
    {
        var result = new List<Rule>();
        foreach (var d in catalogue.definitions)
            if (d.materialId != catalogue.baseStoneId && d.generateDeposits)
                result.Add(new Rule { Id=d.materialId, Grid=d.depositGridSizeInCells, Chance=d.depositChancePerThousand,
                    MinRadius=d.minimumRadiusInCells, MaxRadius=d.maximumRadiusInCells, MinY=d.minimumCellY, MaxY=d.maximumCellY });
        return result.ToArray();
    }

    public int GetSolidMaterial(Vector3Int cell)
    {
        // Reverse order is deliberate: gold can override copper, never the reverse.
        for (int i = _rules.Length - 1; i >= 0; i--)
        {
            Rule r = _rules[i];
            if (cell.y < r.MinY || cell.y > r.MaxY) continue;
            int gx = TerrainDensityStore.FloorDiv(cell.x, r.Grid);
            int gy = TerrainDensityStore.FloorDiv(cell.y, r.Grid);
            int gz = TerrainDensityStore.FloorDiv(cell.z, r.Grid);
            for (int x = gx - 1; x <= gx + 1; x++)
                for (int y = gy - 1; y <= gy + 1; y++)
                    for (int z = gz - 1; z <= gz + 1; z++)
                    {
                        uint h = Hash(x,y,z,_seed,r.Id);
                        if (h % 1000 >= r.Chance) continue;
                        long cx = (long)x * r.Grid * Q + Offset(ref h, r.Grid);
                        long cy = (long)y * r.Grid * Q + Offset(ref h, r.Grid);
                        long cz = (long)z * r.Grid * Q + Offset(ref h, r.Grid);
                        int rx = Radius(ref h,r), ry = Radius(ref h,r), rz = Radius(ref h,r);
                        long dx = (long)cell.x * Q + Q/2 - cx;
                        long dy = (long)cell.y * Q + Q/2 - cy;
                        long dz = (long)cell.z * Q + Q/2 - cz;
                        if (Math.Abs(dx) > rx*Q || Math.Abs(dy) > ry*Q || Math.Abs(dz) > rz*Q) continue;
                        // Dimensionless fixed-point squared distance; no floating-point comparison.
                        long distance = dx*dx/(rx*rx) + dy*dy/(ry*ry) + dz*dz/(rz*rz);
                        if (distance <= Q*Q) return r.Id;
                    }
        }
        return _stone;
    }
    private static long Offset(ref uint h, int grid) { h = Mix(h); return (long)(h % (uint)(grid*1024)); }
    private static int Radius(ref uint h, Rule r) { h=Mix(h); return r.MinRadius+(int)(h%(uint)(r.MaxRadius-r.MinRadius+1)); }
    private static uint Mix(uint h)
    {
        unchecked { h ^= h >> 16; h *= 0x7feb352d; h ^= h >> 15; h *= 0x846ca68b; return h ^ (h >> 16); }
    }
    private static uint Hash(int x,int y,int z,int seed,int id)
    {
        unchecked { return Mix((uint)x*0x8da6b343 ^ (uint)y*0xd8163841 ^ (uint)z*0xcb1ab31f ^ (uint)seed ^ (uint)id*0x9e3779b9); }
    }
}
