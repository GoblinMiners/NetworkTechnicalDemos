using System.Collections.Generic;
using UnityEngine;
using Mirror;

/// <summary>
/// Server pool plus remote-client spawn/unspawn pooling. Rubble is cosmetic:
/// resource ownership stays in PlayerInventory / ResourcePileStore, never here.
/// </summary>
public class DebrisPool : MonoBehaviour
{
    public static DebrisPool Instance { get; private set; }
    [Header("Pool Settings")]
    [SerializeField] private GameObject debrisPrefab;
    [SerializeField, Min(0)] private int initialPoolSize = 64;
    [Tooltip("Zero keeps rubble indefinitely. Resource pickups have a separate lifetime.")]
    [SerializeField, Min(0)] private float autoDespawnTime = 8f;
    [Tooltip("Zero is unlimited. A positive limit bounds active networked rubble bodies.")]
    [SerializeField, Min(0)] private int maxActiveDebris = 128;
    [SerializeField, Min(0.01f)] private float materialDensityKgPerCubicMetre = 2500f;

    [Header("Performance")]
    [Tooltip("When full, remove the oldest cosmetic rubble to show a new piece. Does not remove inventory or ore-pickup value. Off preserves the old wait-for-expiry policy.")]
    [SerializeField] private bool recycleOldestWhenFull = false;
    [SerializeField, Min(1)] private int prewarmPerFrame = 8;
    [SerializeField] private bool poolRemoteClients = true;
    [SerializeField, Min(0)] private int maxClientPooledDebris = 128;
    [SerializeField] private bool disableDebrisShadows = true;

    private readonly Queue<GameObject> _pool = new Queue<GameObject>();
    private readonly Queue<GameObject> _clientPool = new Queue<GameObject>();
    private readonly Dictionary<GameObject, float> _expiry = new Dictionary<GameObject, float>();
    private readonly LinkedList<GameObject> _activeOrder = new LinkedList<GameObject>();
    private readonly Dictionary<GameObject, LinkedListNode<GameObject>> _orderNodes = new Dictionary<GameObject, LinkedListNode<GameObject>>();
    private readonly List<GameObject> _expired = new List<GameObject>();
    private bool _serverWasActive;
    private bool _clientRegistered;
    private GameObject _checkedPrefab;
    private bool _prefabValid;
    private static readonly Unity.Profiling.ProfilerMarker PoolMarker = new Unity.Profiling.ProfilerMarker("Gobmine.DebrisPoolMaintenance");

    public bool IsConfigured
    {
        get
        {
            if (_checkedPrefab != debrisPrefab || !_prefabValid)
            {
                _checkedPrefab = debrisPrefab;
                _prefabValid = debrisPrefab != null && debrisPrefab.GetComponent<NetworkIdentity>() != null &&
                    debrisPrefab.GetComponent<Rigidbody>() != null && debrisPrefab.GetComponent<DebrisCleanup>() != null &&
                    debrisPrefab.GetComponentInChildren<Collider>() != null;
            }
            return _prefabValid;
        }
    }
    public bool HasCapacity => maxActiveDebris <= 0 || _expiry.Count < maxActiveDebris || recycleOldestWhenFull;
    public int ActiveCount => _expiry.Count;
    public int InactiveCount => _pool.Count;
    public int ClientInactiveCount => _clientPool.Count;
    public int ServerInstancesCreated { get; private set; }
    public int ClientInstancesCreated { get; private set; }
    public int RecycledForCapacity { get; private set; }

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
    }

    private void Update()
    {
        using (PoolMarker.Auto())
        {
            EnsureClientPoolRegistered();
            if (_clientRegistered && !NetworkClient.active)
            {
                // Mirror already clears registration dictionaries during shutdown.
                _clientRegistered = false;
                DestroyInactive(_clientPool);
            }
            if (!NetworkServer.active)
            {
                if (_serverWasActive) ClearLocalPool();
                _serverWasActive = false;
                return;
            }
            _serverWasActive = true;
            WarmPool();
            _expired.Clear();
            foreach (var lease in _expiry)
                if (lease.Key == null || Time.time >= lease.Value) _expired.Add(lease.Key);
            foreach (GameObject obj in _expired)
            {
                if (obj == null) RemoveLease(obj);
                else ReturnDebris(obj);
            }
        }
    }

    /// <summary>
    /// Called by World.OnStartClient and retried in Update. Host shares server
    /// objects, so custom remote spawn handlers are deliberately not used there.
    /// Leave the prefab in NetworkManager's list as an early-spawn fallback;
    /// this method removes the normal registration before installing the handler.
    /// </summary>
    public void EnsureClientPoolRegistered()
    {
        if (!poolRemoteClients || !NetworkClient.active || NetworkServer.active || _clientRegistered || !IsConfigured) return;
        NetworkIdentity identity = debrisPrefab.GetComponent<NetworkIdentity>();
        if (identity.assetId == 0) return;
        NetworkClient.UnregisterPrefab(debrisPrefab);
        NetworkClient.RegisterPrefab(debrisPrefab, SpawnClientDebris, UnspawnClientDebris);
        _clientRegistered = true;
    }

    private GameObject SpawnClientDebris(SpawnMessage message)
    {
        GameObject obj = null;
        while (_clientPool.Count > 0 && obj == null) obj = _clientPool.Dequeue();
        if (obj == null) { obj = CreateInactive(); ClientInstancesCreated++; }
        obj.transform.SetPositionAndRotation(message.position, message.rotation);
        obj.transform.localScale = message.scale;
        obj.GetComponent<DebrisCleanup>().PrepareClientReuse(disableDebrisShadows);
        // Mirror applies SyncVars and calls OnStartClient after this handler returns.
        obj.SetActive(true);
        return obj;
    }

    private void UnspawnClientDebris(GameObject obj)
    {
        if (obj == null) return;
        obj.SetActive(false);
        // Mirror resets the NetworkIdentity after the unspawn callback.
        // Never enqueue the server/host object in a second client pool.
        if (_clientPool.Count < Mathf.Max(0, maxClientPooledDebris)) _clientPool.Enqueue(obj);
        else Destroy(obj);
    }

    private void WarmPool()
    {
        if (!IsConfigured) return;
        int target = Mathf.Max(0, initialPoolSize);
        if (maxActiveDebris > 0) target = Mathf.Min(target, maxActiveDebris);
        int toCreate = Mathf.Min(Mathf.Max(1, prewarmPerFrame), target - _pool.Count - _expiry.Count);
        for (int i = 0; i < toCreate; i++) { _pool.Enqueue(CreateInactive()); ServerInstancesCreated++; }
    }

    private GameObject CreateInactive()
    {
        // Keep root transforms: the pool object's position in the scene must not
        // offset network spawn positions or the rubble's scale.
        GameObject obj = Instantiate(debrisPrefab);
        obj.SetActive(false);
        return obj;
    }

    public GameObject GetDebris(Vector3 position, Quaternion rotation, Vector3 scale) =>
        GetDebris(position, rotation, scale, Mathf.Abs(scale.x * scale.y * scale.z));
    public GameObject GetDebris(Vector3 position, Quaternion rotation, Vector3 scale, float materialVolume) =>
        GetDebris(position, rotation, scale, materialVolume, 1);

    public GameObject GetDebris(Vector3 position, Quaternion rotation, Vector3 scale, float materialVolume, int materialId)
    {
        if (!NetworkServer.active || !IsConfigured || !HasCapacity) return null;
        // A lowered live cap may require returning more than one old lease.
        while (maxActiveDebris > 0 && _expiry.Count >= maxActiveDebris)
        {
            if (!recycleOldestWhenFull || _activeOrder.First == null) return null;
            GameObject oldest = _activeOrder.First.Value;
            if (oldest == null) RemoveLease(oldest);
            else ReturnDebris(oldest);
            RecycledForCapacity++;
        }
        GameObject obj = null;
        while (_pool.Count > 0 && obj == null) obj = _pool.Dequeue();
        if (obj == null) { obj = CreateInactive(); ServerInstancesCreated++; }
        obj.transform.SetPositionAndRotation(position, rotation);
        obj.transform.localScale = scale;
        DebrisCleanup cleanup = obj.GetComponent<DebrisCleanup>();
        cleanup.ConfigureForPool(this, scale,
            Mathf.Clamp(materialVolume * materialDensityKgPerCubicMetre, 0.001f, 10000f), materialId);
        cleanup.SetShadowMode(disableDebrisShadows);
        obj.SetActive(true);
        Rigidbody rb = obj.GetComponent<Rigidbody>();
        rb.isKinematic = false;
        rb.useGravity = true;
        SetLinearVelocity(rb, Vector3.zero);
        rb.angularVelocity = Random.insideUnitSphere * 4f;
        rb.WakeUp(); // Once per new lease, not every frame. Sleeping is otherwise left to physics.
        _expiry.Add(obj, autoDespawnTime > 0f ? Time.time + autoDespawnTime : float.PositiveInfinity);
        _orderNodes.Add(obj, _activeOrder.AddLast(obj));
        NetworkServer.Spawn(obj);
        return obj;
    }

    private bool RemoveLease(GameObject obj)
    {
        if (!_expiry.Remove(obj)) return false;
        LinkedListNode<GameObject> node;
        if (_orderNodes.TryGetValue(obj, out node)) { _activeOrder.Remove(node); _orderNodes.Remove(obj); }
        return true;
    }

    public void ReturnDebris(GameObject obj)
    {
        if (!NetworkServer.active || obj == null || !RemoveLease(obj)) return;
        NetworkServer.UnSpawn(obj);
        Rigidbody rb = obj.GetComponent<Rigidbody>();
        if (rb != null)
        {
            if (!rb.isKinematic) { SetLinearVelocity(rb, Vector3.zero); rb.angularVelocity = Vector3.zero; }
            rb.isKinematic = true;
        }
        obj.SetActive(false);
        _pool.Enqueue(obj);
    }

    public static void SetLinearVelocity(Rigidbody body, Vector3 value)
    {
#if UNITY_6000_0_OR_NEWER
        body.linearVelocity = value;
#else
        body.velocity = value;
#endif
    }

    private static void DestroyInactive(Queue<GameObject> queue)
    {
        while (queue.Count > 0) { GameObject obj = queue.Dequeue(); if (obj != null) Destroy(obj); }
    }
    private void ClearLocalPool()
    {
        foreach (var lease in _expiry) if (lease.Key != null) Destroy(lease.Key);
        _expiry.Clear(); _activeOrder.Clear(); _orderNodes.Clear();
        DestroyInactive(_pool);
    }
    private void OnDestroy()
    {
        if (Instance != this) return;
        if (_clientRegistered && debrisPrefab != null)
        {
            NetworkClient.UnregisterPrefab(debrisPrefab);
            // Avoid leaving a callback to a destroyed component during a scene transition.
            if (NetworkClient.active) NetworkClient.RegisterPrefab(debrisPrefab);
        }
        _clientRegistered = false;
        DestroyInactive(_clientPool);
        foreach (var lease in _expiry)
        {
            if (lease.Key == null) continue;
            if (NetworkServer.active) NetworkServer.Destroy(lease.Key);
            else Destroy(lease.Key);
        }
        _expiry.Clear(); _activeOrder.Clear(); _orderNodes.Clear();
        DestroyInactive(_pool);
        Instance = null;
    }
}
