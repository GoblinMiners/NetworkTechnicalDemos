using System;
using UnityEngine;

public enum GobmineDamageType { Collapse, Fall, Heat, Radiation, Drowning }
public enum GobmineSpaceKind { Unavailable, Solid, Air, Water, Lava }
public enum GobmineFluidKind : byte { None = 0, Water = 1, Lava = 2 }
[Flags]
public enum GobmineResearch
{
    None = 0, Tool1 = 1, Tool2 = 2, Tool3 = 4, Tool4 = 8,
    RadiationSuit = 16, ShieldedContainer = 32, HeatSuit = 64, AirTank = 128, ImpactBoots = 256
}
public struct GobmineFluidCell
{
    public GobmineFluidKind Kind;
    public int Amount;
    public GobmineFluidCell(GobmineFluidKind kind, int amount)
    { Amount = Mathf.Clamp(amount, 0, GobmineFluidStore.Capacity); Kind = Amount == 0 ? GobmineFluidKind.None : kind; }
}
public struct GobmineFluidPoint
{
    public Vector3Int Cell;
    public GobmineFluidKind Kind;
    public int Amount;
}
public static class GobmineExpeditionRules
{
    public static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    public static float Depth(float referenceY, float y) => Mathf.Max(0f, referenceY - y);
    public static int ToolTier(int flags)
    {
        int tier = 0;
        for (int i = 0; i < 4; i++) { if ((flags & (1 << i)) == 0) break; tier++; }
        return tier;
    }
    public static bool Has(int flags, GobmineResearch bit) => (flags & (int)bit) == (int)bit;
    public static float FallDamage(float distance, float gravity, float terminalSpeed, float safeDistance, float lethalDistance, float maxHealth)
    {
        if (!Finite(distance) || !Finite(gravity) || !Finite(terminalSpeed) || distance <= safeDistance) return 0f;
        // Energy-equivalent drop, bounded by the motor's terminal speed. Duration is
        // tracked independently; waiting for terrain is not accumulated fall distance.
        float g = Mathf.Max(0.1f, Mathf.Abs(gravity));
        float effective = Mathf.Min(distance, terminalSpeed * terminalSpeed / (2f * g));
        return Mathf.Clamp01((effective - safeDistance) / Mathf.Max(0.1f, lethalDistance - safeDistance)) * maxHealth;
    }
    public static float WaterImpactMultiplier(float depth, float fullCushionDepth)
        => 1f - Mathf.Clamp01(depth / Mathf.Max(0.25f, fullCushionDepth));
}

// Server-side tracker. A collision-streaming pause leaves the current fall intact,
// but contributes neither simulated time nor distance. Respawns explicitly Reset.
public sealed class GobmineFallTracker
{
    private bool _initialized, _airborne;
    private float _peak;
    public float AirborneSeconds { get; private set; }
    public float LastDrop { get; private set; }
    public void Reset(float y) { _initialized = true; _airborne = false; _peak = y; AirborneSeconds = 0f; LastDrop = 0f; }
    public bool Sample(float y, bool grounded, bool suspended, float dt, out float drop)
    {
        drop = 0f;
        if (!GobmineExpeditionRules.Finite(y)) return false;
        if (!_initialized) Reset(y);
        if (suspended) return false;
        if (!grounded)
        {
            if (!_airborne) { _airborne = true; AirborneSeconds = 0f; }
            _peak = Mathf.Max(_peak, y); AirborneSeconds += Mathf.Max(0f, dt); return false;
        }
        bool landed = _airborne;
        if (landed) { drop = Mathf.Max(0f, _peak - y); LastDrop = drop; }
        _peak = y; _airborne = false; AirborneSeconds = 0f;
        return landed;
    }
}
