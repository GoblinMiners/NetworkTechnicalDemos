using UnityEngine;
using Mirror;

public class VoxelMiner : NetworkBehaviour
{
    [Header("Dependencies")]
    public Transform cameraPivot;
    public LayerMask terrainLayer; // Assign to Default or your Terrain layer

    [Header("Mining Settings")]
    public float reach = 10f;
    public float carveRadius = 2.5f;
    public float minePower = 15f;
    public float mineRate = 0.1f; // Mines 10 times a second to prevent network spam

    private PlayerControls _controls;
    private bool _isMining = false;
    private float _nextMineTime = 0f;

    public override void OnStartLocalPlayer()
    {
        _controls = new PlayerControls();
        
        _controls.Player.Mine.performed += ctx => _isMining = true;
        _controls.Player.Mine.canceled += ctx => _isMining = false;
        
        _controls.Enable();
    }

    public override void OnStopLocalPlayer()
    {
        if (_controls != null) _controls.Disable();
    }

    private void Update()
    {
        if (!isLocalPlayer) return;

        // Apply the network cooldown so Mirror doesn't block the client
        if (_isMining && Time.time >= _nextMineTime)
        {
            _nextMineTime = Time.time + mineRate;
            MineRock();
        }
    }

    private void MineRock()
    {
        Ray ray = new Ray(cameraPivot.position, cameraPivot.forward); 
        
        if (Physics.Raycast(ray, out RaycastHit hit, reach, terrainLayer))
        {
            if (World.Instance != null)
            {
                // Call your working World script directly! 
                // We pass (minePower * mineRate) to simulate Time.deltaTime accurately on a fixed timer
                World.Instance.RequestModifyTerrain(hit.point, carveRadius, minePower * mineRate);
            }
            else
            {
                Debug.LogError("World.Instance is missing! Make sure your World script is in the scene.");
            }
        }
    }
}