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
    private Rigidbody _body;
    private Renderer[] _renderers;
    private UnityEngine.Rendering.ShadowCastingMode[] _originalShadows;
    private bool[] _originalReceiveShadows;

    private void Awake() => CacheComponents();
    private void CacheComponents()
    {
        if (_body == null) _body = GetComponent<Rigidbody>();
        if (_renderers != null) return;
        _renderers = GetComponentsInChildren<Renderer>(true);
        _originalShadows = new UnityEngine.Rendering.ShadowCastingMode[_renderers.Length];
        _originalReceiveShadows = new bool[_renderers.Length];
        for (int i = 0; i < _renderers.Length; i++)
        {
            _originalShadows[i] = _renderers[i].shadowCastingMode;
            _originalReceiveShadows[i] = _renderers[i].receiveShadows;
        }
    }
    public void SetShadowMode(bool disable)
    {
        CacheComponents();
        for (int i = 0; i < _renderers.Length; i++)
        {
            if (_renderers[i] == null) continue;
            _renderers[i].shadowCastingMode = disable ? UnityEngine.Rendering.ShadowCastingMode.Off : _originalShadows[i];
            _renderers[i].receiveShadows = !disable && _originalReceiveShadows[i];
        }
    }
    public void PrepareClientReuse(bool disableShadows)
    {
        CacheComponents(); CancelInvoke(); _owner = null;
        _body.isKinematic = true;
        SetShadowMode(disableShadows);
    }
    public void ConfigureForPool(DebrisPool owner, Vector3 scale, float mass, int materialId = 1)
    {
        CacheComponents(); CancelInvoke();
        _owner = owner; _networkScale = scale; _materialId = materialId;
        ApplyMaterial(); _body.mass = mass;
    }
    public override void OnStartServer()
    {
        base.OnStartServer(); CacheComponents(); CancelInvoke();
        _body.isKinematic = false;
        if (_owner == null && lifetime > 0f) Invoke(nameof(Despawn), lifetime);
    }
    public override void OnStartClient()
    {
        base.OnStartClient(); CacheComponents();
        transform.localScale = _networkScale;
        ApplyMaterial();
        if (!isServer) _body.isKinematic = true;
    }
    public override void OnStopServer() { CancelInvoke(); base.OnStopServer(); }
    private void OnMaterialChanged(int oldId, int newId) => ApplyMaterial();
    private void ApplyMaterial()
    {
        CacheComponents();
        var catalog = World.Instance != null ? World.Instance.MaterialCatalog : Resources.Load<TerrainMaterialCatalog>("Gobmine/TerrainMaterialCatalog");
        if (catalog == null) return;
        Material material = catalog.Debris(_materialId);
        if (material == null) return;
        foreach (Renderer renderer in _renderers) if (renderer != null) renderer.sharedMaterial = material;
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
