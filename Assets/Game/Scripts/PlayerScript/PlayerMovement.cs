using System;
using Mirror;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

/// <summary>
/// Owning-client CharacterController motor. Mirror's existing Client To Server
/// NetworkTransform distributes poses. It is NOT server-authoritative anti-cheat.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(CharacterController))]
[DefaultExecutionOrder(100)]
public class PlayerMovement : NetworkBehaviour
{
    [Header("Movement")]
    public float moveSpeed = 5f; // Existing prefab field preserved.
    [Min(0f)] public float jumpHeight = 1.1f;
    [Tooltip("Must be negative. CharacterController.Move does not supply gravity.")]
    public float gravity = -24f;
    [Min(1f)] public float terminalFallSpeed = 25f;
    [UnityEngine.Range(0f, 0.3f)] public float jumpBufferSeconds = 0.12f;
    [UnityEngine.Range(0f, 0.3f)] public float coyoteSeconds = 0.1f;
    [UnityEngine.Range(0f, 0.3f)] public float groundSnapDistance = 0.15f;
    [Tooltip("Cap motor catch-up during a hitch. This can slow movement below 20 FPS rather than making a giant teleport.")]
    [UnityEngine.Range(0.02f, 0.1f)] public float maximumMotorDeltaTime = 0.05f;

    [Header("Input")]
    [Tooltip("Assign the existing PlayerControls.inputactions asset. It is cloned at runtime; your asset/bindings are not overwritten.")]
    public InputActionAsset inputActions;
    [Tooltip("Only used when the asset has no bound Player/Jump action.")]
    public bool useJumpFallback = true;

    [Header("Look / Camera")]
    public float lookSensitivity = 0.2f; // Existing mouse sensitivity preserved.
    public float gamepadLookDegreesPerSecond = 150f;
    public Transform cameraPivot;
    public GameObject localCamera;
    public GameObject localCinemachine;

    [Header("Falling-chunk knockback")]
    [Min(1f)] public float knockbackDeceleration = 10f;
    [Header("Terrain safety")]
    public bool guardTerrainStreaming = true;
    public bool rollbackSolidPenetration = true;

    private CharacterController _controller;
    private GobminePlayerHealth _health;
    private InputActionAsset _runtimeInput;
    private InputAction _move, _look, _jump, _fallbackJump;
    private readonly PlayerJumpBuffer _jumpBuffer = new PlayerJumpBuffer();
    private readonly RaycastHit[] _groundHits = new RaycastHit[32];
    private float _verticalPitch, _verticalVelocity, _configuredStepOffset;
    private bool _localStarted, _grounded, _swimUp, _swimDown;
    private int _collisionMask;
    private Vector3 _chunkImpulseVelocity;
    private float _nextWarning;
    public bool IsGrounded => _grounded;
    public float VerticalVelocity => _verticalVelocity;
    public string MovementStatus { get; private set; } = "Not local / not started";
    public int TerrainWaitFrames { get; private set; }
    public int PenetrationRollbacks { get; private set; }

    private void Awake()
    {
        _controller = GetComponent<CharacterController>();
        _health = GetComponent<GobminePlayerHealth>();
    }

    public override void OnStartLocalPlayer()
    {
        base.OnStartLocalPlayer();
        if (!ValidateMotor()) { enabled = false; return; }
        _localStarted = true;
        _configuredStepOffset = _controller.stepOffset;
        _controller.minMoveDistance = 0f;
        _controller.detectCollisions = true;
        _controller.enableOverlapRecovery = true;
        _collisionMask = 0;
        for (int layer = 0; layer < 32; layer++)
            if (!Physics.GetIgnoreLayerCollision(gameObject.layer, layer)) _collisionMask |= 1 << layer;
        if (localCamera != null) localCamera.SetActive(true);
        if (localCinemachine != null) localCinemachine.SetActive(true);
        if (cameraPivot != null) _verticalPitch = Mathf.DeltaAngle(0f, cameraPivot.localEulerAngles.x);
        Cursor.lockState = CursorLockMode.Locked; Cursor.visible = false;
        try { CreateInput(); }
        catch (Exception ex)
        {
            Debug.LogError("[Gobmine Movement] Input setup failed: " + ex.Message, this);
            DisposeInput(); _localStarted = false; enabled = false;
        }
    }

    private bool ValidateMotor()
    {
        _controller = GetComponent<CharacterController>();
        Rigidbody body = GetComponent<Rigidbody>();
        if (_controller == null || !_controller.enabled || (body != null && !body.isKinematic))
        {
            Debug.LogError("[Gobmine Movement] A working CharacterController and no dynamic Rigidbody are required. Run Tools > Gobmine > Movement > Install Player Controller.", this);
            return false;
        }
        foreach (Collider collider in GetComponents<Collider>())
            if (collider != _controller && collider.enabled && !collider.isTrigger)
            {
                Debug.LogError("[Gobmine Movement] A second solid ROOT collider conflicts with the controller. Run the movement installer; keep only the CharacterController as the root movement shape.", this);
                return false;
            }
        if (!PlayerMotorRules.Finite(gravity) || gravity >= 0f || !PlayerMotorRules.Finite(jumpHeight) || jumpHeight < 0f ||
            !PlayerMotorRules.Finite(moveSpeed) || moveSpeed < 0f || !PlayerMotorRules.Finite(terminalFallSpeed) || terminalFallSpeed <= 0f)
        { Debug.LogError("[Gobmine Movement] Invalid speed, jump height, or gravity.", this); return false; }
        Vector3 scale = transform.lossyScale;
        if (Vector3.Distance(scale, Vector3.one) > 0.001f || Mathf.Abs(Vector3.Dot(transform.up, Vector3.up) - 1f) > 0.001f)
        { Debug.LogError("[Gobmine Movement] Player root must be upright and have world scale (1,1,1). Scale a visual child instead.", this); return false; }
        return true;
    }

    private void CreateInput()
    {
        _runtimeInput = inputActions != null ? Instantiate(inputActions) : CreateDefaultInput();
        _move = _runtimeInput.FindAction("Player/Move", true);
        _look = _runtimeInput.FindAction("Player/Look", true);
        _jump = _runtimeInput.FindAction("Player/Jump", false);
        if ((_jump == null || _jump.bindings.Count == 0) && useJumpFallback)
        {
            _fallbackJump = new InputAction("GobmineJumpFallback", InputActionType.Button, "<Keyboard>/space");
            _fallbackJump.AddBinding("<Gamepad>/buttonSouth");
            _jump = _fallbackJump;
            Debug.LogWarning("[Gobmine Movement] No bound Player/Jump action found. Using Space / gamepad south button until you bind Jump in your asset.", this);
        }
        _move.Enable(); _look.Enable(); _jump?.Enable();
    }

    private static InputActionAsset CreateDefaultInput()
    {
        var asset = ScriptableObject.CreateInstance<InputActionAsset>();
        var map = new InputActionMap("Player"); asset.AddActionMap(map);
        InputAction move = map.AddAction("Move", InputActionType.Value, expectedControlLayout: "Vector2");
        move.AddCompositeBinding("2DVector").With("Up", "<Keyboard>/w").With("Down", "<Keyboard>/s")
            .With("Left", "<Keyboard>/a").With("Right", "<Keyboard>/d");
        move.AddBinding("<Gamepad>/leftStick");
        InputAction look = map.AddAction("Look", InputActionType.Value, expectedControlLayout: "Vector2");
        look.AddBinding("<Mouse>/delta"); look.AddBinding("<Gamepad>/rightStick");
        InputAction jump = map.AddAction("Jump", InputActionType.Button, "<Keyboard>/space");
        jump.AddBinding("<Gamepad>/buttonSouth");
        return asset;
    }

    public override void OnStopLocalPlayer()
    {
        _localStarted = false; DisposeInput();
        Cursor.lockState = CursorLockMode.None; Cursor.visible = true;
        base.OnStopLocalPlayer();
    }
    private void OnEnable()
    {
        if (!_localStarted) return;
        _move?.Enable(); _look?.Enable(); _jump?.Enable();
    }
    private void OnDisable()
    {
        _runtimeInput?.Disable(); _fallbackJump?.Disable();
        _jumpBuffer.Reset(); _verticalVelocity = 0f; _grounded = false; _chunkImpulseVelocity = Vector3.zero;
        if (_controller != null && _localStarted) _controller.stepOffset = _configuredStepOffset;
    }
    private void OnDestroy() { DisposeInput(); }
    private void DisposeInput()
    {
        if (_runtimeInput != null) { _runtimeInput.Disable(); Destroy(_runtimeInput); _runtimeInput = null; }
        if (_fallbackJump != null) { _fallbackJump.Disable(); _fallbackJump.Dispose(); _fallbackJump = null; }
        _move = _look = _jump = null;
    }

    private void Update()
    {
        if (!isLocalPlayer || !_localStarted || _controller == null || !_controller.enabled || _move == null) return;
        bool menuOpen = GobminePlayerEquipment.LocalMenuOpen;
        if (!menuOpen) UpdateLook();
        World world = World.Instance;
        if (world == null || !world.IsReady)
        { MovementStatus = "Waiting for world / terrain snapshot"; TerrainWaitFrames++; return; }
        if (Physics.GetIgnoreLayerCollision(gameObject.layer, world.terrainLayer) ||
            (_controller.excludeLayers.value & (1 << world.terrainLayer)) != 0)
        {
            MovementStatus = "Player/terrain collision is disabled in Physics layers or controller overrides";
            WarnThrottled(MovementStatus); return;
        }
        bool dead = _health != null && _health.IsDead;
        if (dead) _jumpBuffer.Reset();
        else if (!menuOpen && _jump != null && _jump.WasPressedThisFrame()) _jumpBuffer.Press(Time.time);
        _swimUp = !dead && !menuOpen && _jump != null && _jump.IsPressed();
        InputAction descend = _runtimeInput.FindAction("Player/Descend", false);
        if (descend != null && !descend.enabled) descend.Enable();
        _swimDown = !dead && !menuOpen && ((descend != null && descend.IsPressed()) ||
            (Keyboard.current != null && Keyboard.current.leftCtrlKey.isPressed) || (Gamepad.current != null && Gamepad.current.rightShoulder.isPressed));
        Vector2 input = dead || menuOpen ? Vector2.zero : _move.ReadValue<Vector2>();
        Vector3 horizontal = PlayerMotorRules.HorizontalMotion(input, transform.right, transform.forward, moveSpeed);
        float frameDt = Mathf.Min(Time.deltaTime, Mathf.Clamp(maximumMotorDeltaTime, 0.02f, 0.1f));
        if (frameDt <= 0f) return;
        // Short swept moves bound work and help with thin voxel ledges and low FPS.
        float estimatedSpeed = Mathf.Max(Mathf.Abs(_verticalVelocity), PlayerMotorRules.JumpSpeed(jumpHeight, gravity)) + horizontal.magnitude;
        int steps = Mathf.Clamp(Mathf.Max(Mathf.CeilToInt(frameDt / (1f / 90f)), Mathf.CeilToInt(estimatedSpeed * frameDt / 0.15f)), 1, 16);
        float dt = frameDt / steps;
        for (int i = 0; i < steps; i++) if (!MoveStep(world, horizontal, dt, dead)) break;
    }

    private void UpdateLook()
    {
        Vector2 look = _look.ReadValue<Vector2>();
        bool stick = _look.activeControl != null && _look.activeControl.device is Gamepad;
        float sensitivity = stick ? gamepadLookDegreesPerSecond * Time.deltaTime : lookSensitivity;
        transform.Rotate(Vector3.up * (look.x * sensitivity));
        _verticalPitch = Mathf.Clamp(_verticalPitch - look.y * sensitivity, -89f, 89f);
        if (cameraPivot != null) cameraPivot.localRotation = Quaternion.Euler(_verticalPitch, 0f, 0f);
    }

    private bool MoveStep(World world, Vector3 horizontal, float dt, bool dead)
    {
        Vector3 bodyCentre = transform.TransformPoint(_controller.center);
        bool swimming = world.FluidAt(bodyCentre) == GobmineFluidKind.Water;
        GobmineFluidKind feetFluid = world.FluidAt(bodyCentre-Vector3.up*(_controller.height*0.5f-0.08f));
        if (swimming) { horizontal *= 0.55f; _jumpBuffer.Reset(); }
        else if (feetFluid == GobmineFluidKind.Water) horizontal *= 0.7f;
        else if (feetFluid == GobmineFluidKind.Lava) horizontal *= 0.25f;
        bool wantsJump = !swimming && !dead && !GobminePlayerEquipment.LocalMenuOpen && _jumpBuffer.CanConsume(Time.time, jumpBufferSeconds, coyoteSeconds);
        bool wasGrounded = _grounded && !swimming;
        float velocity = wantsJump ? PlayerMotorRules.JumpSpeed(jumpHeight, gravity) : _verticalVelocity;
        if (wasGrounded && !wantsJump && velocity < 0f) velocity = -2f;
        float nextVelocity = Mathf.Max(-terminalFallSpeed, velocity + gravity * dt);
        if (swimming)
        {
            float target = _swimUp ? 3f : _swimDown ? -3f : 0f;
            velocity = Mathf.Clamp(velocity, -4f, 4f);
            nextVelocity = Mathf.MoveTowards(velocity, target, 14f*dt);
        }
        Vector3 displacement = (horizontal + _chunkImpulseVelocity) * dt +
            Vector3.up * ((velocity + nextVelocity) * 0.5f * dt);
        Vector3 centre = transform.TransformPoint(_controller.center);
        Bounds sweep = PlayerMotorRules.SweptBounds(centre, _controller.radius, _controller.height,
            displacement, _configuredStepOffset, groundSnapDistance, Mathf.Max(0.02f, _controller.skinWidth));
        string waitingReason;
        if (guardTerrainStreaming && !world.TryPreparePlayerCollision(sweep, out waitingReason))
        {
            MovementStatus = waitingReason; TerrainWaitFrames++;
            // No catch-up gravity and no accumulated displacement while a collider is unavailable.
            return false;
        }
        if (rollbackSolidPenetration && world.PlayerCentreInsideSolidCell(centre))
        {
            MovementStatus = "Player centre already inside solid terrain; check spawn/teleport position";
            WarnThrottled(MovementStatus); return false;
        }
        if (wantsJump) { _jumpBuffer.Consume(); _grounded = false; }
        float stepOffset = wasGrounded && !wantsJump ? _configuredStepOffset : 0f;
        if (!Mathf.Approximately(_controller.stepOffset, stepOffset)) _controller.stepOffset = stepOffset;
        Vector3 previousPosition = transform.position;
        CollisionFlags flags = _controller.Move(displacement);
        _verticalVelocity = nextVelocity;
        if ((flags & CollisionFlags.Above) != 0 && _verticalVelocity > 0f) _verticalVelocity = 0f;
        _grounded = !swimming && (flags & CollisionFlags.Below) != 0 && _verticalVelocity <= 0f;
        // A short probe prevents bouncing on descending voxel slopes. Never snap upward
        // while jumping, or attach a falling player to a distant floor.
        if (!swimming && !_grounded && !wantsJump && _verticalVelocity <= 0f && wasGrounded)
        {
            float drop;
            if (TryGroundDrop(out drop))
            {
                CollisionFlags snap = _controller.Move(Vector3.down * Mathf.Max(0.005f, drop + 0.02f));
                _grounded = (snap & CollisionFlags.Below) != 0;
            }
        }
        if (rollbackSolidPenetration && world.PlayerCentreInsideSolidCell(transform.TransformPoint(_controller.center)))
        {
            // Exceptional recovery only. Normal locomotion never writes position directly.
            _controller.enabled = false; transform.position = previousPosition; _controller.enabled = true;
            _verticalVelocity = 0f; _grounded = false; _jumpBuffer.Reset(); PenetrationRollbacks++;
            MovementStatus = "Rolled back a move into solid terrain"; WarnThrottled(MovementStatus); return false;
        }
        _chunkImpulseVelocity = Vector3.MoveTowards(_chunkImpulseVelocity,Vector3.zero,Mathf.Max(0.2f,knockbackDeceleration)*dt);
        if (_grounded)
        {
            _verticalVelocity = -2f;
            if (!dead) _jumpBuffer.Grounded(Time.time);
        }
        MovementStatus = dead ? "Downed (gravity only)" : swimming ? "Swimming" : _grounded ? "Grounded" : _verticalVelocity > 0f ? "Jumping" : "Falling";
        return true;
    }

    private bool TryGroundDrop(out float drop)
    {
        drop = 0f;
        if (groundSnapDistance <= 0f) return false;
        float radius = Mathf.Max(0.01f, _controller.radius - _controller.skinWidth);
        float lift = 0.05f;
        Vector3 origin = transform.TransformPoint(_controller.center) -
            Vector3.up * Mathf.Max(0f, _controller.height * 0.5f - _controller.radius) + Vector3.up * lift;
        float offset = lift + _controller.radius - radius;
        PhysicsScene physics = gameObject.scene.GetPhysicsScene();
        int count = physics.SphereCast(origin, radius, Vector3.down, _groundHits,
            groundSnapDistance + offset, _collisionMask, QueryTriggerInteraction.Ignore);
        if (count == _groundHits.Length) return false; // A truncated query is not proof of support.
        float best = float.PositiveInfinity;
        float normalLimit = Mathf.Cos(_controller.slopeLimit * Mathf.Deg2Rad);
        for (int i = 0; i < count; i++)
        {
            RaycastHit hit = _groundHits[i];
            if (hit.collider == null || hit.collider.transform.IsChildOf(transform) || hit.normal.y < normalLimit) continue;
            float distance = Mathf.Max(0f, hit.distance - offset);
            if (distance < best) best = distance;
        }
        if (best > groundSnapDistance) return false;
        drop = best; return true;
    }

    [TargetRpc]
    public void TargetReceiveChunkKnockback(NetworkConnectionToClient owner, Vector3 worldVelocity)
    {
        if (!isLocalPlayer || !PlayerMotorRules.Finite(worldVelocity)) return;
        _chunkImpulseVelocity = Vector3.ClampMagnitude(_chunkImpulseVelocity + new Vector3(worldVelocity.x,0f,worldVelocity.z),18f);
        if (worldVelocity.y > 0f) _verticalVelocity = Mathf.Max(_verticalVelocity,Mathf.Min(worldVelocity.y,8f));
    }

    private void WarnThrottled(string message)
    {
        if (Time.unscaledTime < _nextWarning) return;
        _nextWarning = Time.unscaledTime + 3f;
        Debug.LogWarning("[Gobmine Movement] " + message + " at " + transform.position, this);
    }
}
