using UnityEngine;
using Mirror;

/// <summary>A physics pickup view of a persistent server resource record, not its owner.</summary>
[RequireComponent(typeof(NetworkIdentity),typeof(SphereCollider),typeof(Rigidbody))]
public sealed class OrePickup : NetworkBehaviour
{
    [SyncVar] public long pileId;
    [SyncVar(hook=nameof(MaterialChanged))] public int materialId;
    [SyncVar] public int itemId;
    [SyncVar] public double units;
    [SyncVar(hook=nameof(PositionChanged))] private Vector3 _position;
    private World _world;
    private float _nextPoseSync;
    private Vector3 _targetPosition;
    private float _nextCollectionTime;

    public void Configure(World world,ResourcePileStore.Pile p)
    {
        _world=world; pileId=p.Id; materialId=p.MaterialId;itemId=p.ItemId;units=p.Units;
        _position=p.Position; transform.position=p.Position; ApplyMaterial();
    }
    public override void OnStartServer()
    {
        base.OnStartServer();
        _nextCollectionTime=Time.unscaledTime+(pileId%3)*0.05f;
        GetComponent<SphereCollider>().isTrigger=true;
        Rigidbody body=GetComponent<Rigidbody>();body.isKinematic=false;body.useGravity=true;
        body.constraints=RigidbodyConstraints.FreezeRotation;
        body.collisionDetectionMode=CollisionDetectionMode.ContinuousDynamic;
    }
    public override void OnStartClient()
    {
        base.OnStartClient();transform.position=_position;_targetPosition=_position;ApplyMaterial();
        if(!isServer)GetComponent<Rigidbody>().isKinematic=true;
    }
    public void Refresh(ResourcePileStore.Pile p) {materialId=p.MaterialId;itemId=p.ItemId;units=p.Units;}
    private void FixedUpdate()
    {
        if(!isServer || _world==null)return;
        _world.UpdateResourcePosition(pileId,transform.position);
        if(Time.unscaledTime>=_nextPoseSync) {_position=transform.position;_nextPoseSync=Time.unscaledTime+0.1f;}
    }
    private void Update()
    {
        if(isServer)
        {
            if(_world!=null && Time.unscaledTime>=_nextCollectionTime)
            {
                _nextCollectionTime=Time.unscaledTime+ResourcePickupRules.CollectionIntervalSeconds;
                _world.TryCollectNearbyResource(pileId);
            }
            return;
        }
        if(isClient)transform.position=Vector3.Lerp(transform.position,_targetPosition,1f-Mathf.Exp(-18f*Time.deltaTime));
    }
    public override void OnStopServer()
    {
        if(_world!=null)_world.UpdateResourcePosition(pileId,transform.position);
        base.OnStopServer();
    }
    private void MaterialChanged(int oldId,int newId)=>ApplyMaterial();
    private void PositionChanged(Vector3 oldPosition,Vector3 newPosition)=>_targetPosition=newPosition;
    private void ApplyMaterial()
    {
        var catalogue=World.Instance!=null?World.Instance.MaterialCatalog:Resources.Load<TerrainMaterialCatalog>("Gobmine/TerrainMaterialCatalog");
        if(catalogue==null)return;
        var renderer=GetComponentInChildren<Renderer>();
        if(renderer!=null)renderer.sharedMaterial=catalogue.Debris(materialId);
    }
    // Collection is checked by the server at the configured World reach, not by
    // trigger overlap. A rock pile cannot prevent a nearby resource from trying.
    // The trigger is kept on the prefab for compatibility; no prefab edits needed.
}
