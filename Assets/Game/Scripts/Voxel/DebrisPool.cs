using System.Collections.Generic;
using UnityEngine;
using Mirror;

/// <summary>
/// Server-side pool. Every checkout calls Spawn, every return calls UnSpawn.
/// Remote clients use Mirror's NORMAL registered-prefab instantiate/destroy path.
/// No custom client spawn handlers are required or installed by this script.
/// </summary>
public class DebrisPool : MonoBehaviour
{
    public static DebrisPool Instance { get; private set; }
    [Header("Pool Settings")]
    [SerializeField] private GameObject debrisPrefab;
    [SerializeField, Min(0)] private int initialPoolSize = 80;
    [Tooltip("Zero keeps rubble indefinitely. Use with care; it can exhaust the active limit.")]
    [SerializeField, Min(0)] private float autoDespawnTime = 25f;
    [Tooltip("Zero is unlimited. When full, World retains unspawned rubble in its queue.")]
    [SerializeField, Min(0)] private int maxActiveDebris = 512;
    [SerializeField, Min(0.01f)] private float materialDensityKgPerCubicMetre = 2500f;
    private readonly Queue<GameObject> _pool = new Queue<GameObject>();
    private readonly Dictionary<GameObject, float> _expiry = new Dictionary<GameObject, float>();
    private readonly List<GameObject> _expired = new List<GameObject>();
    private bool _warmed;
    private bool _serverWasActive;

    public bool IsConfigured => debrisPrefab != null && debrisPrefab.GetComponent<NetworkIdentity>() != null &&
        debrisPrefab.GetComponent<Rigidbody>() != null && debrisPrefab.GetComponent<DebrisCleanup>() != null &&
        debrisPrefab.GetComponentInChildren<Collider>() != null;
    public bool HasCapacity => maxActiveDebris <= 0 || _expiry.Count < maxActiveDebris;
    public int ActiveCount => _expiry.Count;

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
    }

    private void Update()
    {
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
            if (obj == null) _expiry.Remove(obj);
            else ReturnDebris(obj);
        }
    }

    private void WarmPool()
    {
        if (_warmed || !IsConfigured) return;
        _warmed = true;
        for (int i = 0; i < Mathf.Max(0, initialPoolSize); i++) _pool.Enqueue(CreateInactive());
    }

    private GameObject CreateInactive()
    {
        GameObject obj = Instantiate(debrisPrefab);
        obj.SetActive(false);
        return obj; // Never Spawn an inactive prewarmed object.
    }

    // Preserve the old three-argument entry point for other project callers.
    public GameObject GetDebris(Vector3 position, Quaternion rotation, Vector3 scale) =>
        GetDebris(position, rotation, scale, Mathf.Abs(scale.x * scale.y * scale.z));

    public GameObject GetDebris(Vector3 position, Quaternion rotation, Vector3 scale, float materialVolume) =>
        GetDebris(position, rotation, scale, materialVolume, 1);

    public GameObject GetDebris(Vector3 position, Quaternion rotation, Vector3 scale, float materialVolume, int materialId)
    {
        if (!NetworkServer.active || !IsConfigured || !HasCapacity) return null;
        WarmPool();
        GameObject obj = null;
        while (_pool.Count > 0 && obj == null) obj = _pool.Dequeue();
        if (obj == null) obj = CreateInactive();
        obj.transform.SetPositionAndRotation(position, rotation);
        obj.transform.localScale = scale;
        obj.GetComponent<DebrisCleanup>().ConfigureForPool(this, scale,
            Mathf.Clamp(materialVolume * materialDensityKgPerCubicMetre, 0.001f, 10000f), materialId);
        obj.SetActive(true);
        Rigidbody rb = obj.GetComponent<Rigidbody>();
        rb.isKinematic = false;
        rb.useGravity = true;
        SetLinearVelocity(rb, Vector3.zero);
        rb.angularVelocity = Random.insideUnitSphere * 4f;
        rb.WakeUp();
        _expiry[obj] = autoDespawnTime > 0f ? Time.time + autoDespawnTime : float.PositiveInfinity;
        NetworkServer.Spawn(obj);
        return obj;
    }

    public void ReturnDebris(GameObject obj)
    {
        if (!NetworkServer.active || obj == null || !_expiry.Remove(obj)) return;
        // Notify clients BEFORE hiding/reusing the server object. UnSpawn resets
        // the identity so a subsequent Spawn starts a fresh network lifetime.
        NetworkServer.UnSpawn(obj);
        Rigidbody rb = obj.GetComponent<Rigidbody>();
        if (rb != null)
        {
            if (!rb.isKinematic)
            {
                SetLinearVelocity(rb, Vector3.zero);
                rb.angularVelocity = Vector3.zero;
            }
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

    private void ClearLocalPool()
    {
        foreach (var lease in _expiry) if (lease.Key != null) Destroy(lease.Key);
        _expiry.Clear();
        while (_pool.Count > 0) { GameObject obj = _pool.Dequeue(); if (obj != null) Destroy(obj); }
        _warmed = false;
    }

    private void OnDestroy()
    {
        if (Instance != this) return;
        foreach (var lease in _expiry)
        {
            if (lease.Key == null) continue;
            if (NetworkServer.active) NetworkServer.Destroy(lease.Key);
            else Destroy(lease.Key);
        }
        _expiry.Clear();
        while (_pool.Count > 0) { GameObject obj = _pool.Dequeue(); if (obj != null) Destroy(obj); }
        Instance = null;
    }
}
