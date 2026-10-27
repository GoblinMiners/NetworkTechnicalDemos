using System;
using System.Collections.Generic;
using Mirror;
using UnityEngine;

public partial class World
{
    private GobmineFluidStore _fluids;
    private readonly Dictionary<Vector3Int,bool> _fluidOpenCache = new Dictionary<Vector3Int,bool>();
    private readonly Dictionary<Vector3Int,GobmineFluidCell> _fluidOutgoing = new Dictionary<Vector3Int,GobmineFluidCell>();
    private readonly List<Vector3Int> _fluidScanOffsets = new List<Vector3Int>();
    private readonly List<Vector3Int> _fluidScanCentres = new List<Vector3Int>();
    private int _fluidDiscoveryCursor;
    private float _nextDiscoveryCycle;
    public int PendingFluidDiscovery => Mathf.Max(0, _fluidScanOffsets.Count*_fluidScanCentres.Count-_fluidDiscoveryCursor);
    private readonly List<Vector3Int> _fluidPlayerRegions = new List<Vector3Int>();
    private readonly List<FluidDelta> _bufferedFluidDeltas = new List<FluidDelta>();
    private GobmineFluidRenderer _fluidRenderer;
    private float _nextFluidRefresh, _nextFluidSend;
    private long _fluidRevision, _lastFluidClientRevision;
    private struct FluidDelta { public long Revision; public GobmineFluidPoint[] Points; }
    public float LastFluidMilliseconds { get; private set; }
    private readonly System.Diagnostics.Stopwatch _fluidWatch = new System.Diagnostics.Stopwatch();
    private static readonly Unity.Profiling.ProfilerMarker FluidMarker = new Unity.Profiling.ProfilerMarker("Gobmine.FluidSimulation");

    private void InitializeExpeditionFluids()
    {
        _fluidOpenCache.Clear(); _fluidScanCentres.Clear(); _fluidDiscoveryCursor=0; _fluidPlayerRegions.Clear();
        _fluidScanOffsets.Clear(); _nextDiscoveryCycle=0f;
        if (ExpeditionEnabled)
        {
            int radius=Mathf.CeilToInt(ExpeditionProfile.fluidActiveRadiusMetres/ExpeditionProfile.fluidCellMetres)+1;
            for(int x=-radius;x<=radius;x++)for(int y=-radius;y<=radius;y++)for(int z=-radius;z<=radius;z++)
                if(x*x+y*y+z*z<=radius*radius)_fluidScanOffsets.Add(new Vector3Int(x,y,z));
            _fluidScanOffsets.Sort((a,b)=>a.sqrMagnitude.CompareTo(b.sqrMagnitude));
        }
        if (_fluidRenderer != null) { Destroy(_fluidRenderer.gameObject); _fluidRenderer = null; }
        _fluids = _caverns == null ? null : new GobmineFluidStore(_caverns.BaseFluid, IsFluidCellOpen);
        if (_fluids != null) _fluids.Changed += FluidChanged;
        // Do not discard buffered live deltas when a late-join baseline begins.
    }
    private void ResetFluidRuntime()
    {
        if (_fluidRenderer != null) Destroy(_fluidRenderer.gameObject);
        _fluidRenderer = null; _fluids = null; _fluidRevision = 0; _lastFluidClientRevision = 0;
        _fluidOpenCache.Clear(); _fluidOutgoing.Clear(); _bufferedFluidDeltas.Clear();
        _fluidScanCentres.Clear(); _fluidScanOffsets.Clear(); _fluidDiscoveryCursor=0; _fluidPlayerRegions.Clear();
        _nextFluidRefresh = _nextFluidSend = 0f; LastFluidMilliseconds = 0f;
    }
    public GobmineFluidCell FluidCell(Vector3Int cell) => _fluids == null ? default : _fluids.Get(cell);
    public GobmineFluidKind FluidAt(Vector3 position)
    {
        if (!ExpeditionEnabled || _fluids == null || !IsReady || !IsAirAt(position)) return GobmineFluidKind.None;
        float size = ExpeditionProfile.fluidCellMetres;
        Vector3Int cell = Vector3Int.FloorToInt(position / size); var state = _fluids.Get(cell);
        float surface = (cell.y + (float)state.Amount/GobmineFluidStore.Capacity) * size;
        return position.y < surface && state.Amount > 0 ? state.Kind : GobmineFluidKind.None;
    }
    public GobmineSpaceKind SpaceAt(Vector3 position)
    {
        if (!IsReady || _terrain == null) return GobmineSpaceKind.Unavailable;
        if (!IsAirAt(position)) return GobmineSpaceKind.Solid;
        GobmineFluidKind fluid = FluidAt(position);
        return fluid == GobmineFluidKind.Water ? GobmineSpaceKind.Water : fluid == GobmineFluidKind.Lava ? GobmineSpaceKind.Lava : GobmineSpaceKind.Air;
    }
    public float WaterDepthBelow(Vector3 position)
    {
        if (_fluids == null || !ExpeditionEnabled) return 0f;
        float size = ExpeditionProfile.fluidCellMetres;
        Vector3Int cell = Vector3Int.FloorToInt(position / size); var state = _fluids.Get(cell);
        if (state.Kind != GobmineFluidKind.Water || state.Amount <= 0) return 0f;
        float depth = ((float)state.Amount/GobmineFluidStore.Capacity)*size;
        for (int i = 1; i <= 16; i++)
        {
            var below = _fluids.Get(cell + Vector3Int.down*i);
            if (below.Kind != GobmineFluidKind.Water || below.Amount < GobmineFluidStore.Capacity) break;
            depth += size;
        }
        return depth;
    }
    public bool IsFluidCellOpen(Vector3Int cell)
    {
        if (_terrain == null || !ExpeditionEnabled) return false;
        bool open; if (_fluidOpenCache.TryGetValue(cell, out open)) return open;
        int n = Mathf.RoundToInt(ExpeditionProfile.fluidCellMetres/voxelSize);
        Vector3Int start = cell*n; open = true;
        // Check EVERY fine lattice sample inside the coarse cell, not merely its
        // corners. A thin rock wall inside a fluid cell must remain a barrier.
        for (int x=0;x<=n && open;x++) for (int y=0;y<=n && open;y++) for (int z=0;z<=n;z++)
            if (_terrain.Get(start+new Vector3Int(x,y,z)) < 0f) { open=false; break; }
        if (_fluidOpenCache.Count >= 32768) _fluidOpenCache.Clear();
        _fluidOpenCache[cell] = open; return open;
    }
    private void NotifyFluidTerrainChange(Vector3Int sample)
    {
        if (_fluids == null || !ExpeditionEnabled) return;
        int n = Mathf.RoundToInt(ExpeditionProfile.fluidCellMetres/voxelSize);
        Vector3Int owner = new Vector3Int(TerrainDensityStore.FloorDiv(sample.x,n), TerrainDensityStore.FloorDiv(sample.y,n), TerrainDensityStore.FloorDiv(sample.z,n));
        for (int x=0;x<(sample.x%n==0?2:1);x++) for (int y=0;y<(sample.y%n==0?2:1);y++) for (int z=0;z<(sample.z%n==0?2:1);z++)
        {
            Vector3Int c=owner-new Vector3Int(x,y,z); _fluidOpenCache.Remove(c);
            if (isServer) _fluids.WakeAround(c);
        }
    }
    private bool FluidActive(Vector3Int cell)
    {
        Vector3 p = ((Vector3)cell+Vector3.one*0.5f)*ExpeditionProfile.fluidCellMetres;
        float r=ExpeditionProfile.fluidActiveRadiusMetres;
        return NearestPlayerDistance(p) <= r*r;
    }
    private void FluidChanged(Vector3Int cell, GobmineFluidCell value)
    {
        if (isServer) _fluidOutgoing[cell]=value;
        if (_fluidRenderer != null) _fluidRenderer.MarkCellDirty(cell);
    }
    private void UpdateFluids()
    {
        if (_fluids == null) return;
        if (NetworkClient.active && NetworkClient.localPlayer != null && _fluidRenderer == null)
        {
            var go = new GameObject("Expedition fluids (visual only)"); go.transform.SetParent(transform, false);
            _fluidRenderer = go.AddComponent<GobmineFluidRenderer>(); _fluidRenderer.Initialize(this);
        }
        if (!isServer) return;
        using (FluidMarker.Auto())
        {
            _fluidWatch.Restart();
            if (Time.unscaledTime >= _nextFluidRefresh)
            {
                RefreshFluidDiscovery(); _nextFluidRefresh = Time.unscaledTime+0.5f;
            }
            int scans = Mathf.Max(1, ExpeditionProfile.fluidDiscoveryCellsPerFrame);
            while (scans-- > 0 && PendingFluidDiscovery > 0)
            {
                if (_fluidWatch.Elapsed.TotalMilliseconds >= ExpeditionProfile.fluidMillisecondsPerFrame) break;
                int player=_fluidDiscoveryCursor%_fluidScanCentres.Count;
                int offset=_fluidDiscoveryCursor/_fluidScanCentres.Count;
                _fluidDiscoveryCursor++;
                Vector3Int c=_fluidScanCentres[player]+_fluidScanOffsets[offset];
                if (FluidActive(c) && _fluids.Get(c).Amount>0) _fluids.Wake(c);
            }
            int operations = Mathf.Max(1, ExpeditionProfile.fluidOperationsPerFrame);
            while (operations-- > 0 && _fluids.Pending > 0)
            {
                if (operations < ExpeditionProfile.fluidOperationsPerFrame-1 && _fluidWatch.Elapsed.TotalMilliseconds >= ExpeditionProfile.fluidMillisecondsPerFrame) break;
                _fluids.Step(FluidActive);
            }
            LastFluidMilliseconds=(float)_fluidWatch.Elapsed.TotalMilliseconds;
        }
        if (Time.unscaledTime >= _nextFluidSend)
        { FlushFluidDeltas(); _nextFluidSend=Time.unscaledTime+0.1f; }
    }
    private void RefreshFluidDiscovery()
    {
        float size=ExpeditionProfile.fluidCellMetres;
        bool changed=_fluidPlayerRegions.Count!=_connectedPlayers.Count;
        if (!changed)
            for (int i=0;i<_connectedPlayers.Count;i++)
                if (_connectedPlayers[i]==null || _fluidPlayerRegions[i]!=Vector3Int.FloorToInt(_connectedPlayers[i].position/(size*4f))) { changed=true;break; }
        if (!changed && (PendingFluidDiscovery>0 || Time.unscaledTime<_nextDiscoveryCycle)) return;
        _fluidPlayerRegions.Clear();_fluidScanCentres.Clear();_fluidDiscoveryCursor=0;
        _nextDiscoveryCycle=Time.unscaledTime+4f;
        foreach(Transform player in _connectedPlayers)
        {
            if(player==null){_fluidPlayerRegions.Add(Vector3Int.zero);continue;}
            _fluidPlayerRegions.Add(Vector3Int.FloorToInt(player.position/(size*4f)));
            _fluidScanCentres.Add(Vector3Int.FloorToInt(player.position/size));
        }
        // No per-player cubic scan/sort here. The precomputed near-first offsets
        // are consumed incrementally inside the measured discovery allowance.
    }
    private void FlushFluidDeltas()
    {
        if (_fluidOutgoing.Count==0) return;
        // Bound each send and total work. Newer absolute values replace pending
        // values in the dictionary; no unbounded stream of obsolete updates.
        int batches=0;
        while (_fluidOutgoing.Count>0 && batches++<4)
        {
            var points=new List<GobmineFluidPoint>(128);
            foreach (var e in _fluidOutgoing)
            {
                points.Add(new GobmineFluidPoint{Cell=e.Key,Kind=e.Value.Kind,Amount=e.Value.Amount});
                if (points.Count==128) break;
            }
            foreach(var p in points) _fluidOutgoing.Remove(p.Cell);
            RpcFluidDelta(++_fluidRevision,points.ToArray());
        }
    }
    [ClientRpc]
    private void RpcFluidDelta(long revision,GobmineFluidPoint[] points)
    {
        if (isServer) return;
        var delta=new FluidDelta{Revision=revision,Points=points};
        if (!IsReady || _receivingSnapshot || _snapshotRequested || _fluids==null) { _bufferedFluidDeltas.Add(delta); return; }
        if (revision<=_lastFluidClientRevision) return;
        if (revision!=_lastFluidClientRevision+1) { _bufferedFluidDeltas.Add(delta); RequestSnapshot(); return; }
        ApplyFluidDelta(delta);
    }
    private void ApplyFluidDelta(FluidDelta delta)
    {
        if (_fluids!=null)
            foreach(var p in delta.Points)
            { _fluids.Apply(p.Cell,new GobmineFluidCell(p.Kind,p.Amount)); if(_fluidRenderer!=null)_fluidRenderer.MarkCellDirty(p.Cell); }
        _lastFluidClientRevision=delta.Revision;
    }
    [TargetRpc]
    private void TargetFluidSnapshotPart(NetworkConnectionToClient target,GobmineFluidPoint[] points)
    { if(!isServer && _fluids!=null)_fluids.Import(points); }
    private bool FinishFluidSnapshot()
    {
        _bufferedFluidDeltas.Sort((a,b)=>a.Revision.CompareTo(b.Revision));
        foreach(var d in _bufferedFluidDeltas)
        {
            if(d.Revision<=_lastFluidClientRevision)continue;
            if(d.Revision!=_lastFluidClientRevision+1) { _bufferedFluidDeltas.Clear();return false; }
            ApplyFluidDelta(d);
        }
        _bufferedFluidDeltas.Clear();return true;
    }
}
