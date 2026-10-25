using System;
using Mirror;
using UnityEngine;

/// <summary>Attach once to the NETWORK PLAYER PREFAB before it is spawned.</summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(NetworkIdentity))]
public sealed class GobminePlayerHealth : NetworkBehaviour
{
    [Min(1f)] public float maximumHealth = 100f;
    public bool showHealthHUD = true;
    [SyncVar] private float currentHealth = 100f;
    private float _nextCollapseDamageTime;
    private Collider _bodyCollider;
    public float CurrentHealth => currentHealth;
    public bool IsDead => currentHealth <= 0f;
    public event Action<GobminePlayerHealth> ServerDied;

    private void Awake() { _bodyCollider = GetComponent<Collider>(); }
    public Bounds DamageBounds => _bodyCollider != null && _bodyCollider.enabled
        ? _bodyCollider.bounds : new Bounds(transform.position, new Vector3(0.8f, 1.8f, 0.8f));

    public override void OnStartServer()
    {
        base.OnStartServer();
        currentHealth = Mathf.Max(1f, maximumHealth);
        _nextCollapseDamageTime = 0f;
    }

    [Server]
    public bool ServerApplyCollapseDamage(float amount, float cooldownSeconds)
    {
        if (!isServer || IsDead || float.IsNaN(amount) || float.IsInfinity(amount) || amount <= 0f ||
            Time.time < _nextCollapseDamageTime) return false;
        _nextCollapseDamageTime = Time.time + Mathf.Max(0f, cooldownSeconds);
        currentHealth = Mathf.Max(0f, currentHealth - amount);
        if (IsDead) ServerDied?.Invoke(this);
        return true;
    }

    // Deliberately not a client Command. A real respawn system should choose a
    // safe spawn point on the server before restoring health.
    [ContextMenu("Server / Restore Health (test only)")]
    public void ServerRestoreHealthForTesting()
    {
        if (!NetworkServer.active || !isServer) return;
        currentHealth = Mathf.Max(1f, maximumHealth);
        _nextCollapseDamageTime = 0f;
    }

    private GUIStyle _healthLabelStyle;

    private void OnGUI()
    {
        if (!showHealthHUD || !isLocalPlayer)
            return;

        GUI.depth = -100;

        float width = 280f;
        float x = Mathf.Max(10f, Screen.width - width - 15f);
        float y = 15f;

        if (_healthLabelStyle == null)
        {
            _healthLabelStyle = new GUIStyle(GUI.skin.label);
            _healthLabelStyle.fontSize = 18;
            _healthLabelStyle.fontStyle = FontStyle.Bold;
            _healthLabelStyle.normal.textColor = Color.white;
        }

        Color originalColor = GUI.color;

        // Dark background
        GUI.color = new Color(0f, 0f, 0f, 0.85f);
        GUI.DrawTexture(
            new Rect(x, y, width, 85f),
            Texture2D.whiteTexture
        );

        // Health text
        GUI.color = Color.white;

        GUI.Label(
            new Rect(x + 12f, y + 8f, width - 24f, 30f),
            $"HEALTH: {currentHealth:0} / {maximumHealth:0}",
            _healthLabelStyle
        );

        // Health bar background
        GUI.color = Color.gray;

        GUI.DrawTexture(
            new Rect(x + 12f, y + 48f, width - 24f, 20f),
            Texture2D.whiteTexture
        );

        // Health bar fill
        float percentage = Mathf.Clamp01(
            currentHealth / Mathf.Max(1f, maximumHealth)
        );

        GUI.color = percentage > 0.5f
            ? Color.green
            : percentage > 0.25f
                ? Color.yellow
                : Color.red;

        GUI.DrawTexture(
            new Rect(
                x + 12f,
                y + 48f,
                (width - 24f) * percentage,
                20f
            ),
            Texture2D.whiteTexture
        );

        GUI.color = Color.white;

        if (IsDead)
        {
            GUI.Label(
                new Rect(x, y + 90f, width, 30f),
                "PLAYER DOWN",
                _healthLabelStyle
            );
        }

        GUI.color = originalColor;
    }
}
