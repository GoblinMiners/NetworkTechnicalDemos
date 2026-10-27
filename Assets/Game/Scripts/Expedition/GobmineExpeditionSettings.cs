using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

[CreateAssetMenu(menuName = "MiningGame/Expedition Settings", fileName = "ExpeditionSettings")]
public sealed class GobmineExpeditionSettings : ScriptableObject
{
    public const int GenerationVersion = 1;
    [Header("World generation - new save required when changed")]
    public float surfaceReferenceY = 0f;
    [Min(16)] public int cavernGridMetres = 24;
    [Min(8f)] public float firstCavernDepth = 20f;
    [UnityEngine.Range(0, 1000)] public int cavernChancePerThousand = 650;
    [UnityEngine.Range(0.1f, 0.32f)] public float horizontalRadiusFraction = 0.30f;
    [UnityEngine.Range(0.1f, 0.32f)] public float verticalRadiusFraction = 0.28f;
    [Min(0f)] public float lavaStartsAtDepth = 220f;
    [UnityEngine.Range(0, 1000)] public int waterPocketChancePerThousand = 450;
    [UnityEngine.Range(0, 1000)] public int lavaPocketChancePerThousand = 500;
    [UnityEngine.Range(0.1f, 0.7f)] public float initialFluidFillFraction = 0.30f;
    [Tooltip("Must be an integer multiple of voxelSize, and divide cavernGridMetres. Fluids cannot enter narrower openings.")]
    [Min(0.25f)] public float fluidCellMetres = 1f;
    [Header("Work budgets - may be tuned without replacing the world")]
    [UnityEngine.Range(1, 256)] public int fluidOperationsPerFrame = 24;
    [Min(0.1f)] public float fluidMillisecondsPerFrame = 1.5f;
    [UnityEngine.Range(8, 512)] public int fluidDiscoveryCellsPerFrame = 64;
    [UnityEngine.Range(4, 24)] public int fluidActiveRadiusMetres = 12;
    [UnityEngine.Range(1, 16)] public int fluidRegionsPerFrame = 1;
    [UnityEngine.Range(16, 512)] public int maxRenderedFluidRegions = 160;
    [Header("Fluid appearance - client only")]
    public Material waterMaterial;
    public Material lavaMaterial;
    [Header("Damage / exposure")]
    [Min(0f)] public float safeFallMetres = 3f;
    [Min(1f)] public float lethalFallMetres = 12f;
    [Min(0.25f)] public float waterCushionDepthMetres = 2.5f;
    [Min(1f)] public float oxygenSeconds = 30f;
    [Min(1f)] public float tankOxygenSeconds = 90f;
    [Min(0f)] public float drowningDamagePerSecond = 12f;
    [Min(0f)] public float lavaDamagePerSecond = 60f;
    [Min(0f)] public float radiationDamageAtFullDose = 12f;
    [Min(0f)] public float doseRecoveryPerSecond = 2f;
    [Min(1f)] public float radiationRadiusMetres = 6f;
    [UnityEngine.Range(0f, 1f)] public float radiationSuitReduction = 0.90f;
    [UnityEngine.Range(0f, 1f)] public float heatSuitReduction = 0.70f;
    [UnityEngine.Range(0f, 1f)] public float impactBootReduction = 0.50f;
    [Header("Shared expedition research")]
    public Vector3 workshopPosition = new Vector3(0f, -2f, 0f);
    [Min(1f)] public float workshopReach = 8f;
    public ResearchRecipe[] recipes = new ResearchRecipe[0];
    [Serializable] public struct Cost { public int itemId; [Min(1)] public int amount; }
    [Serializable] public sealed class ResearchRecipe
    {
        public GobmineResearch unlock;
        public GobmineResearch prerequisite;
        public string label;
        public Cost[] costs = new Cost[0];
    }
    public void ValidateOrThrow(float voxelSize)
    {
        if (!GobmineExpeditionRules.Finite(voxelSize) || voxelSize <= 0f || cavernGridMetres < 16 || cavernGridMetres > 128 ||
            !GobmineExpeditionRules.Finite(surfaceReferenceY) || !GobmineExpeditionRules.Finite(fluidCellMetres) ||
            fluidCellMetres < voxelSize || fluidCellMetres > 2f || fluidCellMetres/voxelSize>8.01f || fluidActiveRadiusMetres/fluidCellMetres>24f || radiationRadiusMetres>16f ||
            Mathf.Abs(fluidCellMetres / voxelSize - Mathf.Round(fluidCellMetres / voxelSize)) > 0.0001f ||
            Mathf.Abs(cavernGridMetres / fluidCellMetres - Mathf.Round(cavernGridMetres / fluidCellMetres)) > 0.0001f ||
            Mathf.Abs(cavernGridMetres / voxelSize - Mathf.Round(cavernGridMetres / voxelSize)) > 0.0001f ||
            horizontalRadiusFraction < 0.1f || horizontalRadiusFraction > 0.32f || verticalRadiusFraction < 0.1f || verticalRadiusFraction > 0.32f ||
            firstCavernDepth < 8f || initialFluidFillFraction < 0.1f || initialFluidFillFraction > 0.7f ||
            cavernChancePerThousand < 0 || cavernChancePerThousand > 1000 || waterPocketChancePerThousand < 0 || waterPocketChancePerThousand > 1000 ||
            lavaPocketChancePerThousand < 0 || lavaPocketChancePerThousand > 1000 || lethalFallMetres <= safeFallMetres)
            throw new InvalidOperationException("Invalid expedition profile. Check cave margins, depth, fluid/voxel alignment and fall thresholds.");
        float[] finite = { firstCavernDepth, lavaStartsAtDepth, initialFluidFillFraction, horizontalRadiusFraction, verticalRadiusFraction,
            fluidMillisecondsPerFrame, safeFallMetres, lethalFallMetres, waterCushionDepthMetres, oxygenSeconds, tankOxygenSeconds,
            drowningDamagePerSecond, lavaDamagePerSecond, radiationDamageAtFullDose, doseRecoveryPerSecond, radiationRadiusMetres,
            radiationSuitReduction, heatSuitReduction, impactBootReduction, workshopPosition.x, workshopPosition.y, workshopPosition.z, workshopReach };
        foreach (float value in finite) if (!GobmineExpeditionRules.Finite(value)) throw new InvalidOperationException("Expedition values must be finite.");
        if (recipes == null || fluidOperationsPerFrame < 1 || fluidOperationsPerFrame > 256 || fluidMillisecondsPerFrame <= 0f ||
            fluidDiscoveryCellsPerFrame < 1 || fluidDiscoveryCellsPerFrame > 512 || fluidRegionsPerFrame < 1 || fluidRegionsPerFrame > 16 ||
            maxRenderedFluidRegions < 16 || maxRenderedFluidRegions > 512 || fluidActiveRadiusMetres < 4 || fluidActiveRadiusMetres > 24 ||
            safeFallMetres < 0f || lavaStartsAtDepth < firstCavernDepth || oxygenSeconds < 1f || tankOxygenSeconds < oxygenSeconds ||
            waterCushionDepthMetres < 0.25f || drowningDamagePerSecond < 0f || lavaDamagePerSecond < 0f || radiationDamageAtFullDose < 0f || doseRecoveryPerSecond < 0f ||
            radiationRadiusMetres < 1f || radiationSuitReduction < 0f || radiationSuitReduction > 1f || heatSuitReduction < 0f || heatSuitReduction > 1f ||
            impactBootReduction < 0f || impactBootReduction > 1f || workshopReach < 1f)
            throw new InvalidOperationException("Invalid expedition budgets, damage, protection or equipment settings.");
        int seen = 0;
        foreach (var r in recipes)
        {
            int bit = r == null ? 0 : (int)r.unlock;
            if (bit <= 0 || (bit & (bit - 1)) != 0 || (bit & ~511) != 0 || (seen & bit) != 0 || r.costs == null)
                throw new InvalidOperationException("Research unlocks must be unique defined single-bit values.");
            if (((int)r.prerequisite & ~511) != 0 || ((int)r.prerequisite & bit) != 0)
                throw new InvalidOperationException("Invalid/self-referencing research prerequisite.");
            seen |= bit;
            foreach (var c in r.costs) if (c.itemId <= 0 || c.amount <= 0) throw new InvalidOperationException("Invalid research cost.");
        }
    }
    public string GenerationSignature()
    {
        var c = CultureInfo.InvariantCulture; var s = new StringBuilder("GobmineExpedition:" + GenerationVersion);
        s.Append('|').Append(surfaceReferenceY.ToString("R", c)).Append('|').Append(cavernGridMetres).Append('|').Append(firstCavernDepth.ToString("R", c))
         .Append('|').Append(cavernChancePerThousand).Append('|').Append(horizontalRadiusFraction.ToString("R", c)).Append('|').Append(verticalRadiusFraction.ToString("R", c))
         .Append('|').Append(lavaStartsAtDepth.ToString("R", c)).Append('|').Append(waterPocketChancePerThousand).Append('|').Append(lavaPocketChancePerThousand)
         .Append('|').Append(initialFluidFillFraction.ToString("R", c)).Append('|').Append(fluidCellMetres.ToString("R", c));
        return Hash(s.ToString());
    }
    public static string Hash(string input)
    { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(input))).Replace("-", ""); }
}
