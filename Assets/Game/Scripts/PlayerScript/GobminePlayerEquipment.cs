using System.Text;
using Mirror;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>Shared research is server-owned; these are derived equipment capabilities, not client grants.</summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(NetworkIdentity), typeof(PlayerInventory))]
public sealed class GobminePlayerEquipment : NetworkBehaviour
{
    [SyncVar] private int unlocked;
    private PlayerInventory _inventory;
    private bool _menu;
    private string _message;
    private float _messageUntil, _nextServerMessage, _nextResearchCommand;
    public static bool LocalMenuOpen { get; private set; }
    public int ToolTier => GobmineExpeditionRules.ToolTier(unlocked);
    public bool Has(GobmineResearch bit) => GobmineExpeditionRules.Has(unlocked,bit);
    private void Awake() { _inventory=GetComponent<PlayerInventory>(); }
    private void Update()
    {
        World world=World.Instance;
        if(isServer)unlocked=world!=null ? world.ExpeditionUnlocks : 0;
        if(!isLocalPlayer || world==null || !world.ExpeditionEnabled)return;
        if(Keyboard.current!=null && Keyboard.current.f2Key.wasPressedThisFrame)
        {
            _menu=!_menu;LocalMenuOpen=_menu;
            Cursor.lockState=_menu?CursorLockMode.None:CursorLockMode.Locked;Cursor.visible=_menu;
        }
    }
    public override void OnStopLocalPlayer() { LocalMenuOpen=false;_menu=false;base.OnStopLocalPlayer(); }
    private void OnDisable() { if(isLocalPlayer)LocalMenuOpen=false; }
    [Command]
    private void CmdResearch(int index)
    {
        if(Time.unscaledTime<_nextResearchCommand)return;
        _nextResearchCommand=Time.unscaledTime+0.35f;
        string message="World is unavailable.";
        if(World.Instance!=null)World.Instance.TryPurchaseResearch(_inventory,index,out message);
        ServerMessage(message);
    }
    [Server]
    public void ServerMessage(string message)
    {
        if(string.IsNullOrEmpty(message) || Time.unscaledTime<_nextServerMessage || connectionToClient==null)return;
        _nextServerMessage=Time.unscaledTime+0.5f;TargetMessage(connectionToClient,message);
    }
    [TargetRpc]
    private void TargetMessage(NetworkConnectionToClient target,string message)
    { _message=message;_messageUntil=Time.unscaledTime+4f; }
    private void OnGUI()
    {
        World world=World.Instance;
        if(!isLocalPlayer || world==null || !world.ExpeditionEnabled)return;
        GUI.Box(new Rect(12,Screen.height-74,450,60),$"Depth {world.DepthAt(transform.position):0.0} m | Tool tier {ToolTier}\nF2: expedition equipment / shared research");
        if(Time.unscaledTime<_messageUntil)GUI.Box(new Rect(12,Screen.height-116,620,38),_message);
        if(!_menu)return;
        var settings=world.ExpeditionProfile;
        GUILayout.BeginArea(new Rect(20,100,Mathf.Min(650,Screen.width-40),Mathf.Min(600,Screen.height-210)),GUI.skin.box);
        GUILayout.Label("EXPEDITION RESEARCH - shared, permanent world unlocks");
        GUILayout.Label("Spend your minerals at the starting workshop. All players equip researched gear automatically.");
        GUILayout.Label($"Workshop distance: {Vector3.Distance(transform.position,settings.workshopPosition):0.0} m (reach {settings.workshopReach:0.0} m)");
        for(int i=0;i<settings.recipes.Length;i++)
        {
            var r=settings.recipes[i];var text=new StringBuilder(r.label);
            text.Append(Has(r.unlock)?"  [UNLOCKED]":"  ");
            if(!Has(r.unlock))foreach(var c in r.costs)
            { var item=_inventory.FindItem(c.itemId);text.Append(c.amount).Append(' ').Append(item!=null?item.mineralName:c.itemId.ToString()).Append("; "); }
            GUI.enabled=!Has(r.unlock);
            if(GUILayout.Button(text.ToString()))CmdResearch(i);
        }
        GUI.enabled=true;GUILayout.Label("F2 closes this menu. Research cannot be purchased from deep underground.");
        GUILayout.EndArea();
    }
}
