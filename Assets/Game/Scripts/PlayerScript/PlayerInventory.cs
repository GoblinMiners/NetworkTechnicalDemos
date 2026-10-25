using System;
using System.Collections.Generic;
using UnityEngine;
using Mirror;

/// <summary>Server-owned quantities, including fractional mining yield. Only the owner receives them.</summary>
public class PlayerInventory : NetworkBehaviour
{
    [SerializeField] private List<MineralData> allMineralsDatabase = new List<MineralData>();
    public readonly SyncDictionary<int,double> Quantities = new SyncDictionary<int,double>();
    public static PlayerInventory Local { get; private set; }
    public event Action Changed;
    internal float NextServerMiningTime;

    private void Awake() { syncMode=SyncMode.Owner; }
    public double GetQuantity(int itemId) { double n; return Quantities.TryGetValue(itemId,out n)?n:0; }
    public long GetWholeQuantity(int itemId) => (long)Math.Floor(GetQuantity(itemId));
    public MineralData FindItem(int id)
    {
        if(World.Instance!=null) { MineralData d=World.Instance.GetInventoryItem(id); if(d!=null)return d; }
        return allMineralsDatabase.Find(d=>d!=null && d.mineralID==id);
    }
    public void AddMineral(MineralData mineral,int amount=1)
    {
        if(!isServer || mineral==null || amount<=0)return;
        if(!allMineralsDatabase.Contains(mineral))allMineralsDatabase.Add(mineral);
        ServerAddUnits(mineral.mineralID,amount);
    }
    [Server]
    public bool ServerAddUnits(int itemId,double units)
    {
        if(itemId<=0 || units<=0 || double.IsNaN(units)||double.IsInfinity(units)||FindItem(itemId)==null)return false;
        if(World.Instance!=null && World.Instance.MaterialCatalog!=null && World.Instance.GetInventoryItem(itemId)==null)return false;
        double previous=GetQuantity(itemId), total=previous+units;
        if(double.IsInfinity(total) || total>1e12)return false;
        Quantities[itemId]=total;
        long wholeAdded=(long)Math.Floor(total)-(long)Math.Floor(previous);
        if(wholeAdded>0 && connectionToClient!=null)
            TargetCollected(connectionToClient,itemId,(int)Math.Min(int.MaxValue,wholeAdded));
        return true;
    }
    [TargetRpc]
    private void TargetCollected(NetworkConnectionToClient target,int id,int amount)
    {
        MineralData data=FindItem(id);
        if(data!=null)GameEvents.TriggerMineralCollected(data,amount);
    }
    public override void OnStartClient()
    {
        base.OnStartClient();
        Quantities.OnAdd+=OnAdd; Quantities.OnSet+=OnSet; Quantities.OnRemove+=OnRemove; Quantities.OnClear+=OnClear;
    }
    public override void OnStartLocalPlayer() { base.OnStartLocalPlayer(); Local=this; Changed?.Invoke(); }
    public override void OnStopLocalPlayer() { if(Local==this)Local=null; base.OnStopLocalPlayer(); }
    public override void OnStopClient()
    {
        Quantities.OnAdd-=OnAdd; Quantities.OnSet-=OnSet; Quantities.OnRemove-=OnRemove; Quantities.OnClear-=OnClear;
        base.OnStopClient();
    }
    public override void OnStopServer()
    {
        // Without authenticated persistent account IDs, keep loot in the world rather
        // than trusting a client-selected save key or deleting a departing player's ore.
        if(World.Instance!=null)World.Instance.PreserveDepartingInventory(this);
        base.OnStopServer();
    }
    private void OnAdd(int id)=>Changed?.Invoke();
    private void OnSet(int id,double previous)=>Changed?.Invoke();
    private void OnRemove(int id,double previous)=>Changed?.Invoke();
    private void OnClear()=>Changed?.Invoke();
}
