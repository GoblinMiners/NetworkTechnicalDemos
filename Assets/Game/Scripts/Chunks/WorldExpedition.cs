using System;
using System.Collections.Generic;
using Mirror;
using UnityEngine;

// Another part of World, NOT a component to attach separately.
public partial class World
{
    [Header("Expedition - optional; use a NEW save slot")]
    public GobmineExpeditionSettings expeditionSettings;
    private GobmineExpeditionSettings _runtimeExpeditionSettings;
    public GobmineExpeditionSettings ExpeditionProfile => _runtimeExpeditionSettings != null ? _runtimeExpeditionSettings : expeditionSettings;
    [SyncVar] private int expeditionUnlocks;
    public bool ExpeditionEnabled => ExpeditionProfile != null;
    public int ExpeditionUnlocks => expeditionUnlocks;
    public float DepthAt(Vector3 position) => ExpeditionEnabled ? GobmineExpeditionRules.Depth(ExpeditionProfile.surfaceReferenceY, position.y) : Mathf.Max(0f, -position.y);
    private GobmineCavernGenerator _caverns;
    private struct RadiationSource { public Vector3 Position; public float Rate; }
    private readonly Dictionary<Vector3Int, List<RadiationSource>> _radiationSurfaces = new Dictionary<Vector3Int, List<RadiationSource>>();

    private string ComputeWorldRulesSignature()
    {
        if (!ExpeditionEnabled) return materialCatalog.RulesSignature();
        expeditionSettings.ValidateOrThrow(voxelSize);
        return GobmineExpeditionSettings.Hash(materialCatalog.RulesSignature() + "|" + expeditionSettings.GenerationSignature());
    }
    private void InitializeExpeditionGenerator()
    {
        if (_runtimeExpeditionSettings != null) Destroy(_runtimeExpeditionSettings);
        _runtimeExpeditionSettings = expeditionSettings != null ? Instantiate(expeditionSettings) : null;
        if (_runtimeExpeditionSettings != null) _runtimeExpeditionSettings.hideFlags = HideFlags.DontSave;
        _caverns = ExpeditionEnabled ? new GobmineCavernGenerator(seed, voxelSize, ExpeditionProfile) : null;
        if (ExpeditionEnabled && ceilingCollapseThicknessMetres >= ExpeditionProfile.cavernGridMetres * 0.18f)
            throw new InvalidOperationException("Ceiling threshold is too large for the generated cave support margins. Use a smaller ceiling threshold or larger cave grid.");
    }
    private void ResetExpeditionRuntime()
    {
        ResetFluidRuntime(); _caverns = null; _radiationSurfaces.Clear();
        expeditionUnlocks = 0;
        if (_runtimeExpeditionSettings != null) Destroy(_runtimeExpeditionSettings);
        _runtimeExpeditionSettings = null;
    }
    private void UpdateExpeditionRuntime()
    {
        if (ExpeditionEnabled && IsReady) UpdateFluids();
    }
    private bool CanMineExpeditionSample(Vector3Int sample, PlayerInventory miner, out string reason)
    {
        reason = null;
        if (!ExpeditionEnabled || miner == null) return true; // Trusted server edits only.
        var health = miner.GetComponent<GobminePlayerHealth>();
        if (health != null && health.IsDead) { reason = "You are downed."; return false; }
        // Changing one corner changes up to eight material cells. Check ALL of them
        // so aiming at neighbouring stone cannot bypass an ore's equipment gate.
        for (int x=0;x<=1;x++) for (int y=0;y<=1;y++) for (int z=0;z<=1;z++)
        {
            Vector3Int cell = sample - new Vector3Int(x,y,z);
            // A material ID can exist procedurally inside a natural cavity even
            // though that cell contains no rock. Empty cells must not gate tools.
            if (TerrainQuery.CellState(cell, _densityReader) == TerrainCellState.Air) continue;
            var d = materialCatalog.Get(GetSolidMaterialId(cell));
            if (d == null) continue;
            if (GobmineExpeditionRules.ToolTier(expeditionUnlocks) < d.requiredToolTier)
            { reason = d.displayName + " needs tool tier " + d.requiredToolTier + "."; return false; }
            if (d.requireRadiationSuit && !GobmineExpeditionRules.Has(expeditionUnlocks, GobmineResearch.RadiationSuit))
            { reason = "Radiation protection required for " + d.displayName + "."; return false; }
            if (d.requireShieldedContainer && !GobmineExpeditionRules.Has(expeditionUnlocks, GobmineResearch.ShieldedContainer))
            { reason = "A shielded container is required for " + d.displayName + "."; return false; }
        }
        return true;
    }
    public bool CanCollectExpeditionMaterial(PlayerInventory inventory, int materialId, out string reason)
    {
        reason = null;
        if (!ExpeditionEnabled) return true;
        var d = materialCatalog.Get(materialId);
        if (d == null) { reason = "Unknown material."; return false; }
        if (GobmineExpeditionRules.ToolTier(expeditionUnlocks) < d.requiredToolTier)
        { reason = "Upgrade your tool before recovering " + d.displayName + "."; return false; }
        if (d.requireShieldedContainer && !GobmineExpeditionRules.Has(expeditionUnlocks, GobmineResearch.ShieldedContainer))
        { reason = "Uncollected radioactive ore: shielded container required."; return false; }
        if (d.requireRadiationSuit && !GobmineExpeditionRules.Has(expeditionUnlocks, GobmineResearch.RadiationSuit))
        { reason = "Radiation protection required for collection."; return false; }
        return inventory != null;
    }
    private void NotifyEquipmentMessage(PlayerInventory inventory, string message)
    {
        if (inventory == null || string.IsNullOrEmpty(message)) return;
        var gear = inventory.GetComponent<GobminePlayerEquipment>();
        if (gear != null) gear.ServerMessage(message);
    }
    private void NotifyMinedExposure(PlayerInventory miner, int material, double units)
    {
        if (!ExpeditionEnabled || miner == null) return;
        var d = materialCatalog.Get(material);
        var vitals = miner.GetComponent<GobminePlayerVitals>();
        if (d != null && vitals != null && d.radiationRate > 0f)
            vitals.ServerAddExposure((float)Math.Min(50d, units * d.radiationRate * 0.15d));
    }
    [Server]
    public bool TryPurchaseResearch(PlayerInventory payer, int recipeIndex, out string message)
    {
        message = "Expedition is not ready.";
        if (!isServer || !IsReady || !ExpeditionEnabled || payer == null || !payer.isServer) return false;
        var health = payer.GetComponent<GobminePlayerHealth>();
        if (health == null || health.IsDead) { message = "A living player is required."; return false; }
        if (Vector3.Distance(payer.transform.position, ExpeditionProfile.workshopPosition) > ExpeditionProfile.workshopReach)
        { message = "Return to the starting workshop to research equipment."; return false; }
        if (recipeIndex < 0 || recipeIndex >= ExpeditionProfile.recipes.Length) { message = "Unknown recipe."; return false; }
        var recipe = ExpeditionProfile.recipes[recipeIndex];
        if (GobmineExpeditionRules.Has(expeditionUnlocks, recipe.unlock)) { message = "Already researched."; return false; }
        if (!GobmineExpeditionRules.Has(expeditionUnlocks, recipe.prerequisite)) { message = "Research the prerequisite first."; return false; }
        if (!payer.ServerTrySpend(recipe.costs)) { message = "Not enough minerals."; return false; }
        expeditionUnlocks |= (int)recipe.unlock;
        message = recipe.label + " unlocked for this expedition (all players).";
        if (!SaveMaterialWorld()) message += " SAVE FAILED: inspect the server Console before stopping.";
        return true;
    }
    // Server-only helper for Editor testing, never a client Command.
    public void ServerGrantExpeditionResearchForTesting(int mask)
    { if (isServer && ExpeditionEnabled) expeditionUnlocks |= mask & 511; }

    public void RefreshRadiationSurface(Vector3Int origin, TerrainMeshBuilder.Result mesh)
    {
        if (!isServer || !ExpeditionEnabled || materialCatalog == null) return;
        Vector3Int coord = _terrain.Owner(origin);
        _radiationSurfaces.Remove(coord);
        var buckets = new Dictionary<Vector3Int, RadiationSource>();
        foreach (var group in mesh.Triangles)
        {
            var d = materialCatalog.Get(group.Key);
            if (d == null || d.radiationRate <= 0f) continue;
            for (int i = 0; i+2 < group.Value.Count; i += 3)
            {
                Vector3 p = (Vector3)origin * voxelSize + (mesh.Vertices[group.Value[i]] + mesh.Vertices[group.Value[i+1]] + mesh.Vertices[group.Value[i+2]]) / 3f;
                Vector3Int key = Vector3Int.FloorToInt(p); // One aura source per surface metre, not per triangle.
                RadiationSource previous;
                if (!buckets.TryGetValue(key, out previous) || previous.Rate < d.radiationRate)
                    buckets[key] = new RadiationSource { Position = p, Rate = d.radiationRate };
            }
        }
        if (buckets.Count > 0) _radiationSurfaces[coord] = new List<RadiationSource>(buckets.Values);
    }
    public float RadiationAt(Vector3 position, PlayerInventory carrying)
    {
        if (!isServer || !ExpeditionEnabled) return 0f;
        float radius = ExpeditionProfile.radiationRadiusMetres, total = 0f;
        Vector3Int lo = GetChunkCoordFromPosition(position - Vector3.one * radius), hi = GetChunkCoordFromPosition(position + Vector3.one * radius);
        for (int x=lo.x;x<=hi.x;x++) for (int y=lo.y;y<=hi.y;y++) for (int z=lo.z;z<=hi.z;z++)
        {
            Vector3Int c = new Vector3Int(x,y,z); List<RadiationSource> sources;
            if (!_activeChunks.ContainsKey(c) || !_radiationSurfaces.TryGetValue(c, out sources)) continue;
            foreach (var source in sources)
                total += source.Rate * Mathf.Clamp01(1f-Vector3.Distance(position, source.Position)/radius);
        }
        if (_resourcePiles != null)
            foreach (var pile in _resourcePiles.Nearby(position, radius))
            {
                var d = materialCatalog.Get(pile.MaterialId);
                if (d != null && d.radiationRate > 0f)
                    total += d.radiationRate * (float)Math.Min(4d, Math.Sqrt(pile.Units)) * Mathf.Clamp01(1f-Vector3.Distance(position,pile.Position)/radius);
            }
        if (carrying != null && !GobmineExpeditionRules.Has(expeditionUnlocks, GobmineResearch.ShieldedContainer))
            foreach (var item in carrying.Quantities)
            {
                var d = materialCatalog.Get(MaterialForItem(item.Key));
                if (d != null && d.radiationRate > 0f) total += d.radiationRate * (float)Math.Min(8d, Math.Sqrt(item.Value));
            }
        return Mathf.Min(100f, total);
    }
    public string ExpeditionStatus()
    {
        return !ExpeditionEnabled ? "[Gobmine Expedition] No profile assigned (legacy world)." :
            $"[Gobmine Expedition] seed={seed}; ready={IsReady}; server={isServer}; unlocks={expeditionUnlocks}; fluid overrides={(_fluids==null?0:_fluids.ModifiedCells)}; pending fluid work={(_fluids==null?0:_fluids.Pending)}; pending discovery={PendingFluidDiscovery}; latest fluid work={LastFluidMilliseconds:0.00} ms; save={materialSaveFileName}";
    }
}
