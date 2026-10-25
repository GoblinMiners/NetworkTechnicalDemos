using System;
using UnityEngine;

/// <summary>
/// Estimates removed material using six linearly interpolated tetrahedra per cell.
/// Unlike counting sign flips, this catches a surface moving during a partial cut,
/// and produces zero rubble for changes wholly inside solid rock or empty space.
/// This is an approximation to the marching-cubes surface, NOT exact mesh fracture.
/// Instance scratch arrays make this allocation-free per cell; use on one thread.
/// </summary>
public sealed class TerrainVolume
{
    public struct Measure
    {
        public float Volume;
        public Vector3 Moment; // First moment in cell-local sample coordinates.
        public static Measure operator +(Measure a, Measure b) => new Measure { Volume = a.Volume + b.Volume, Moment = a.Moment + b.Moment };
        public static Measure operator -(Measure a, Measure b) => new Measure { Volume = a.Volume - b.Volume, Moment = a.Moment - b.Moment };
    }
    private static readonly Vector3[] P = {
        new Vector3(0,0,0), new Vector3(1,0,0), new Vector3(1,1,0), new Vector3(0,1,0),
        new Vector3(0,0,1), new Vector3(1,0,1), new Vector3(1,1,1), new Vector3(0,1,1)
    };
    private static readonly int[,] T = { {0,1,2,6}, {0,2,3,6}, {0,3,7,6}, {0,7,4,6}, {0,4,5,6}, {0,5,1,6} };
    private readonly float[] _d = new float[8];
    private readonly int[] _inside = new int[4], _outside = new int[4];

    public Measure SampleCell(Vector3Int cell, Func<Vector3Int, float> density)
    {
        for (int i = 0; i < 8; i++) _d[i] = density(cell + Vector3Int.RoundToInt(P[i]));
        Measure result = new Measure();
        for (int t = 0; t < 6; t++) result += ClipTetra(t);
        return result;
    }

    private Measure ClipTetra(int t)
    {
        int ni = 0, no = 0;
        for (int j = 0; j < 4; j++)
        {
            int i = T[t,j];
            if (_d[i] < 0f) _inside[ni++] = i; else _outside[no++] = i;
        }
        if (ni == 0) return new Measure();
        Measure full = Tetra(P[T[t,0]], P[T[t,1]], P[T[t,2]], P[T[t,3]]);
        if (ni == 4) return full;
        if (ni == 1)
        {
            int a = _inside[0];
            return Tetra(P[a], Edge(a,_outside[0]), Edge(a,_outside[1]), Edge(a,_outside[2]));
        }
        if (ni == 3)
        {
            int a = _outside[0];
            return full - Tetra(P[a], Edge(a,_inside[0]), Edge(a,_inside[1]), Edge(a,_inside[2]));
        }
        int u = _inside[0], v = _inside[1], c = _outside[0], d = _outside[1];
        Vector3 uc = Edge(u,c), ud = Edge(u,d), vc = Edge(v,c), vd = Edge(v,d);
        return Tetra(P[u], P[v], uc, ud) + Tetra(P[v], uc, ud, vd) + Tetra(P[v], uc, vc, vd);
    }

    private Vector3 Edge(int a, int b) => P[a] + (P[b] - P[a]) * (_d[a] / (_d[a] - _d[b]));
    private static Measure Tetra(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
    {
        float volume = Mathf.Abs(Vector3.Dot(b-a, Vector3.Cross(c-a, d-a))) / 6f;
        return new Measure { Volume = volume, Moment = (a+b+c+d) * (volume * 0.25f) };
    }
}
