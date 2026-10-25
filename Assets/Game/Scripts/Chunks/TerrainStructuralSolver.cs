using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Main-thread, resumable structural pass. Uses face-connected solid samples.
/// All mutation during a pass MUST be removal-only. Deletions cannot create a new
/// supporting path; every deletion is also queued for a fresh follow-up pass.
/// No Unity API is accessed by a worker and no search budget implies support.
/// </summary>
public sealed class TerrainStructuralSolver
{
    public static readonly Vector3Int[] Directions = {
        Vector3Int.right, Vector3Int.left, Vector3Int.up,
        Vector3Int.down, Vector3Int.forward, Vector3Int.back
    };
    private readonly Func<Vector3Int, float> _density;
    private readonly Func<Vector3Int, bool> _anchor;
    private readonly int _thickness;
    private readonly Queue<Vector3Int> _roots = new Queue<Vector3Int>();
    private readonly Queue<Vector3Int> _thinOrigins = new Queue<Vector3Int>();
    private Vector3Int _thinOrigin;
    private int _thinOffset;
    private bool _hasThinOrigin;
    private readonly HashSet<Vector3Int> _thinChecked = new HashSet<Vector3Int>();
    private readonly HashSet<Vector3Int> _thinSeen = new HashSet<Vector3Int>();
    private sealed class ComponentLabel { public bool Supported; }
    private readonly Dictionary<Vector3Int, ComponentLabel> _labels = new Dictionary<Vector3Int, ComponentLabel>();
    private ComponentLabel _label;
    private readonly Queue<Vector3Int> _frontier = new Queue<Vector3Int>();
    private readonly Queue<List<Vector3Int>> _results = new Queue<List<Vector3Int>>();
    private List<Vector3Int> _component;
    public bool IsComplete { get; private set; }

    public TerrainStructuralSolver(IEnumerable<Vector3Int> changes, Func<Vector3Int, float> density,
        Func<Vector3Int, bool> anchor, int minimumThicknessInSamples)
    {
        _density = density;
        _anchor = anchor;
        _thickness = Math.Max(0, minimumThicknessInSamples);
        var roots = new HashSet<Vector3Int>();
        var columns = new HashSet<Vector3Int>();
        foreach (Vector3Int p in changes)
        {
            roots.Add(p);
            foreach (Vector3Int d in Directions) roots.Add(p + d);
            if (_thickness <= 0) continue;
            // A density sample changes adjacent cells, not only its own column.
            columns.Add(p);
            columns.Add(p + Vector3Int.left);
            columns.Add(p + Vector3Int.right);
            columns.Add(p + Vector3Int.forward);
            columns.Add(p + Vector3Int.back);
        }
        foreach (Vector3Int p in roots) _roots.Enqueue(p);
        foreach (Vector3Int column in columns) _thinOrigins.Enqueue(column);
    }

    public void Step(int operationBudget)
    {
        if (operationBudget < 1) throw new ArgumentOutOfRangeException(nameof(operationBudget));
        while (operationBudget-- > 0 && !IsComplete)
        {
            if (_hasThinOrigin || _thinOrigins.Count > 0)
            {
                if (!_hasThinOrigin)
                {
                    _thinOrigin = _thinOrigins.Dequeue();
                    _thinOffset = -_thickness - 2;
                    _hasThinOrigin = true;
                }
                Vector3Int p = _thinOrigin + Vector3Int.up * _thinOffset++;
                if (_thinOffset > _thickness + 2) _hasThinOrigin = false;
                if (!_thinChecked.Add(p) || _thinSeen.Contains(p)) continue;
                List<Vector3Int> span = FindThinSpan(p, _density, _thickness);
                if (span != null)
                {
                    foreach (Vector3Int n in span) _thinSeen.Add(n);
                    _results.Enqueue(span);
                }
                continue;
            }
            if (_component == null)
            {
                if (_roots.Count == 0) { IsComplete = true; continue; }
                Vector3Int root = _roots.Dequeue();
                if (_labels.ContainsKey(root) || _density(root) >= 0f) continue;
                _component = new List<Vector3Int>();
                _frontier.Clear();
                _label = new ComponentLabel();
                _labels[root] = _label;
                _frontier.Enqueue(root);
            }
            if (_frontier.Count == 0)
            {
                if (_component.Count > 0) _results.Enqueue(_component);
                _component = null;
                continue;
            }
            Vector3Int current = _frontier.Dequeue();
            if (_density(current) >= 0f) continue; // It may have been mined since enqueue.
            _component.Add(current);
            if (_anchor(current))
            {
                _label.Supported = true;
                _component = null;
                continue;
            }
            foreach (Vector3Int direction in Directions)
            {
                Vector3Int next = current + direction;
                if (_density(next) >= 0f) continue;
                ComponentLabel known;
                if (_labels.TryGetValue(next, out known))
                {
                    // Reaching a previously proven supported component is itself
                    // support. Merely skipping its visited nodes would cause false collapses.
                    if (known != _label && known.Supported)
                    {
                        _label.Supported = true;
                        _component = null;
                        break;
                    }
                    continue;
                }
                _labels[next] = _label;
                _frontier.Enqueue(next);
            }
        }
    }

    public bool TryGetCollapse(out List<Vector3Int> samples)
    {
        if (_results.Count == 0) { samples = null; return false; }
        samples = _results.Dequeue();
        return true;
    }

    /// <summary>
    /// Finds BOTH actual air/solid boundaries. Density interpolation gives the
    /// thickness in sample spacings; two samples are not blindly called two units.
    /// The candidate may be anywhere inside the slab, above OR below the cut.
    /// </summary>
    public static List<Vector3Int> FindThinSpan(Vector3Int p, Func<Vector3Int, float> density, int threshold)
    {
        if (threshold <= 0 || density(p) >= 0f) return null;
        int bottom = p.y, top = p.y;
        int maxSamples = threshold + 1;
        while (density(new Vector3Int(p.x, bottom - 1, p.z)) < 0f)
        {
            bottom--;
            if (top - bottom + 1 > maxSamples) return null;
        }
        while (density(new Vector3Int(p.x, top + 1, p.z)) < 0f)
        {
            top++;
            if (top - bottom + 1 > maxSamples) return null;
        }
        float lowerAir = density(new Vector3Int(p.x, bottom - 1, p.z));
        float lowerSolid = density(new Vector3Int(p.x, bottom, p.z));
        float upperSolid = density(new Vector3Int(p.x, top, p.z));
        float upperAir = density(new Vector3Int(p.x, top + 1, p.z));
        float lowerBoundary = bottom - 1 + lowerAir / (lowerAir - lowerSolid);
        float upperBoundary = top + (-upperSolid) / (upperAir - upperSolid);
        if (upperBoundary - lowerBoundary > threshold + 0.00001f) return null;
        var result = new List<Vector3Int>();
        for (int y = bottom; y <= top; y++) result.Add(new Vector3Int(p.x, y, p.z));
        return result;
    }
}
