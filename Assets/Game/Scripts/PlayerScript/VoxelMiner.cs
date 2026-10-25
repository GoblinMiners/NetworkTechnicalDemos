using UnityEngine;
using Mirror;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

public class VoxelMiner : NetworkBehaviour
{
    [Header("Dependencies")]
    [SerializeField] private Transform cameraPivot;
    [SerializeField] private LayerMask terrainLayer=1;
    [Header("Mining Settings")]
    [SerializeField] private float reach=10f;
    [SerializeField] private float carveRadius=0.75f;
    [SerializeField] private float minePower=15f;
    [SerializeField] private float mineRate=0.1f;
    private float _mineTimer;
    private void Update()
    {
        if(!isLocalPlayer || World.Instance==null || !World.Instance.CanMine || cameraPivot==null)return;
        bool held=false;
#if ENABLE_INPUT_SYSTEM
        held=Mouse.current!=null && Mouse.current.leftButton.isPressed;
#elif ENABLE_LEGACY_INPUT_MANAGER
        held=Input.GetMouseButton(0);
#endif
        if(!held){_mineTimer=0;return;}
        float interval=Mathf.Max(0.03f,mineRate);_mineTimer+=Time.deltaTime;
        if(_mineTimer<interval)return;
        _mineTimer=Mathf.Min(_mineTimer-interval,interval);
        CmdMine(cameraPivot.forward);
    }
    [Command]
    private void CmdMine(Vector3 direction)=>MiningServerValidation.TryMine(this,cameraPivot,direction,reach,carveRadius,minePower,mineRate,terrainLayer);
}
