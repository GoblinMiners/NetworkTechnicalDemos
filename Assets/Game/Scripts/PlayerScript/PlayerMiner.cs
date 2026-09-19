using Mirror;
using UnityEngine;

public class PlayerMiner : NetworkBehaviour
{
    public float mineRadius = 2.5f;
    public float minePower = 15f;
    public float reach = 10f;
    
    public Transform cameraPivot;
    private PlayerControls _controls;
    private bool _isMining = false;

    public override void OnStartLocalPlayer()
    {
        _controls = new PlayerControls();
        
        // Set mining state based on the button press phase
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
        // Ensure only the local player can fire their own laser
        if (!isLocalPlayer) return;

        if (_isMining)
        {
            MineRock();
        }
    }

    private void MineRock()
    {
        // Fire the laser from the camera pivot so it shoots exactly where you look (up/down)
        Ray ray = new Ray(cameraPivot.position, cameraPivot.forward); 
        
        if (Physics.Raycast(ray, out RaycastHit hit, reach))
        {
            World.Instance.RequestModifyTerrain(hit.point, mineRadius, minePower * Time.deltaTime);
        }
    }
}
