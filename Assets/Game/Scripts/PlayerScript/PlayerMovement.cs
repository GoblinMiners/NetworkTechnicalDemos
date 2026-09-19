using UnityEngine;
using UnityEngine.InputSystem;
using Mirror;

public class PlayerMovement : NetworkBehaviour
{
    [Header("Movement")]
    public float moveSpeed = 5f;
    
    [Header("Look / Camera")]
    public float lookSensitivity = 0.2f;
    public Transform cameraPivot;
    public GameObject localCamera;
    public GameObject localCinemachine;

    private PlayerControls _controls;
    private Vector2 _moveInput;
    private Vector2 _lookInput;
    private float _verticalPitch = 0f;

    public override void OnStartLocalPlayer()
    {
        if (localCamera != null) localCamera.SetActive(true);
        if (localCinemachine != null) localCinemachine.SetActive(true);

        // Lock the cursor to the center of the screen
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;

        if (World.Instance != null) World.Instance.playerTarget = this.transform;

        _controls = new PlayerControls();
        
        _controls.Player.Move.performed += ctx => _moveInput = ctx.ReadValue<Vector2>();
        _controls.Player.Move.canceled += ctx => _moveInput = Vector2.zero;

        _controls.Player.Look.performed += ctx => _lookInput = ctx.ReadValue<Vector2>();
        _controls.Player.Look.canceled += ctx => _lookInput = Vector2.zero;

        _controls.Enable();
    }

    public override void OnStopLocalPlayer()
    {
        if (_controls != null) _controls.Disable();
        
        // Unlock cursor when leaving the game
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
    }

    private void Update()
    {
        if (!isLocalPlayer) return;

        // 1. Mouse Look
        // Rotate the entire player body left and right
        transform.Rotate(Vector3.up * (_lookInput.x * lookSensitivity));

        // Rotate the camera pivot up and down, clamped to prevent flipping
        _verticalPitch -= _lookInput.y * lookSensitivity;
        _verticalPitch = Mathf.Clamp(_verticalPitch, -89f, 89f);
        
        if (cameraPivot != null)
        {
            cameraPivot.localRotation = Quaternion.Euler(_verticalPitch, 0f, 0f);
        }

        // 2. Movement
        // Multiply by transform right/forward so you walk in the direction you are facing
        Vector3 moveDirection = (transform.right * _moveInput.x) + (transform.forward * _moveInput.y);
        
        // We use transform.position instead of Translate to avoid compounding rotations
        transform.position += moveDirection * (moveSpeed * Time.deltaTime);
    }
}