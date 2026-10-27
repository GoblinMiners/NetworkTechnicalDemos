using UnityEngine;

/// <summary>Server-side, monotonic mass and impact-speed gameplay scaling.</summary>
public static class TerrainShapeImpactRules
{
    public static float Damage(float volume, float speed, float threshold, float rate, float maximum)
    {
        if (!Valid(volume) || !Valid(speed) || !Valid(rate) || !Valid(maximum)) return 0f;
        return Mathf.Clamp(Mathf.Sqrt(Mathf.Max(0f,volume)) *
            Mathf.Max(0f,speed-Mathf.Max(0f,threshold)) * Mathf.Max(0f,rate),0f,Mathf.Max(0f,maximum));
    }
    public static float Knockback(float volume, float speed, float threshold, float rate, float maximum)
    {
        if (!Valid(volume) || !Valid(speed) || !Valid(rate) || !Valid(maximum)) return 0f;
        return Mathf.Clamp(Mathf.Sqrt(Mathf.Max(0f,volume)) *
            Mathf.Max(0f,speed-Mathf.Max(0f,threshold)) * Mathf.Max(0f,rate),0f,Mathf.Max(0f,maximum));
    }
    private static bool Valid(float v) => !float.IsNaN(v) && !float.IsInfinity(v);
}
