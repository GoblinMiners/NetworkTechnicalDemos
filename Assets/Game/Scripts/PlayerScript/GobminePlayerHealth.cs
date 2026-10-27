using System;
using Mirror;
using UnityEngine;

[DisallowMultipleComponent]
[RequireComponent(typeof(NetworkIdentity))]
public sealed class GobminePlayerHealth : NetworkBehaviour
{
    [Min(1f)] public float maximumHealth=100f;
    public bool showHealthHUD=true;
    [SyncVar] private float currentHealth=100f;
    [SyncVar] private GobmineDamageType lastDamageType;
    private readonly float[] _nextDamageTime=new float[5];
    private Collider _bodyCollider;
    public float CurrentHealth=>currentHealth;
    public bool IsDead=>currentHealth<=0f;
    public GobmineDamageType LastDamageType=>lastDamageType;
    public event Action<GobminePlayerHealth> ServerDied;
    private void Awake(){_bodyCollider=GetComponent<Collider>();}
    public Bounds DamageBounds=>_bodyCollider!=null && _bodyCollider.enabled?_bodyCollider.bounds:new Bounds(transform.position,new Vector3(0.8f,1.8f,0.8f));
    public override void OnStartServer(){base.OnStartServer();currentHealth=Mathf.Max(1f,maximumHealth);Array.Clear(_nextDamageTime,0,_nextDamageTime.Length);}
    [Server]
    public bool ServerApplyCollapseDamage(float amount,float cooldownSeconds)=>ServerApplyDamage(GobmineDamageType.Collapse,amount,cooldownSeconds);
    [Server]
    public bool ServerApplyDamage(GobmineDamageType type,float amount,float cooldownSeconds=0f)
    {
        int index=(int)type;
        if(!isServer || IsDead || index<0 || index>=_nextDamageTime.Length || !GobmineExpeditionRules.Finite(amount) || amount<=0f || Time.time<_nextDamageTime[index])return false;
        World world=World.Instance;
        if(world!=null && world.ExpeditionEnabled)
        {
            int flags=world.ExpeditionUnlocks;var s=world.ExpeditionProfile;
            if(type==GobmineDamageType.Fall && GobmineExpeditionRules.Has(flags,GobmineResearch.ImpactBoots))amount*=1f-Mathf.Clamp01(s.impactBootReduction);
            if(type==GobmineDamageType.Heat && GobmineExpeditionRules.Has(flags,GobmineResearch.HeatSuit))amount*=1f-Mathf.Clamp01(s.heatSuitReduction);
        }
        _nextDamageTime[index]=Time.time+Mathf.Max(0f,cooldownSeconds);
        lastDamageType=type;currentHealth=Mathf.Max(0f,currentHealth-amount);
        if(IsDead)ServerDied?.Invoke(this);return true;
    }
    [ContextMenu("Server / Restore Health (test only)")]
    public void ServerRestoreHealthForTesting()
    {
        if(!NetworkServer.active || !isServer)return;
        currentHealth=Mathf.Max(1f,maximumHealth);Array.Clear(_nextDamageTime,0,_nextDamageTime.Length);
        var vitals=GetComponent<GobminePlayerVitals>();if(vitals!=null)vitals.ServerResetAfterTeleport();
    }
    private void OnGUI()
    {
        if(!showHealthHUD || !isLocalPlayer)return;
        GUI.Box(new Rect(Mathf.Max(12f,Screen.width-242f),12f,230f,58f),IsDead?"HEALTH: 0\nDowned - server respawn required":$"HEALTH: {currentHealth:0} / {maximumHealth:0}\nLast damage: {lastDamageType}");
    }
}
