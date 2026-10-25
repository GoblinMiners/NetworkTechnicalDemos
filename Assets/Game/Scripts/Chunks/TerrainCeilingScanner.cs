using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Incremental thin-roof checks, independent of the island/support flood fill.
/// Density is negative in rock. Thickness is measured between interpolated air
/// boundaries, not by counting solid samples. No scene or networking APIs here.
/// </summary>
public sealed class TerrainCeilingScanner
{
    public struct Span
    {
        public int Bottom, Top;
        public float Thickness;
    }
    private const int BandHeight = 8;
    private readonly Func<Vector3Int, float> _density;
    private readonly float _threshold;
    private readonly int _padding;
    private readonly Queue<Vector3Int> _urgent = new Queue<Vector3Int>();
    private readonly Queue<Vector3Int> _normal = new Queue<Vector3Int>();
    private readonly Dictionary<Vector3Int, bool> _queued = new Dictionary<Vector3Int, bool>();
    private readonly HashSet<Vector3Int> _pendingSamples = new HashSet<Vector3Int>();
    private readonly Queue<List<Vector3Int>> _results = new Queue<List<Vector3Int>>();
    private Vector3Int _column;
    private int _nextY, _endY;
    private bool _hasColumn;

    public bool HasPending => _hasColumn || _queued.Count > 0 || _results.Count > 0;
    public int PendingBands => _queued.Count + (_hasColumn ? 1 : 0);
    public int PendingSamples => _pendingSamples.Count;

    public TerrainCeilingScanner(Func<Vector3Int, float> density, float maximumThicknessInSamples)
    {
        _density = density ?? throw new ArgumentNullException(nameof(density));
        if (float.IsNaN(maximumThicknessInSamples) || float.IsInfinity(maximumThicknessInSamples) || maximumThicknessInSamples < 0f)
            throw new ArgumentOutOfRangeException(nameof(maximumThicknessInSamples));
        _threshold = maximumThicknessInSamples;
        _padding = Mathf.CeilToInt(_threshold) + 2;
    }

    public void NotifyChange(Vector3Int p, bool urgent)
    {
        // Applied removals release the deduplication entry, not newly queued work.
        if (_density(p) >= 0f) _pendingSamples.Remove(p);
        if (_threshold <= 0f) return;
        // Include diagonal neighbours: a lattice sample belongs to eight cells.
        for (int x = -1; x <= 1; x++)
            for (int z = -1; z <= 1; z++)
                QueueColumn(new Vector3Int(p.x + x, p.y, p.z + z), urgent);
    }

    private void QueueColumn(Vector3Int p, bool urgent)
    {
        Vector3Int key = new Vector3Int(p.x, TerrainDensityStore.FloorDiv(p.y, BandHeight), p.z);
        bool existing;
        if (_queued.TryGetValue(key, out existing))
        {
            if (!urgent || existing) return;
            // Promote an old saved-world/cascade check ahead of background work.
            _queued[key] = true;
            _urgent.Enqueue(key);
            return;
        }
        _queued.Add(key, urgent);
        (urgent ? _urgent : _normal).Enqueue(key);
    }

    private bool BeginColumn()
    {
        while (_urgent.Count > 0 || _normal.Count > 0)
        {
            bool urgent = _urgent.Count > 0;
            Vector3Int key = (urgent ? _urgent : _normal).Dequeue();
            bool expected;
            if (!_queued.TryGetValue(key, out expected) || expected != urgent) continue;
            _queued.Remove(key);
            _column = key;
            _nextY = key.y * BandHeight - _padding;
            _endY = (key.y + 1) * BandHeight - 1 + _padding;
            return _hasColumn = true;
        }
        return false;
    }

    public void Step(int checks)
    {
        if (checks < 1) throw new ArgumentOutOfRangeException(nameof(checks));
        while (checks-- > 0)
        {
            if (!_hasColumn && !BeginColumn()) return;
            Vector3Int p = new Vector3Int(_column.x, _nextY++, _column.z);
            Span span;
            if (!_pendingSamples.Contains(p) && TryFindSpan(p, _density, _threshold, out span))
            {
                var removed = new List<Vector3Int>(span.Top - span.Bottom + 1);
                for (int y = span.Bottom; y <= span.Top; y++)
                {
                    Vector3Int sample = new Vector3Int(p.x, y, p.z);
                    if (_pendingSamples.Add(sample)) removed.Add(sample);
                }
                _nextY = Math.Max(_nextY, span.Top + 1);
                if (removed.Count > 0)
                {
                    _results.Enqueue(removed);
                    // Walk the connected weak layer even when it extends beyond
                    // the brush. It may remain attached to the surrounding walls.
                    p.y = (span.Bottom + span.Top) / 2;
                    QueueColumn(p + Vector3Int.left, false);
                    QueueColumn(p + Vector3Int.right, false);
                    QueueColumn(p + Vector3Int.forward, false);
                    QueueColumn(p + Vector3Int.back, false);
                }
            }
            if (_nextY > _endY) _hasColumn = false;
        }
    }

    public bool TryTakeCollapse(out List<Vector3Int> samples)
    {
        if (_results.Count == 0) { samples = null; return false; }
        samples = _results.Dequeue();
        return true;
    }

    public static bool TryFindSpan(Vector3Int p, Func<Vector3Int, float> density, float threshold, out Span span)
    {
        span = default(Span);
        if (threshold <= 0f || density(p) >= 0f) return false;
        int bottom = p.y, top = p.y;
        int maxSamples = Mathf.CeilToInt(threshold) + 2;
        while (density(new Vector3Int(p.x, bottom - 1, p.z)) < 0f)
        {
            bottom--;
            if (top - bottom + 1 > maxSamples) return false;
        }
        while (density(new Vector3Int(p.x, top + 1, p.z)) < 0f)
        {
            top++;
            if (top - bottom + 1 > maxSamples) return false;
        }
        float la = density(new Vector3Int(p.x, bottom - 1, p.z));
        float ls = density(new Vector3Int(p.x, bottom, p.z));
        float us = density(new Vector3Int(p.x, top, p.z));
        float ua = density(new Vector3Int(p.x, top + 1, p.z));
        float lower = bottom - 1 + la / (la - ls);
        float upper = top + (-us) / (ua - us);
        float thickness = upper - lower;
        if (thickness > threshold + 0.00001f) return false;
        span = new Span { Bottom = bottom, Top = top, Thickness = thickness };
        return true;
    }
}
