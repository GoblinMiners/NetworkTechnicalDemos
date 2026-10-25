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
        base.OnStartLocalPlayer();
        Debug.Log("[PlayerMovement] OnStartLocalPlayer triggered successfully.");

        if (localCamera != null) localCamera.SetActive(true);
        if (localCinemachine != null) localCinemachine.SetActive(true);

        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;

        _controls = new PlayerControls();
        
        _controls.Player.Move.performed += ctx => {
            _moveInput = ctx.ReadValue<Vector2>();
            
        };
        _controls.Player.Move.canceled += ctx => _moveInput = Vector2.zero;

        _controls.Player.Look.performed += ctx => _lookInput = ctx.ReadValue<Vector2>();
        _controls.Player.Look.canceled += ctx => _lookInput = Vector2.zero;

        _controls.Enable();
    }

    public override void OnStopLocalPlayer()
    {
        if (_controls != null) _controls.Disable();
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
        base.OnStopLocalPlayer();
    }

    private void Update()
    {
        if (!isLocalPlayer || World.Instance == null || !World.Instance.IsReady) return;

        // 1. Mouse Look
        transform.Rotate(Vector3.up * (_lookInput.x * lookSensitivity));

        _verticalPitch -= _lookInput.y * lookSensitivity;
        _verticalPitch = Mathf.Clamp(_verticalPitch, -89f, 89f);
        
        if (cameraPivot != null)
        {
            cameraPivot.localRotation = Quaternion.Euler(_verticalPitch, 0f, 0f);
        }

        // 2. Movement
        Vector3 moveDirection = (transform.right * _moveInput.x) + (transform.forward * _moveInput.y);
        
        if (moveDirection.sqrMagnitude > 0.01f)
        {
           
        }

        transform.position += moveDirection * (moveSpeed * Time.deltaTime);
    }
}