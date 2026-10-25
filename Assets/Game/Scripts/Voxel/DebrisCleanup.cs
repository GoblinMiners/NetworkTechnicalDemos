using UnityEngine;
using Mirror;

[RequireComponent(typeof(Rigidbody), typeof(NetworkIdentity))]
public class DebrisCleanup : NetworkBehaviour
{
    [Tooltip("Used ONLY for standalone debris. Pooled rubble uses DebrisPool's lifetime.")]
    public float lifetime = 5f;
    [SyncVar(hook = nameof(OnScaleChanged))] private Vector3 _networkScale = Vector3.one;
    [SyncVar(hook = nameof(OnMaterialChanged))] private int _materialId = 1;
    private DebrisPool _owner;

    public void ConfigureForPool(DebrisPool owner, Vector3 scale, float mass, int materialId = 1)
    {
        CancelInvoke();
        _owner = owner;
        _networkScale = scale;
        _materialId = materialId;
        ApplyMaterial();
        GetComponent<Rigidbody>().mass = mass;
    }

    public override void OnStartServer()
    {
        base.OnStartServer();
        CancelInvoke();
        GetComponent<Rigidbody>().isKinematic = false;
        if (_owner == null && lifetime > 0f) Invoke(nameof(Despawn), lifetime);
    }

    public override void OnStartClient()
    {
        base.OnStartClient();
        transform.localScale = _networkScale;
        ApplyMaterial();
        // Only the server simulates rubble. A server-authoritative NetworkTransform
        // on the prefab supplies pose updates to remote, kinematic client bodies.
        if (!isServer) GetComponent<Rigidbody>().isKinematic = true;
    }

    public override void OnStopServer()
    {
        CancelInvoke();
        base.OnStopServer();
    }

    private void OnMaterialChanged(int oldId, int newId) => ApplyMaterial();
    private void ApplyMaterial()
    {
        var catalog = World.Instance != null ? World.Instance.MaterialCatalog : Resources.Load<TerrainMaterialCatalog>("Gobmine/TerrainMaterialCatalog");
        if (catalog == null) return;
        Material material = catalog.Debris(_materialId);
        foreach (Renderer r in GetComponentsInChildren<Renderer>()) if (material != null) r.sharedMaterial = material;
    }
    private void OnDisable() => CancelInvoke();
    private void OnScaleChanged(Vector3 previous, Vector3 current) => transform.localScale = current;

    private void Despawn()
    {
        if (!isServer) return;
        if (_owner != null) _owner.ReturnDebris(gameObject);
        else NetworkServer.Destroy(gameObject);
    }
}
