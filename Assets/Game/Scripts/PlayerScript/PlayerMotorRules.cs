using System;
using UnityEngine;

/// <summary>Movement maths and input timing. A helper, NOT a GameObject component.</summary>
public static class PlayerMotorRules
{
    public static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    public static bool Finite(Vector3 value) => Finite(value.x) && Finite(value.y) && Finite(value.z);

    public static float JumpSpeed(float height, float gravity)
    {
        if (!Finite(height) || !Finite(gravity) || height < 0f || gravity >= 0f)
            throw new ArgumentOutOfRangeException("Jump height must be nonnegative and gravity must be negative.");
        return Mathf.Sqrt(-2f * gravity * height);
    }

    public static Vector3 HorizontalMotion(Vector2 input, Vector3 right, Vector3 forward, float speed)
    {
        input = Vector2.ClampMagnitude(input, 1f); // Diagonal input must not move faster.
        Vector3 direction = right * input.x + forward * input.y;
        direction.y = 0f;
        return Vector3.ClampMagnitude(direction, 1f) * speed;
    }

    /// <summary>Bounds include the WHOLE body, not just the feet or the destination point.</summary>
    public static Bounds SweptBounds(Vector3 centre, float radius, float height, Vector3 displacement,
        float stepHeight, float groundProbeDistance, float padding)
    {
        float r = Mathf.Max(0.01f, radius) + Mathf.Max(0f, padding);
        float halfHeight = Mathf.Max(height * 0.5f, radius) + Mathf.Max(0f, padding);
        Vector3 extent = new Vector3(r, halfHeight, r);
        Vector3 low = Vector3.Min(centre, centre + displacement) - extent;
        Vector3 high = Vector3.Max(centre, centre + displacement) + extent;
        low.y -= Mathf.Max(0f, groundProbeDistance);
        high.y += Mathf.Max(0f, stepHeight);
        var result = new Bounds(); result.SetMinMax(low, high); return result;
    }

    public static bool ChunkRange(Bounds bounds, Vector3 chunkMetres, int maximumChunks,
        out Vector3Int low, out Vector3Int high)
    {
        low = high = Vector3Int.zero;
        if (!Finite(bounds.min) || !Finite(bounds.max) || !Finite(chunkMetres) ||
            chunkMetres.x <= 0f || chunkMetres.y <= 0f || chunkMetres.z <= 0f || maximumChunks < 1)
            return false;
        // Keep float-to-int conversion and inclusive iteration safely within int range.
        Vector3 a = bounds.min, b = bounds.max;
        if (Mathf.Max(Mathf.Abs(a.x / chunkMetres.x), Mathf.Abs(b.x / chunkMetres.x)) > 10000000f ||
            Mathf.Max(Mathf.Abs(a.y / chunkMetres.y), Mathf.Abs(b.y / chunkMetres.y)) > 10000000f ||
            Mathf.Max(Mathf.Abs(a.z / chunkMetres.z), Mathf.Abs(b.z / chunkMetres.z)) > 10000000f)
            return false;
        low = new Vector3Int(Mathf.FloorToInt(a.x / chunkMetres.x), Mathf.FloorToInt(a.y / chunkMetres.y), Mathf.FloorToInt(a.z / chunkMetres.z));
        high = new Vector3Int(Mathf.FloorToInt(b.x / chunkMetres.x), Mathf.FloorToInt(b.y / chunkMetres.y), Mathf.FloorToInt(b.z / chunkMetres.z));
        long x = (long)high.x - low.x + 1, y = (long)high.y - low.y + 1, z = (long)high.z - low.z + 1;
        return x > 0 && y > 0 && z > 0 && x <= maximumChunks && y <= maximumChunks && z <= maximumChunks &&
            x * y * z <= maximumChunks;
    }
}

/// <summary>A press can be consumed once, including during the short coyote-time window.</summary>
public sealed class PlayerJumpBuffer
{
    private float _pressedAt = float.NegativeInfinity;
    private float _groundedAt = float.NegativeInfinity;
    private bool _jumpAvailable;
    public void Press(float now) { if (PlayerMotorRules.Finite(now)) _pressedAt = now; }
    public void Grounded(float now)
    {
        if (!PlayerMotorRules.Finite(now)) return;
        _groundedAt = now; _jumpAvailable = true;
    }
    public bool CanConsume(float now, float bufferSeconds, float coyoteSeconds)
    {
        return PlayerMotorRules.Finite(now) && _jumpAvailable && now >= _pressedAt && now >= _groundedAt &&
            now - _pressedAt <= Mathf.Max(0f, bufferSeconds) && now - _groundedAt <= Mathf.Max(0f, coyoteSeconds);
    }
    public void Consume() { _pressedAt = float.NegativeInfinity; _jumpAvailable = false; }
    public void Reset() { _pressedAt = _groundedAt = float.NegativeInfinity; _jumpAvailable = false; }
}
