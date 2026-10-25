using System.Text;
using Mirror;
using UnityEngine;

/// <summary>Small replaceable test HUD. Uses actual owned inventory totals, not global collection events.</summary>
[RequireComponent(typeof(PlayerInventory))]
public sealed class MiningInventoryHUD : MonoBehaviour
{
    public bool showHUD=true;
    private PlayerInventory _inventory;
    private Camera _camera;
    private string _target="";
    private float _nextTargetCheck;
    private readonly StringBuilder _text=new StringBuilder();
    private void Awake()=>_inventory=GetComponent<PlayerInventory>();
    private void Update()
    {
        if(_inventory==null || !_inventory.isLocalPlayer || !showHUD || Time.unscaledTime<_nextTargetCheck)return;
        _nextTargetCheck=Time.unscaledTime+0.2f;
        if(_camera==null)_camera=GetComponentInChildren<Camera>();
        World world=World.Instance;
        _target=world!=null && world.IsReady?"Air":"Waiting for terrain";
        if(_camera==null || world==null || !world.IsReady)return;
        RaycastHit hit;
        if(Physics.Raycast(new Ray(_camera.transform.position,_camera.transform.forward),out hit,10f,1<<world.terrainLayer,QueryTriggerInteraction.Ignore))
        {
            var chunk=hit.collider.GetComponentInParent<Chunk>();
            if(chunk!=null)
            {
                var definition=world.MaterialCatalog.Get(chunk.GetHitMaterialId(hit.triangleIndex));
                if(definition!=null)_target=definition.displayName;
            }
        }
    }
    private void OnGUI()
    {
        if(!showHUD || _inventory==null || !_inventory.isLocalPlayer)return;
        _text.Clear();_text.Append("MINING INVENTORY\nTarget: ").Append(_target).Append('\n');
        foreach(var pair in _inventory.Quantities)
        {
            var item=_inventory.FindItem(pair.Key);
            _text.Append(item!=null?item.mineralName:pair.Key.ToString()).Append(": ").Append(pair.Value.ToString("0.##")).Append('\n');
        }
        if(_inventory.Quantities.Count==0)_text.Append("No resources collected yet.\n");
        GUI.Box(new Rect(12,12,320,Mathf.Max(100,65+_inventory.Quantities.Count*22)),_text.ToString());
    }
}
