using System;
using UnityEngine;

public enum ResourcePickupAccess
{
    Reachable, Invalid, TooFar, PlayerDown, TerrainUpdating, BuriedInTerrain, SolidObstacle
}

/// <summary>
/// Collection rules only. This helper must NOT be attached to a GameObject.
/// Cosmetic debris never becomes a resource-collection barrier, regardless of layer.
/// Ordinary solid colliders still block collection. No collision settings are changed.
/// </summary>
public static class ResourcePickupRules
{
    public const float CollectionIntervalSeconds = 0.15f;
    private const int MaximumRayHits = 4096;

    public static bool IsFinite(Vector3 p) =>
        IsFinite(p.x) && IsFinite(p.y) && IsFinite(p.z);
    private static bool IsFinite(float f) => !float.IsNaN(f) && !float.IsInfinity(f);

    public static bool WithinReach(Vector3 playerPosition, Vector3 resourcePosition, float reach)
    {
        if (!IsFinite(playerPosition) || !IsFinite(resourcePosition) || !IsFinite(reach) || reach <= 0f)
            return false;
        // Double avoids overflow turning an invalidly large distance into a valid one.
        double x = (double)playerPosition.x - resourcePosition.x;
        double y = (double)playerPosition.y - resourcePosition.y;
        double z = (double)playerPosition.z - resourcePosition.z;
        return x * x + y * y + z * z <= (double)reach * reach;
    }

    public static bool IsBlockingCollider(Collider collider)
    {
        if (collider == null || !collider.enabled || collider.isTrigger || !collider.gameObject.activeInHierarchy)
            return false;
        // Test the hierarchy, not the object's name or layer. Child rubble colliders
        // and an OrePickup's own non-trigger BoxCollider must also be ignored.
        return collider.GetComponentInParent<DebrisCleanup>() == null &&
               collider.GetComponentInParent<OrePickup>() == null &&
               collider.GetComponentInParent<PlayerInventory>() == null;
    }

    public static bool HasSolidObstacle(PhysicsScene scene, Vector3 from, Vector3 to, ref RaycastHit[] hits)
    {
        if (!scene.IsValid() || !IsFinite(from) || !IsFinite(to)) return true;
        Vector3 delta = to - from;
        float distance = delta.magnitude;
        if (!IsFinite(distance)) return true;
        if (distance <= 0.00001f) return false;
        if (hits == null || hits.Length == 0) hits = new RaycastHit[32];
        Vector3 direction = delta / distance;
        while (true)
        {
            // All layers, including Ignore Raycast: layers must not accidentally turn
            // a real wall into air. Components identify the explicit exclusions above.
            int count = scene.Raycast(from, direction, hits, distance, ~0, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < count; i++)
                if (IsBlockingCollider(hits[i].collider)) return true;
            if (count < hits.Length) return false;
            // A full, unsorted result buffer does not prove that every hit was seen.
            // Re-query with a retained larger buffer, or fail closed at the hard cap.
            if (hits.Length >= MaximumRayHits) return true;
            Array.Resize(ref hits, Math.Min(MaximumRayHits, hits.Length * 2));
        }
    }
}
