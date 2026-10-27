using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Disjoint ellipsoidal caverns inside a continuous solid scaffold. No cave touches
/// a macrocell boundary. This invariant supplies an actual support certificate;
/// neither chunk boundaries nor traversal budgets are treated as support.
/// </summary>
public sealed class GobmineCavernGenerator
{
    public struct Chamber
    {
        public bool Exists;
        public Vector3 Centre, Radius;
        public GobmineFluidKind Fluid;
        public float FluidLevel;
    }
    private readonly GobmineExpeditionSettings _s;
    private readonly int _seed;
    private readonly float _voxel;
    private readonly Dictionary<Vector3Int, Chamber> _cache = new Dictionary<Vector3Int, Chamber>();
    public GobmineCavernGenerator(int seed, float voxel, GobmineExpeditionSettings settings)
    { settings.ValidateOrThrow(voxel); _seed = seed; _voxel = voxel; _s = settings; }
    public Chamber AtWorld(Vector3 p) => AtGrid(Vector3Int.FloorToInt(p / _s.cavernGridMetres));
    public Chamber AtGrid(Vector3Int cell)
    {
        Chamber result;
        if (_cache.TryGetValue(cell, out result)) return result;
        uint h = Hash(cell, _seed);
        bool selected = h % 1000 < _s.cavernChancePerThousand;
        float grid = _s.cavernGridMetres;
        // Jitter <= 8% and radii <= 32% guarantee >= 10% solid boundary bands.
        Vector3 centre = ((Vector3)cell + Vector3.one * 0.5f) * grid;
        centre += new Vector3(Unit(ref h), Unit(ref h), Unit(ref h)) * (grid * 0.08f);
        Vector3 radius = new Vector3(grid * _s.horizontalRadiusFraction, grid * _s.verticalRadiusFraction, grid * _s.horizontalRadiusFraction);
        radius = Vector3.Scale(radius, new Vector3(0.8f + 0.2f * Positive(ref h), 0.75f + 0.25f * Positive(ref h), 0.8f + 0.2f * Positive(ref h)));
        bool deepEnough = _s.surfaceReferenceY - (centre.y + radius.y) >= _s.firstCavernDepth;
        // The starting room is never overlapped, independently of surfaceReferenceY.
        bool roomOverlap = centre.x - radius.x <= 14 && centre.x + radius.x >= -14 &&
            centre.y - radius.y <= 10 && centre.y + radius.y >= -6 && centre.z - radius.z <= 14 && centre.z + radius.z >= -14;
        result = new Chamber { Exists = selected && deepEnough && !roomOverlap, Centre = centre, Radius = radius };
        h = Mix(h);
        if (result.Exists)
        {
            // Whole lava chambers must be below the threshold (not just their centre).
            bool lavaDepth = _s.surfaceReferenceY - (centre.y + radius.y) >= _s.lavaStartsAtDepth;
            if (lavaDepth && h % 1000 < _s.lavaPocketChancePerThousand) result.Fluid = GobmineFluidKind.Lava;
            else if (Mix(h) % 1000 < _s.waterPocketChancePerThousand) result.Fluid = GobmineFluidKind.Water;
            result.FluidLevel = centre.y - radius.y + 2f * radius.y * _s.initialFluidFillFraction;
        }
        if (_cache.Count >= 4096) _cache.Clear();
        _cache[cell] = result; return result;
    }
    public float ModifyDensity(Vector3Int sample, float rock)
    {
        Vector3 p = (Vector3)sample * _voxel; Chamber cave = AtWorld(p);
        if (!cave.Exists) return rock;
        double x = (p.x - cave.Centre.x) / cave.Radius.x, y = (p.y - cave.Centre.y) / cave.Radius.y, z = (p.z - cave.Centre.z) / cave.Radius.z;
        // Quantize to avoid tiny floating-point differences near density zero.
        float field = (float)(Math.Round((1.0 - x*x - y*y - z*z) * 8192.0) / 4096.0);
        return Mathf.Max(rock, field);
    }
    public Vector3Int SupportEndpoint(Vector3Int sample)
    {
        Vector3 world = (Vector3)sample * _voxel;
        Vector3Int grid = Vector3Int.FloorToInt(world / _s.cavernGridMetres);
        Chamber cave = AtGrid(grid);
        float split = cave.Exists ? cave.Centre.y : (grid.y + 0.5f) * _s.cavernGridMetres;
        int seam = world.y >= split ? grid.y + 1 : grid.y;
        return new Vector3Int(sample.x, Mathf.RoundToInt(seam * _s.cavernGridMetres / _voxel), sample.z);
    }
    public GobmineFluidCell BaseFluid(Vector3Int fluidCell)
    {
        float size = _s.fluidCellMetres; Vector3 min = (Vector3)fluidCell * size;
        Chamber cave = AtWorld(min + Vector3.one * (size * 0.5f));
        if (!cave.Exists || cave.Fluid == GobmineFluidKind.None || cave.FluidLevel <= min.y) return default;
        // Only fully contained coarse cells hold initial fluid. All ellipsoid corners
        // inside implies the whole cuboid is inside this convex cavity.
        for (int x = 0; x <= 1; x++) for (int y = 0; y <= 1; y++) for (int z = 0; z <= 1; z++)
        {
            Vector3 p = min + new Vector3(x, y, z) * size;
            double dx = (p.x-cave.Centre.x)/cave.Radius.x, dy = (p.y-cave.Centre.y)/cave.Radius.y, dz = (p.z-cave.Centre.z)/cave.Radius.z;
            if (dx*dx + dy*dy + dz*dz >= 0.999) return default;
        }
        return new GobmineFluidCell(cave.Fluid, Mathf.FloorToInt(Mathf.Clamp01((cave.FluidLevel-min.y)/size)*GobmineFluidStore.Capacity));
    }
    private static float Unit(ref uint h) { h = Mix(h); return (h % 2001 - 1000f) / 1000f; }
    private static float Positive(ref uint h) { h = Mix(h); return (h % 1001) / 1000f; }
    private static uint Hash(Vector3Int c, int seed) { unchecked { return Mix((uint)c.x*0x8da6b343 ^ (uint)c.y*0xd8163841 ^ (uint)c.z*0xcb1ab31f ^ (uint)seed); } }
    private static uint Mix(uint h) { unchecked { h ^= h >> 16; h *= 0x7feb352d; h ^= h >> 15; h *= 0x846ca68b; return h ^ (h >> 16); } }
}
