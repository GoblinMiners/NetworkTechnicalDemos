using System;
using System.Collections.Generic;
using UnityEngine;
using Mirror;

public partial class World
{
    [Header("Materials and resources")]
    [SerializeField] private TerrainMaterialCatalog materialCatalog;
    [SerializeField] private GameObject resourcePickupPrefab;
    [Min(1)] public int maxVisibleResourcePickups=128;
    [Min(0.25f)] public float resourcePickupReach=2f;
    [Tooltip("Seconds between coherent terrain/resource snapshots. Zero disables periodic saves, not shutdown saves.")]
    [Min(0)] public float materialAutosaveSeconds=60f;
    [Tooltip("Use a separate filename for test worlds. File only, no directories.")]
    public string materialSaveFileName="world_materials_v1.dat";
    public bool importLegacyDensitySave=true;
    private string MaterialSavePath
    {
        get
        {
            if(string.IsNullOrWhiteSpace(materialSaveFileName) || System.IO.Path.GetFileName(materialSaveFileName)!=materialSaveFileName)
                throw new InvalidOperationException("Material save name must be a filename without directories.");
            return System.IO.Path.Combine(Application.persistentDataPath,materialSaveFileName);
        }
    }
    public TerrainMaterialCatalog MaterialCatalog=>materialCatalog;
    public int UncollectedResourcePiles=>_resourcePiles==null?0:_resourcePiles.Count;
    private TerrainMaterialGenerator _materials;
    private readonly Dictionary<Vector3Int,int> _materialCache=new Dictionary<Vector3Int,int>();
    private ResourcePileStore _resourcePiles;
    private readonly Dictionary<long,OrePickup> _resourceViews=new Dictionary<long,OrePickup>();
    private readonly HashSet<PlayerInventory> _inventories=new HashSet<PlayerInventory>();
    private float _nextResourceRefresh,_nextAutosave;
    private bool _materialWorldValid;
    private string _worldRulesSignature;

    private void ResolveMaterialAssets()
    {
        if(materialCatalog==null)materialCatalog=Resources.Load<TerrainMaterialCatalog>("Gobmine/TerrainMaterialCatalog");
        if(resourcePickupPrefab==null)resourcePickupPrefab=Resources.Load<GameObject>("Gobmine/OrePickup");
        if(materialCatalog==null)throw new InvalidOperationException("Gobmine terrain material catalogue is missing.");
        materialCatalog.ValidateOrThrow();
        if(resourcePickupPrefab==null || resourcePickupPrefab.GetComponent<OrePickup>()==null)
            throw new InvalidOperationException("Gobmine OrePickup prefab is missing or invalid.");
    }
    private bool InitializeMaterialWorld()
    {
        try
        {
            ResolveMaterialAssets();
            _worldRulesSignature=materialCatalog.RulesSignature();
            MaterialWorldPersistence.Snapshot saved; string error;
            bool loaded=MaterialWorldPersistence.TryLoad(MaterialSavePath,out saved,out error);
            if(!loaded && error!=null)throw new InvalidOperationException(error+" Back up/repair the save; no new world will overwrite it.");
            if(loaded)
            {
                if(saved.ChunkSize!=chunkSize || saved.VoxelSize!=voxelSize || saved.RulesSignature!=_worldRulesSignature)
                    throw new InvalidOperationException("Saved world rules differ from current dimensions/material IDs/yields/generation. Restore the original settings or move the material save AND its .bak aside for a new world.");
                seed=saved.Seed;
            }
            _terrain=new TerrainDensityStore(chunkSize,voxelSize);_densityReader=_terrain.Get;
            _materials=new TerrainMaterialGenerator(seed,materialCatalog);
            _resourcePiles=new ResourcePileStore();
            if(loaded)
            {
                foreach(var p in saved.Density) _terrain.Set(p.Position,p.Value);
                foreach(var p in saved.Resources)
                {
                    var definition=materialCatalog.Get(p.MaterialId);
                    if(definition==null || definition.inventoryItem==null || definition.inventoryItem.mineralID!=p.ItemId)
                        throw new InvalidOperationException("Saved resource item/material mapping is invalid.");
                }
                _resourcePiles.Import(saved.Resources);
            }
            else if(importLegacyDensitySave) _terrain.ImportLegacyChunks(WorldSaver.LoadWorld(chunkSize)); // Old save is read, never overwritten.
            _materialWorldValid=true;
            _nextAutosave=Time.unscaledTime+Mathf.Max(1f,materialAutosaveSeconds);
            return true;
        }
        catch(Exception ex) {Debug.LogError("[Gobmine Materials] "+ex);_materialWorldValid=false;return false;}
    }
    private void InitializeClientMaterials(int worldSeed,string signature)
    {
        ResolveMaterialAssets();
        if(materialCatalog.RulesSignature()!=signature)throw new InvalidOperationException("Host and client material catalogues differ. Install the same patch/assets on both.");
        _worldRulesSignature=signature;
        seed=worldSeed;_materialCache.Clear();_materials=new TerrainMaterialGenerator(seed,materialCatalog);
    }
    private void ResetMaterialRuntime()
    {
        foreach(var pair in _resourceViews)
            if(pair.Value!=null) { if(NetworkServer.active)NetworkServer.Destroy(pair.Value.gameObject);else Destroy(pair.Value.gameObject); }
        _resourceViews.Clear();_inventories.Clear();_resourcePiles=null;_materials=null;
        _materialWorldValid=false;_worldRulesSignature=null;_nextResourceRefresh=0f;_materialCache.Clear();
    }
    public int GetSolidMaterialId(Vector3Int cell)
    {
        if(_materials==null)return 1;
        int id;if(_materialCache.TryGetValue(cell,out id))return id;
        if(_materialCache.Count>=131072)_materialCache.Clear();
        id=_materials.GetSolidMaterial(cell);_materialCache[cell]=id;return id;
    }
    public TerrainCellInfo GetCellInfo(Vector3Int cell)
    {
        if(_terrain==null)throw new InvalidOperationException("Terrain not initialized; an unavailable world is not air.");
        TerrainCellState state=TerrainQuery.CellState(cell,_densityReader);
        return new TerrainCellInfo {Cell=cell,State=state,MaterialId=state==TerrainCellState.Air?TerrainMaterialCatalog.Air:GetSolidMaterialId(cell)};
    }
    public bool IsAirAt(Vector3 worldPosition)
    {
        if(_terrain==null)throw new InvalidOperationException("Terrain not initialized.");
        return TerrainQuery.SampleDensity(worldPosition/voxelSize,_densityReader)>=0;
    }
    public int GetMaterialAt(Vector3 worldPosition)=>IsAirAt(worldPosition)?TerrainMaterialCatalog.Air:GetSolidMaterialId(Vector3Int.FloorToInt(worldPosition/voxelSize));
    public MineralData GetInventoryItem(int itemId)=>materialCatalog==null?null:materialCatalog.GetItem(itemId);
    public Material GetMaterialForSurface(int id)=>materialCatalog==null?GetVoxelMaterial():materialCatalog.Surface(id);

    private float HardnessAtSample(Vector3Int p)
    {
        float hardness=0.05f;
        for(int x=0;x<=1;x++)for(int y=0;y<=1;y++)for(int z=0;z<=1;z++)
        {
            var d=materialCatalog.Get(GetSolidMaterialId(p-new Vector3Int(x,y,z)));
            if(d!=null)hardness=Mathf.Max(hardness,d.hardness);
        }
        return hardness; // Conservative hardest-touching-cell rule at interfaces.
    }
    private void RewardRemoval(Vector3Int cell,Vector3 centre,float cubicMetres,bool collapse,PlayerInventory miner)
    {
        int id=GetSolidMaterialId(cell);
        var d=materialCatalog.Get(id);
        QueueDebris(cell,centre,cubicMetres,id);
        if(d==null || d.inventoryItem==null || d.itemsPerCubicMetre<=0)return;
        double units=cubicMetres*d.itemsPerCubicMetre;
        if(!collapse && miner!=null && miner.isServer && miner.ServerAddUnits(d.inventoryItem.mineralID,units))return;
        _resourcePiles.Add(id,d.inventoryItem.mineralID,units,centre);
        // A disconnected miner does not cause queued edits to destroy their rewards.
    }
    public void RegisterInventory(PlayerInventory inventory) {if(inventory!=null)_inventories.Add(inventory);}
    public void PreserveDepartingInventory(PlayerInventory inventory)
    {
        if(inventory==null || !isServer || _resourcePiles==null || !_materialWorldValid)return;
        foreach(var pair in inventory.Quantities)
        {
            int material=MaterialForItem(pair.Key);
            if(material>0 && pair.Value>0)_resourcePiles.Add(material,pair.Key,pair.Value,inventory.transform.position+Vector3.up*0.4f);
        }
        inventory.Quantities.Clear();_inventories.Remove(inventory);
    }
    private int MaterialForItem(int item)
    {
        foreach(var d in materialCatalog.definitions)if(d.inventoryItem!=null && d.inventoryItem.mineralID==item)return d.materialId;
        return 0;
    }
    public void UpdateResourcePosition(long id,Vector3 position)
    {
        if(isServer && _resourcePiles!=null)_resourcePiles.UpdatePosition(id,position);
    }
    public bool TryCollectResource(long id,PlayerInventory inventory)
    {
        if(!isServer || !IsReady || inventory==null || !inventory.isServer || _resourcePiles==null)return false;
        ResourcePileStore.Pile pile;
        if(!_resourcePiles.TryGet(id,out pile) || Vector3.Distance(inventory.transform.position,pile.Position)>resourcePickupReach)return false;
        Vector3 eye=inventory.transform.position+Vector3.up*0.6f;
        if(Physics.Linecast(eye,pile.Position,1<<terrainLayer,QueryTriggerInteraction.Ignore))return false;
        if(!inventory.ServerAddUnits(pile.ItemId,pile.Units))return false;
        // Main-thread operation: removal follows successful credit with no yield/await.
        _resourcePiles.Remove(id,out pile);
        OrePickup view;
        if(_resourceViews.TryGetValue(id,out view)) {if(view!=null)NetworkServer.Destroy(view.gameObject);_resourceViews.Remove(id);}
        return true;
    }
    private void UpdateMaterialWorld()
    {
        if(!isServer || !_materialWorldValid || !IsReady)return;
        if(materialAutosaveSeconds>0 && Time.unscaledTime>=_nextAutosave)
        { SaveMaterialWorld();_nextAutosave=Time.unscaledTime+Mathf.Max(1f,materialAutosaveSeconds); }
        if(Time.unscaledTime<_nextResourceRefresh)return;
        _nextResourceRefresh=Time.unscaledTime+0.5f;
        var nearest=new List<ResourcePileStore.Pile>();
        foreach(var p in _resourcePiles.All)
            if(_activeChunks.ContainsKey(GetChunkCoordFromPosition(p.Position)) && !_dirtyMeshes.Contains(GetChunkCoordFromPosition(p.Position)) && NearestPlayerDistance(p.Position)<40f*40f)nearest.Add(p);
        nearest.Sort((a,b)=>NearestPlayerDistance(a.Position).CompareTo(NearestPlayerDistance(b.Position)));
        int visibleLimit=Mathf.Max(1,maxVisibleResourcePickups);
        if(nearest.Count>visibleLimit)nearest.RemoveRange(visibleLimit,nearest.Count-visibleLimit);
        var wanted=new HashSet<long>();foreach(var p in nearest)wanted.Add(p.Id);
        var remove=new List<long>();
        foreach(var pair in _resourceViews)if(!wanted.Contains(pair.Key)||pair.Value==null)
        {if(pair.Value!=null)NetworkServer.Destroy(pair.Value.gameObject);remove.Add(pair.Key);}
        foreach(long id in remove)_resourceViews.Remove(id);
        foreach(var p in nearest)
        {
            OrePickup view;
            if(!_resourceViews.TryGetValue(p.Id,out view))
            {
                var obj=Instantiate(resourcePickupPrefab,p.Position,Quaternion.identity);
                view=obj.GetComponent<OrePickup>();view.Configure(this,p);
                _resourceViews[p.Id]=view;NetworkServer.Spawn(obj);
            }
            else view.Refresh(p);
        }
    }
    private float NearestPlayerDistance(Vector3 p)
    {
        float result=float.PositiveInfinity;
        foreach(var t in _connectedPlayers)if(t!=null)result=Mathf.Min(result,(t.position-p).sqrMagnitude);
        return result;
    }
    public bool SaveWorldNow() => isServer && SaveMaterialWorld();
    private bool SaveMaterialWorld()
    {
        if(!_materialWorldValid || _terrain==null)return false;
        try
        {
            if(materialCatalog.RulesSignature()!=_worldRulesSignature)
                throw new InvalidOperationException("Material rules changed while the world was running. Revert them before saving; use a new save slot for different generation or reward rules.");
            var s=new MaterialWorldPersistence.Snapshot {Seed=seed,ChunkSize=chunkSize,VoxelSize=voxelSize,RulesSignature=_worldRulesSignature};
            foreach(var p in _terrain.CopyOverrides())s.Density.Add(new MaterialWorldPersistence.DensityPoint {Position=p.Key,Value=p.Value});
            foreach(var view in _resourceViews.Values)if(view!=null)_resourcePiles.UpdatePosition(view.pileId,view.transform.position);
            s.Resources=_resourcePiles.Copy();
            // No account/authenticator exists in the upload. Snapshot carried items as
            // recoverable world bundles, never assign inventories using an untrusted client ID.
            foreach(var inventory in _inventories)
            {
                if(inventory==null || !inventory.isServer)continue;
                foreach(var p in inventory.Quantities)
                {
                    int material=MaterialForItem(p.Key);
                    if(material>0 && p.Value>0)s.Resources.Add(new ResourcePileStore.Pile {MaterialId=material,ItemId=p.Key,Units=p.Value,Position=inventory.transform.position+Vector3.up*0.4f});
                }
            }
            MaterialWorldPersistence.Write(MaterialSavePath,s);return true;
        }
        catch(Exception ex){Debug.LogError("[Gobmine Material Save] "+ex);return false;}
    }
}
