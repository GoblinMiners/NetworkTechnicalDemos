using UnityEngine;

/// <summary>Stable terrain identity, independent of whether a cell is partly excavated.</summary>
[CreateAssetMenu(fileName = "TerrainMaterial", menuName = "MiningGame/Terrain Material")]
public sealed class TerrainMaterialDefinition : ScriptableObject
{
    [Tooltip("Stable save/network ID. 0 is reserved for Air. Never renumber an existing world.")]
    [Min(1)] public int materialId = 1;
    public string displayName = "Stone";
    public Material surfaceMaterial;
    public Material debrisMaterial;
    [Min(0.05f)] public float hardness = 1f;
    public MineralData inventoryItem;
    [Tooltip("Items per cubic metre of removed terrain; fractions accumulate, never round per hit.")]
    [Min(0)] public double itemsPerCubicMetre = 8;

    [Header("Ore generation (ignored for the catalogue's base stone)")]
    public bool generateDeposits;
    [Min(4)] public int depositGridSizeInCells = 48;
    [UnityEngine.Range(0,1000)] public int depositChancePerThousand = 500;
    [Min(1)] public int minimumRadiusInCells = 3;
    [Min(1)] public int maximumRadiusInCells = 8;
    public int minimumCellY = -100000;
    public int maximumCellY = 100000;

    [Header("Expedition progression (optional)")]
    public bool useDepthBand;
    [Min(0f)] public float minimumDepthMetres;
    [Min(0f)] public float maximumDepthMetres = 100000f;
    [UnityEngine.Range(0,4)] public int requiredToolTier;
    [Min(0f)] public float radiationRate;
    public bool requireRadiationSuit;
    public bool requireShieldedContainer;
}
