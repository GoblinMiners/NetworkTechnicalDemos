using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

[CreateAssetMenu(fileName = "TerrainMaterialCatalog", menuName = "MiningGame/Terrain Material Catalogue")]
public sealed class TerrainMaterialCatalog : ScriptableObject
{
    public const int Air = 0;
    public const int GeneratorVersion = 1;
    public int baseStoneId = 1;
    [Tooltip("Later entries take priority when deposits overlap. Put rare ores last.")]
    public TerrainMaterialDefinition[] definitions = new TerrainMaterialDefinition[0];
    private Dictionary<int, TerrainMaterialDefinition> _byId;
    private Dictionary<int, MineralData> _items;

    private void OnEnable() { _byId = null; _items = null; }
    private void OnValidate() { _byId = null; _items = null; }

    public void ValidateOrThrow()
    {
        _byId = new Dictionary<int, TerrainMaterialDefinition>();
        _items = new Dictionary<int, MineralData>();
        if (definitions == null || definitions.Length == 0) throw new InvalidOperationException("Material catalogue is empty.");
        foreach (TerrainMaterialDefinition d in definitions)
        {
            if (d == null || d.materialId <= 0 || d.materialId > ushort.MaxValue || _byId.ContainsKey(d.materialId))
                throw new InvalidOperationException("Material IDs must be unique, nonzero ushort values.");
            if (d.surfaceMaterial == null) throw new InvalidOperationException(d.name + " has no surface material.");
            if (float.IsNaN(d.hardness) || float.IsInfinity(d.hardness) || d.hardness < 0.05f ||
                double.IsNaN(d.itemsPerCubicMetre) || double.IsInfinity(d.itemsPerCubicMetre) || d.itemsPerCubicMetre < 0)
                throw new InvalidOperationException(d.name + " has invalid hardness or yield.");
            if (d.itemsPerCubicMetre > 0 && (d.inventoryItem == null || d.inventoryItem.mineralID <= 0))
                throw new InvalidOperationException(d.name + " needs an inventory item with a positive ID.");
            if (d.inventoryItem != null)
            {
                MineralData existing;
                if (_items.TryGetValue(d.inventoryItem.mineralID, out existing) && existing != d.inventoryItem)
                    throw new InvalidOperationException("Different inventory assets share an item ID.");
                _items[d.inventoryItem.mineralID] = d.inventoryItem;
            }
            if (d.generateDeposits && (d.depositGridSizeInCells < 4 || d.depositGridSizeInCells > 1024 || d.minimumRadiusInCells < 1 ||
                d.maximumRadiusInCells < d.minimumRadiusInCells || d.maximumRadiusInCells > d.depositGridSizeInCells / 2 ||
                d.minimumCellY > d.maximumCellY || d.depositChancePerThousand < 0 || d.depositChancePerThousand > 1000))
                throw new InvalidOperationException(d.name + " has invalid deposit settings (radius must be <= half grid spacing).");
            _byId.Add(d.materialId, d);
        }
        if (!_byId.ContainsKey(baseStoneId)) throw new InvalidOperationException("Base stone ID is missing.");
    }

    private void EnsureIndex() { if (_byId == null) ValidateOrThrow(); }
    public TerrainMaterialDefinition Get(int id)
    {
        EnsureIndex(); TerrainMaterialDefinition d;
        return _byId.TryGetValue(id, out d) ? d : null;
    }
    public MineralData GetItem(int id)
    {
        EnsureIndex(); MineralData d;
        return _items.TryGetValue(id, out d) ? d : null;
    }
    public Material Surface(int id) { var d = Get(id); return d == null ? null : d.surfaceMaterial; }
    public Material Debris(int id) { var d = Get(id); return d == null ? null : (d.debrisMaterial != null ? d.debrisMaterial : d.surfaceMaterial); }

    /// <summary>Locks geometry/reward rules for a saved world and for joining clients; art can change.</summary>
    public string RulesSignature()
    {
        EnsureIndex();
        var s = new StringBuilder();
        s.Append(GeneratorVersion).Append('|').Append(baseStoneId);
        foreach (var d in definitions)
        {
            s.Append('|').Append(d.materialId).Append(':').Append(d.inventoryItem == null ? 0 : d.inventoryItem.mineralID)
             .Append(':').Append(d.hardness.ToString("R", CultureInfo.InvariantCulture))
             .Append(':').Append(d.itemsPerCubicMetre.ToString("R", CultureInfo.InvariantCulture))
             .Append(':').Append(d.generateDeposits ? 1 : 0).Append(':').Append(d.depositGridSizeInCells)
             .Append(':').Append(d.depositChancePerThousand).Append(':').Append(d.minimumRadiusInCells)
             .Append(':').Append(d.maximumRadiusInCells).Append(':').Append(d.minimumCellY).Append(':').Append(d.maximumCellY);
        }
        using (var sha = SHA256.Create())
            return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(s.ToString()))).Replace("-", "");
    }
}
