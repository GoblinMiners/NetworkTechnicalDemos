using Mirror;
using UnityEngine;

// Alternate Input System tool: enable this OR VoxelMiner, not both.
public class PlayerMiner : NetworkBehaviour
{
    public float mineRadius=2.5f,minePower=15f,reach=10f,mineInterval=0.1f;
    public Transform cameraPivot;
    public LayerMask terrainLayer=1;
    private PlayerControls _controls;
    private bool _isMining;
    private float _timer;
    public override void OnStartLocalPlayer()
    {
        base.OnStartLocalPlayer();
        _controls=new PlayerControls();
        _controls.Player.Mine.performed+=ctx=>_isMining=true;
        _controls.Player.Mine.canceled+=ctx=>_isMining=false;
        _controls.Enable();
    }
    public override void OnStopLocalPlayer()
    {
        if(_controls!=null){_controls.Disable();_controls.Dispose();_controls=null;}
        _isMining=false;base.OnStopLocalPlayer();
    }
    private void Update()
    {
        if (GobminePlayerEquipment.LocalMenuOpen) return;
        if(!isLocalPlayer || cameraPivot==null || World.Instance==null || !World.Instance.CanMine)return;
        if(!_isMining){_timer=0;return;}
        float interval=Mathf.Max(0.03f,mineInterval);_timer+=Time.deltaTime;
        if(_timer<interval)return;
        _timer=Mathf.Min(_timer-interval,interval);CmdMine(cameraPivot.forward);
    }
    [Command]
    private void CmdMine(Vector3 direction)=>MiningServerValidation.TryMine(this,cameraPivot,direction,reach,mineRadius,minePower,mineInterval,terrainLayer);
}
