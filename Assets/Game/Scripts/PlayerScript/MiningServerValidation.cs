using Mirror;
using UnityEngine;

/// <summary>Called only from owned player Commands. Settings are read from the server's tool.</summary>
public static class MiningServerValidation
{
    public static void TryMine(NetworkBehaviour tool,Transform pivot,Vector3 direction,float reach,
        float radius,float power,float interval,LayerMask layers)
    {
        World world=World.Instance;
        if(!tool.isServer || !tool.enabled || world==null || !world.CanMine || tool.connectionToClient==null ||
            tool.connectionToClient.identity!=tool.netIdentity || !Finite(direction.x)||!Finite(direction.y)||!Finite(direction.z))return;
        if(direction.sqrMagnitude<0.25f || direction.sqrMagnitude>4f)return;
        var health=tool.GetComponent<GobminePlayerHealth>();
        if(health!=null && health.IsDead)return;
        var inventory=tool.GetComponent<PlayerInventory>();
        if(inventory==null || Time.unscaledTime<inventory.NextServerMiningTime)return;
        float cooldown=Mathf.Clamp(interval,0.03f,2f);
        inventory.NextServerMiningTime=Time.unscaledTime+cooldown;
        Vector3 origin=pivot!=null?pivot.position:tool.transform.position+Vector3.up*1.6f;
        if(Vector3.Distance(origin,tool.transform.position)>3f)return;
        RaycastHit hit;
        if(!Physics.Raycast(new Ray(origin,direction.normalized),out hit,Mathf.Min(reach,world.maxMineReach),layers,QueryTriggerInteraction.Ignore) ||
            hit.collider.GetComponentInParent<Chunk>()==null)return;
        world.QueuePlayerMining(inventory,hit.point,Mathf.Min(radius,world.maxMineRadius),Mathf.Min(power*cooldown,world.maxMineAmount));
    }
    private static bool Finite(float f)=>!float.IsNaN(f)&&!float.IsInfinity(f);
}
