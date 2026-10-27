using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Finite-volume, integer-cell approximation, not an incompressible CFD solver.
/// Every transfer subtracts exactly what it adds. Zero overrides are retained so
/// drained procedural reservoirs cannot refill on chunk reload or server restart.
/// The world adapter decides which cells are active; this class never assumes an
/// ungenerated terrain view means an opening.
/// </summary>
public sealed class GobmineFluidStore
{
    public const int Capacity = 1000;
    private readonly Func<Vector3Int, GobmineFluidCell> _base;
    private readonly Func<Vector3Int, bool> _open;
    private readonly Dictionary<Vector3Int, GobmineFluidCell> _edits = new Dictionary<Vector3Int, GobmineFluidCell>();
    private readonly Queue<Vector3Int> _work = new Queue<Vector3Int>();
    private readonly HashSet<Vector3Int> _queued = new HashSet<Vector3Int>();
    private int _turn;
    public event Action<Vector3Int, GobmineFluidCell> Changed;
    private static readonly Vector3Int[] Sides = { Vector3Int.right, Vector3Int.forward, Vector3Int.left, Vector3Int.back };
    public int Pending => _work.Count;
    public int ModifiedCells => _edits.Count;
    public GobmineFluidStore(Func<Vector3Int, GobmineFluidCell> initial, Func<Vector3Int, bool> open)
    { _base = initial ?? throw new ArgumentNullException(nameof(initial)); _open = open ?? throw new ArgumentNullException(nameof(open)); }
    public GobmineFluidCell Get(Vector3Int p) { GobmineFluidCell c; return _edits.TryGetValue(p, out c) ? c : _base(p); }
    public void Apply(Vector3Int p, GobmineFluidCell state, bool notify = false)
    {
        if (state.Amount < 0 || state.Amount > Capacity || (int)state.Kind < 0 || (int)state.Kind > 2 || (state.Amount > 0 && state.Kind == GobmineFluidKind.None))
            throw new ArgumentException("Invalid fluid state.");
        state = new GobmineFluidCell(state.Kind, state.Amount); _edits[p] = state;
        if (notify) Changed?.Invoke(p, state);
    }
    public void Wake(Vector3Int p) { if (_queued.Add(p)) _work.Enqueue(p); }
    public void WakeAround(Vector3Int p)
    { Wake(p); Wake(p + Vector3Int.up); Wake(p + Vector3Int.down); foreach (var d in Sides) Wake(p + d); }
    public List<GobmineFluidPoint> CopyOverrides()
    {
        var result = new List<GobmineFluidPoint>(_edits.Count);
        foreach (var e in _edits) result.Add(new GobmineFluidPoint { Cell = e.Key, Kind = e.Value.Kind, Amount = e.Value.Amount });
        return result;
    }
    public void Import(IEnumerable<GobmineFluidPoint> points)
    { foreach (var p in points) Apply(p.Cell, new GobmineFluidCell(p.Kind, p.Amount)); }
    public bool Step(Func<Vector3Int, bool> active)
    {
        if (_work.Count == 0) return false;
        Vector3Int p = _work.Dequeue(); _queued.Remove(p);
        // Dormant boundary holds its value. Discovery wakes it again when a player
        // approaches. Never pour into an inactive/unresolved region.
        if (!active(p)) return true;
        GobmineFluidCell from = Get(p);
        if (from.Amount == 0 || !_open(p)) return true;
        int maxFlow = from.Kind == GobmineFluidKind.Lava ? 100 : Capacity;
        Transfer(p, p + Vector3Int.down, maxFlow, false, active);
        from = Get(p);
        if (from.Amount == 0) return true;
        int start = (_turn++) & 3;
        for (int i = 0; i < 4; i++) Transfer(p, p + Sides[(start+i)&3], maxFlow, true, active);
        return true;
    }
    private void Transfer(Vector3Int fromPos, Vector3Int toPos, int maximum, bool sideways, Func<Vector3Int, bool> active)
    {
        if (!active(toPos) || !_open(toPos)) return;
        var from = Get(fromPos); var to = Get(toPos);
        if (from.Amount == 0 || (to.Amount > 0 && to.Kind != from.Kind)) return; // No mixing/solidification in v1.
        int available = Capacity - to.Amount;
        int amount = sideways ? Math.Max(0, (from.Amount-to.Amount)/2) : from.Amount;
        amount = Math.Min(maximum, Math.Min(available, amount));
        if (amount <= 0) return;
        GobmineFluidKind kind = from.Kind;
        Apply(fromPos, new GobmineFluidCell(kind, from.Amount-amount), true);
        Apply(toPos, new GobmineFluidCell(kind, to.Amount+amount), true);
        WakeAround(fromPos); WakeAround(toPos);
    }
}
