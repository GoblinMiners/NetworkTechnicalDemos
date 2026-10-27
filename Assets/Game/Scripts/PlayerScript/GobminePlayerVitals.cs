using Mirror;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Server observes body positions/terrain contacts for fall damage. It never
/// accepts a client-provided damage amount. Movement itself is still the project's
/// client-authoritative motor, so this is NOT a complete movement anti-cheat.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(NetworkIdentity),typeof(GobminePlayerHealth),typeof(PlayerInventory))]
public sealed class GobminePlayerVitals:NetworkBehaviour
{
    [SyncVar] private float oxygen=30f;
    [SyncVar] private float radiationDose;
    [SyncVar] private float radiationRate;
    [SyncVar] private bool submerged;
    private GobminePlayerHealth _health;
    private PlayerInventory _inventory;
    private PlayerMovement _motor;
    private CharacterController _controller;
    private readonly GobmineFallTracker _fall=new GobmineFallTracker();
    private readonly RaycastHit[] _groundHits=new RaycastHit[32];
    private float _nextTick,_lastTick;
    public float Oxygen=>oxygen;
    public float RadiationDose=>radiationDose;
    public float LastFallMetres=>_fall.LastDrop;
    private void Awake(){_health=GetComponent<GobminePlayerHealth>();_inventory=GetComponent<PlayerInventory>();_motor=GetComponent<PlayerMovement>();_controller=GetComponent<CharacterController>();}
    public override void OnStartServer(){base.OnStartServer();ServerResetAfterTeleport();}
    [Server]
    public void ServerResetAfterTeleport()
    {
        _fall.Reset(_health.DamageBounds.center.y);
        radiationDose=0;oxygen=MaxOxygen();_lastTick=Time.time;_nextTick=Time.time+0.2f;
    }
    private float MaxOxygen()
    {
        var w=World.Instance;if(w==null || !w.ExpeditionEnabled)return 30f;
        return GobmineExpeditionRules.Has(w.ExpeditionUnlocks,GobmineResearch.AirTank)?w.ExpeditionProfile.tankOxygenSeconds:w.ExpeditionProfile.oxygenSeconds;
    }
    [Server]
    public void ServerAddExposure(float amount)
    {
        World world=World.Instance;if(world==null || !world.ExpeditionEnabled || !GobmineExpeditionRules.Finite(amount) || amount<=0f)return;
        if(GobmineExpeditionRules.Has(world.ExpeditionUnlocks,GobmineResearch.RadiationSuit))amount*=1f-world.ExpeditionProfile.radiationSuitReduction;
        radiationDose=Mathf.Clamp(radiationDose+amount,0f,100f);
    }
    private void Update()
    {
        if(!isServer || Time.time<_nextTick)return;
        float dt=Mathf.Clamp(Time.time-_lastTick,0f,0.5f);_lastTick=Time.time;_nextTick=Time.time+0.1f;
        var world=World.Instance;
        if(world==null || !world.IsReady || !world.ExpeditionEnabled || _health.IsDead)return;
        var s=world.ExpeditionProfile;Bounds body=_health.DamageBounds;
        Vector3 feet=new Vector3(body.center.x,body.min.y+0.08f,body.center.z);
        Vector3 head=new Vector3(body.center.x,body.max.y-0.12f,body.center.z);
        GobmineFluidKind middle=world.FluidAt(body.center),bottom=world.FluidAt(feet),top=world.FluidAt(head);
        bool water=middle==GobmineFluidKind.Water;
        submerged=top==GobmineFluidKind.Water;
        oxygen=Mathf.Clamp(oxygen+(submerged?-dt:dt*5f),0f,MaxOxygen());
        if(submerged && oxygen<=0f)_health.ServerApplyDamage(GobmineDamageType.Drowning,s.drowningDamagePerSecond*dt);
        if(middle==GobmineFluidKind.Lava || bottom==GobmineFluidKind.Lava || top==GobmineFluidKind.Lava)
            _health.ServerApplyDamage(GobmineDamageType.Heat,s.lavaDamagePerSecond*dt);
        radiationRate=world.RadiationAt(body.center,_inventory);
        if(radiationRate>0f)ServerAddExposure(radiationRate*dt);
        else radiationDose=Mathf.Max(0f,radiationDose-s.doseRecoveryPerSecond*dt);
        if(radiationDose>25f)_health.ServerApplyDamage(GobmineDamageType.Radiation,s.radiationDamageAtFullDose*Mathf.Clamp01((radiationDose-25f)/75f)*dt);
        // Missing terrain is not a landing or an endlessly growing airborne timer.
        Bounds safe=body;safe.Expand(0.5f);string reason;
        bool ready=world.TryPreparePlayerCollision(safe,out reason);
        bool grounded=ready && Grounded(body);
        float drop;
        bool landed=_fall.Sample(body.center.y,grounded || water,!ready,dt,out drop);
        if(landed && drop>s.safeFallMetres)
        {
            float damage=GobmineExpeditionRules.FallDamage(drop,_motor!=null?_motor.gravity:-24f,_motor!=null?_motor.terminalFallSpeed:25f,
                s.safeFallMetres,s.lethalFallMetres,_health.maximumHealth);
            if(water || bottom==GobmineFluidKind.Water)damage*=GobmineExpeditionRules.WaterImpactMultiplier(world.WaterDepthBelow(water?body.center:feet),s.waterCushionDepthMetres);
            _health.ServerApplyDamage(GobmineDamageType.Fall,damage);
        }
    }
    private bool Grounded(Bounds body)
    {
        float radius=_controller!=null?Mathf.Max(0.05f,_controller.radius*0.8f):0.25f;
        Vector3 origin=new Vector3(body.center.x,body.min.y+radius+0.08f,body.center.z);
        int count=gameObject.scene.GetPhysicsScene().SphereCast(origin,radius,Vector3.down,_groundHits,0.22f,~0,QueryTriggerInteraction.Ignore);
        if(count==_groundHits.Length)return false;
        for(int i=0;i<count;i++)
        {
            var hit=_groundHits[i];if(hit.collider==null || hit.collider.transform.IsChildOf(transform) || hit.normal.y<0.4f)continue;
            if(Physics.GetIgnoreLayerCollision(gameObject.layer,hit.collider.gameObject.layer))continue;
            return true;
        }
        return false;
    }
    private void OnGUI()
    {
        if(!isLocalPlayer || World.Instance==null || !World.Instance.ExpeditionEnabled)return;
        GUI.Box(new Rect(Mathf.Max(12f,Screen.width-262f),78,250,98),$"Oxygen: {oxygen:0.0}s{(submerged?" (submerged)":"")}\nRadiation dose: {radiationDose:0.0}/100\nExposure: {radiationRate:0.0}/s\nSpace: swim up | Left Ctrl: dive");
    }
}
