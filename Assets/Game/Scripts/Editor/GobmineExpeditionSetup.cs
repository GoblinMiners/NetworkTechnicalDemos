#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using Mirror;
using kcp2k;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

public static class GobmineExpeditionSetup
{
    private const string Menu="Tools/Gobmine/Expedition/";
    private const string Folder="Assets/Game/Expedition";
    private const string SettingsPath=Folder+"/ExpeditionSettings.asset";
    private const string CatalogPath=Folder+"/ExpeditionCatalog.asset";
    private const string PlayerPath="Assets/Game/Prefabs/Player.prefab";

    [MenuItem(Menu+"1 - Create Assets and Install Player Components")]
    public static void InstallPlayerAndAssets()
    {
        if(EditorApplication.isPlaying)throw new InvalidOperationException("Stop Play mode first.");
        CreateAssets();
        var prefab=AssetDatabase.LoadAssetAtPath<GameObject>(PlayerPath);
        if(prefab==null)throw new InvalidOperationException("Missing "+PlayerPath);
        if(prefab.GetComponent<CharacterController>()==null)
        {
            UnityEngine.Object selection=Selection.activeObject;Selection.activeObject=prefab;
            GobmineMovementTools.Install();Selection.activeObject=selection;
            prefab=AssetDatabase.LoadAssetAtPath<GameObject>(PlayerPath);
            if(prefab.GetComponent<CharacterController>()==null)throw new InvalidOperationException("The CharacterController migration was not completed. Install movement before expedition components.");
        }
        if(!EditorUtility.DisplayDialog("Install expedition components?","Backs up Player.prefab outside Assets, then adds Health, Equipment and Vitals only where missing. Existing movement, camera and mining assignments are retained. Install on both server and client projects.","Back up and install","Cancel"))return;
        GameObject root=PrefabUtility.LoadPrefabContents(PlayerPath);
        try
        {
            foreach(Transform child in root.GetComponentsInChildren<Transform>(true))
                if(GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(child.gameObject)>0)
                    throw new InvalidOperationException("Missing script on "+child.name+". Repair it first; prefab not saved.");
            if(root.GetComponent<NetworkIdentity>()==null || root.GetComponent<PlayerMovement>()==null || root.GetComponent<PlayerInventory>()==null)
                throw new InvalidOperationException("Player requires NetworkIdentity, PlayerMovement and PlayerInventory.");
            if(root.GetComponent<Rigidbody>()!=null && !root.GetComponent<Rigidbody>().isKinematic)
                throw new InvalidOperationException("Player still has a dynamic Rigidbody. Run the movement installer first.");
            string backupFolder=Path.Combine(Directory.GetParent(Application.dataPath).FullName,"GobmineBackups");Directory.CreateDirectory(backupFolder);
            string backup=Path.Combine(backupFolder,DateTime.Now.ToString("yyyyMMdd-HHmmss-fff")+"-Expedition-Player.prefab");
            File.Copy(PlayerPath,backup);if(File.Exists(PlayerPath+".meta"))File.Copy(PlayerPath+".meta",backup+".meta");
            if(root.GetComponent<GobminePlayerHealth>()==null)root.AddComponent<GobminePlayerHealth>();
            if(root.GetComponent<GobminePlayerEquipment>()==null)root.AddComponent<GobminePlayerEquipment>();
            if(root.GetComponent<GobminePlayerVitals>()==null)root.AddComponent<GobminePlayerVitals>();
            if(PrefabUtility.SaveAsPrefabAsset(root,PlayerPath)==null)throw new InvalidOperationException("Unity did not save the prefab. Check Console; backup: "+backup);
            Debug.Log("[Gobmine Expedition] Installed player components. Backup: "+backup+". Create the expedition test scene next; your normal scenes are unchanged.");
        }
        finally{PrefabUtility.UnloadPrefabContents(root);}
        AssetDatabase.SaveAssets();
    }

    private static void CreateAssets()
    {
        Directory.CreateDirectory(Folder);AssetDatabase.Refresh();
        var source=Resources.Load<TerrainMaterialCatalog>("Gobmine/TerrainMaterialCatalog");
        if(source==null)throw new InvalidOperationException("Original Gobmine material catalogue is missing. Install the materials assets first.");
        source.ValidateOrThrow();
        var settings=AssetDatabase.LoadAssetAtPath<GobmineExpeditionSettings>(SettingsPath);
        var catalogue=AssetDatabase.LoadAssetAtPath<TerrainMaterialCatalog>(CatalogPath);
        if(settings!=null && catalogue!=null){settings.ValidateOrThrow(0.25f);catalogue.ValidateOrThrow();return;}
        Shader fluidShader=Shader.Find("Gobmine/Expedition Fluid URP");Shader oreShader=Shader.Find("Gobmine/Terrain Ore URP");
        if(fluidShader==null || oreShader==null)throw new InvalidOperationException("Expedition fluid / terrain ore shader missing. Allow Unity to finish importing.");
        if(settings==null)
        {
            settings=ScriptableObject.CreateInstance<GobmineExpeditionSettings>();
            settings.waterMaterial=FluidMaterial("Water",fluidShader,new Color(0.04f,0.32f,0.58f,0.70f),0f);
            settings.lavaMaterial=FluidMaterial("Lava",fluidShader,new Color(1f,0.16f,0.015f,1f),1.4f);
            AssetDatabase.CreateAsset(settings,SettingsPath);
        }
        if(catalogue==null)
        {
            var defs=new List<TerrainMaterialDefinition>();
            foreach(var original in source.definitions)
            {
                string path=Folder+"/"+original.name+"_Expedition.asset";
                var copy=AssetDatabase.LoadAssetAtPath<TerrainMaterialDefinition>(path);
                if(copy==null)
                {
                    copy=UnityEngine.Object.Instantiate(original);copy.name=original.name+"_Expedition";
                    copy.useDepthBand=true;copy.minimumDepthMetres=0;copy.maximumDepthMetres=100000;
                    copy.minimumCellY=-10000000;copy.maximumCellY=10000000;
                    if(original.materialId==3){copy.minimumDepthMetres=20;copy.requiredToolTier=1;}
                    if(original.materialId==4){copy.minimumDepthMetres=60;copy.requiredToolTier=2;}
                    AssetDatabase.CreateAsset(copy,path);
                }
                defs.Add(copy);
            }
            foreach(var d in defs)if(d.materialId==5 || d.materialId==6 || (d.inventoryItem!=null && (d.inventoryItem.mineralID==5 || d.inventoryItem.mineralID==6)))
                throw new InvalidOperationException("Material/item IDs 5 and 6 are already used in this custom catalogue. Choose unused IDs in the installer before proceeding; nothing will be renumbered automatically.");
            defs.Add(NewOre("Uranium Ore",5,120,3,new Color(0.3f,0.8f,0.06f,1f),oreShader,true));
            defs.Add(NewOre("Deep Crystal",6,220,4,new Color(0.3f,0.25f,0.9f,1f),oreShader,false));
            catalogue=ScriptableObject.CreateInstance<TerrainMaterialCatalog>();catalogue.baseStoneId=source.baseStoneId;catalogue.definitions=defs.ToArray();
            AssetDatabase.CreateAsset(catalogue,CatalogPath);
            settings.recipes=Recipes();EditorUtility.SetDirty(settings);
        }
        catalogue.ValidateOrThrow();settings.ValidateOrThrow(0.25f);AssetDatabase.SaveAssets();
        Debug.Log("[Gobmine Expedition] Created separate catalogue/profile in "+Folder+". Original material definitions have NOT been edited.");
    }
    private static Material FluidMaterial(string name,Shader shader,Color color,float glow)
    {
        string path=Folder+"/"+name+".mat";var m=AssetDatabase.LoadAssetAtPath<Material>(path);if(m!=null)return m;
        m=new Material(shader){name=name};m.SetColor("_BaseColor",color);m.SetFloat("_Glow",glow);m.SetFloat("_ZWrite",glow>0?1f:0f);AssetDatabase.CreateAsset(m,path);return m;
    }
    private static TerrainMaterialDefinition NewOre(string name,int id,float depth,int tier,Color color,Shader shader,bool radioactive)
    {
        string prefix=Folder+"/"+name.Replace(" ","");
        var d=AssetDatabase.LoadAssetAtPath<TerrainMaterialDefinition>(prefix+"Definition.asset");if(d!=null)return d;
        var item=AssetDatabase.LoadAssetAtPath<MineralData>(prefix+"Item.asset");
        if(item==null){item=ScriptableObject.CreateInstance<MineralData>();item.mineralName=name;item.mineralID=id;item.baseValue=radioactive?250:500;item.displayColor=color;AssetDatabase.CreateAsset(item,prefix+"Item.asset");}
        Material surface=AssetDatabase.LoadAssetAtPath<Material>(prefix+"Surface.mat");
        if(surface==null){surface=new Material(shader){name=name+" Surface"};surface.SetColor("_BaseColor",new Color(0.22f,0.23f,0.25f,1));surface.SetColor("_OreColor",color);surface.SetFloat("_VeinAmount",0.25f);AssetDatabase.CreateAsset(surface,prefix+"Surface.mat");}
        Material debris=AssetDatabase.LoadAssetAtPath<Material>(prefix+"Debris.mat");
        if(debris==null){debris=new Material(surface){name=name+" Rubble"};debris.SetFloat("_WorldSpace",0f);AssetDatabase.CreateAsset(debris,prefix+"Debris.mat");}
        d=ScriptableObject.CreateInstance<TerrainMaterialDefinition>();d.materialId=id;d.displayName=name;d.surfaceMaterial=surface;d.debrisMaterial=debris;d.inventoryItem=item;
        d.hardness=radioactive?3f:4f;d.itemsPerCubicMetre=radioactive?6:4;d.generateDeposits=true;d.depositGridSizeInCells=48;d.depositChancePerThousand=radioactive?450:300;
        d.minimumRadiusInCells=4;d.maximumRadiusInCells=9;d.minimumCellY=-10000000;d.maximumCellY=10000000;
        d.useDepthBand=true;d.minimumDepthMetres=depth;d.maximumDepthMetres=100000;d.requiredToolTier=tier;d.radiationRate=radioactive?1f:0f;
        d.requireRadiationSuit=radioactive;d.requireShieldedContainer=radioactive;AssetDatabase.CreateAsset(d,prefix+"Definition.asset");return d;
    }
    private static GobmineExpeditionSettings.ResearchRecipe Recipe(string label,GobmineResearch unlock,GobmineResearch prior,params int[] costs)
    {
        var recipe=new GobmineExpeditionSettings.ResearchRecipe{label=label,unlock=unlock,prerequisite=prior,costs=new GobmineExpeditionSettings.Cost[costs.Length/2]};
        for(int i=0;i<recipe.costs.Length;i++)recipe.costs[i]=new GobmineExpeditionSettings.Cost{itemId=costs[i*2],amount=costs[i*2+1]};return recipe;
    }
    private static GobmineExpeditionSettings.ResearchRecipe[] Recipes()=>new[]{
        Recipe("Tier 1 tool - silver",GobmineResearch.Tool1,0,1,50,2,20),
        Recipe("Tier 2 tool - gold",GobmineResearch.Tool2,GobmineResearch.Tool1,2,40,3,20),
        Recipe("Tier 3 tool - uranium",GobmineResearch.Tool3,GobmineResearch.Tool2,3,30,4,15),
        Recipe("Tier 4 tool - deep crystal",GobmineResearch.Tool4,GobmineResearch.Tool3,4,30,5,10),
        Recipe("Radiation suit",GobmineResearch.RadiationSuit,0,3,20,4,10),
        Recipe("Shielded ore container",GobmineResearch.ShieldedContainer,0,2,30,3,15),
        Recipe("Heat-resistant suit (70% reduction)",GobmineResearch.HeatSuit,0,4,20,5,20),
        Recipe("Air tank (90 seconds)",GobmineResearch.AirTank,0,2,20,3,10),
        Recipe("Impact boots (50% fall reduction)",GobmineResearch.ImpactBoots,0,1,40,2,20)};

    [MenuItem(Menu+"2 - Create Isolated Expedition Test Scene")]
    public static void CreateTestScene()
    {
        if(EditorApplication.isPlaying)throw new InvalidOperationException("Stop Play mode first.");
        CreateAssets();ValidatePlayer();
        if(!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())return;
        string path=Folder+"/GobmineExpeditionTest.unity";
        if(File.Exists(path) && !EditorUtility.DisplayDialog("Replace test scene?","Only the expedition test scene is replaced. Existing save files are not erased.","Replace","Cancel"))return;
        GameObject player=AssetDatabase.LoadAssetAtPath<GameObject>(PlayerPath),debris=AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Game/Prefabs/Debris.prefab"),pickup=Resources.Load<GameObject>("Gobmine/OrePickup");
        if(debris==null || pickup==null)throw new InvalidOperationException("Debris and OrePickup prefabs are required.");
        var scene=EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
        var net=new GameObject("Expedition Test Network");var transport=net.AddComponent<KcpTransport>();var manager=net.AddComponent<NetworkManager>();
        manager.transport=transport;manager.playerPrefab=player;manager.networkAddress="localhost";manager.maxConnections=4;manager.autoCreatePlayer=true;
        manager.spawnPrefabs.Add(debris);manager.spawnPrefabs.Add(pickup);net.AddComponent<NetworkManagerHUD>();
        var world=new GameObject("World").AddComponent<World>();world.customSeed=1729;world.viewDistanceInChunks=4;world.sleepDistanceInChunks=6;world.chunkBuildsPerFrame=1;
        world.densityChangesPerFrame=1024;world.debrisBucketSizeInCells=4;world.debrisSpawnsPerFrame=4;world.maxVisibleResourcePickups=32;
        ConfigureWorld(world,"gobmine_expedition_TEST_v1.dat");
        var pool=new GameObject("Debris Pool").AddComponent<DebrisPool>();var poolData=new SerializedObject(pool);
        poolData.FindProperty("debrisPrefab").objectReferenceValue=debris;
        SetIfPresent(poolData,"initialPoolSize",64);SetIfPresent(poolData,"maxActiveDebris",128);
        var lifetime=poolData.FindProperty("autoDespawnTime");if(lifetime!=null)lifetime.floatValue=8;
        var recycle=poolData.FindProperty("recycleOldestWhenFull");if(recycle!=null)recycle.boolValue=true;
        var clientPool=poolData.FindProperty("poolRemoteClients");if(clientPool!=null)clientPool.boolValue=true;
        poolData.ApplyModifiedPropertiesWithoutUndo();
        var gm=new GameObject("Game State");gm.AddComponent<NetworkIdentity>();gm.AddComponent<GameManager>().currentGameState=GameState.Exploring;
        new GameObject("Player Spawn").AddComponent<NetworkStartPosition>().transform.position=new Vector3(0,-2,0);
        var light=new GameObject("Directional Light").AddComponent<Light>();light.type=LightType.Directional;light.intensity=1.2f;light.transform.rotation=Quaternion.Euler(50,-30,0);
        RenderSettings.ambientMode=AmbientMode.Flat;RenderSettings.ambientLight=new Color(0.35f,0.35f,0.35f);
        EditorSceneManager.MarkSceneDirty(scene);EditorSceneManager.SaveScene(scene,path);AssetDatabase.SaveAssets();
        Debug.Log("[Gobmine Expedition] Created "+path+". Enter Play mode and choose Host. F2 opens research. This scene uses gobmine_expedition_TEST_v1.dat, not the original world save.");
    }
    private static void SetIfPresent(SerializedObject obj,string name,int value){var p=obj.FindProperty(name);if(p!=null)p.intValue=value;}
    private static void ConfigureWorld(World world,string save)
    {
        Undo.RecordObject(world,"Configure expedition");
        world.expeditionSettings=AssetDatabase.LoadAssetAtPath<GobmineExpeditionSettings>(SettingsPath);
        world.expeditionSettings.ValidateOrThrow(world.voxelSize);
        var serialized=new SerializedObject(world);serialized.FindProperty("materialCatalog").objectReferenceValue=AssetDatabase.LoadAssetAtPath<TerrainMaterialCatalog>(CatalogPath);serialized.ApplyModifiedProperties();
        world.materialSaveFileName=save;world.importLegacyDensitySave=false;world.ceilingCollapseThicknessMetres=1f;world.collapseDamagesPlayers=true;
        world.GetComponent<NetworkIdentity>().visibility=Visibility.ForceShown;EditorUtility.SetDirty(world);
    }
    [MenuItem(Menu+"Configure Current Scene for NEW Expedition World")]
    public static void ConfigureScene()
    {
        if(EditorApplication.isPlaying)throw new InvalidOperationException("Stop Play mode first.");CreateAssets();ValidatePlayer();
        World[] worlds=UnityEngine.Object.FindObjectsByType<World>(FindObjectsInactive.Include,FindObjectsSortMode.None);
        if(worlds.Length!=1)throw new InvalidOperationException("Open a scene containing exactly one World.");
        if(!EditorUtility.DisplayDialog("Switch this scene to a new expedition world?","Assigns the separate expedition catalogue/profile and world_expedition_v1.dat. Your old save remains untouched; existing tunnels are NOT migrated. Back up the scene first.","Use new world","Cancel"))return;
        ConfigureWorld(worlds[0],"world_expedition_v1.dat");EditorSceneManager.MarkSceneDirty(worlds[0].gameObject.scene);
        Debug.Log("[Gobmine Expedition] Scene configured. Save the scene. Original world saves are untouched.");
    }
    private static void ValidatePlayer()
    {
        var p=AssetDatabase.LoadAssetAtPath<GameObject>(PlayerPath);
        if(p==null || p.GetComponent<GobminePlayerVitals>()==null || p.GetComponent<GobminePlayerEquipment>()==null || p.GetComponent<CharacterController>()==null)
            throw new InvalidOperationException("Run '1 - Create Assets and Install Player Components' first.");
    }
    [MenuItem(Menu+"Validate Expedition Setup")]
    public static void Validate()
    {
        ValidatePlayer();
        var profile=AssetDatabase.LoadAssetAtPath<GobmineExpeditionSettings>(SettingsPath);var catalogue=AssetDatabase.LoadAssetAtPath<TerrainMaterialCatalog>(CatalogPath);
        if(profile==null || catalogue==null)throw new InvalidOperationException("Create expedition assets first.");profile.ValidateOrThrow(0.25f);catalogue.ValidateOrThrow();
        if(profile.waterMaterial==null || profile.lavaMaterial==null)throw new InvalidOperationException("Fluid materials missing.");
        foreach(var r in profile.recipes)foreach(var c in r.costs)if(catalogue.GetItem(c.itemId)==null)throw new InvalidOperationException("Recipe references missing item "+c.itemId);
        Shader fluid=Shader.Find("Gobmine/Expedition Fluid URP");if(fluid==null)throw new InvalidOperationException("Fluid shader missing.");
        foreach(var m in ShaderUtil.GetShaderMessages(fluid))if(m.severity.ToString()=="Error")throw new InvalidOperationException("Fluid shader: "+m.message);
        foreach(var w in UnityEngine.Object.FindObjectsByType<World>(FindObjectsInactive.Include,FindObjectsSortMode.None))
        {
            if(w.transform.position!=Vector3.zero || w.transform.rotation!=Quaternion.identity || w.transform.lossyScale!=Vector3.one)throw new InvalidOperationException("World transform must be zero / identity / unit scale.");
            if(w.ExpeditionEnabled){w.expeditionSettings.ValidateOrThrow(w.voxelSize);if(w.importLegacyDensitySave)Debug.LogWarning("Legacy import is ignored for expedition worlds; disable its checkbox.");}
        }
        Debug.Log("[Gobmine Expedition] Asset/setup checks passed. This does not replace host/client, physics or performance testing.");
    }
    [MenuItem(Menu+"Log Runtime State")]
    public static void LogState()
    {
        var w=World.Instance;if(w==null)throw new InvalidOperationException("Start a host first.");Debug.Log(w.ExpeditionStatus());
        foreach(var p in UnityEngine.Object.FindObjectsByType<GobminePlayerVitals>(FindObjectsInactive.Exclude,FindObjectsSortMode.None))
            Debug.Log($"[Gobmine Expedition] player={p.netId}; server={p.isServer}; position={p.transform.position}; oxygen={p.Oxygen:0.0}; dose={p.RadiationDose:0.0}; last fall={p.LastFallMetres:0.00}m",p);
    }
    private static World TestHost()
    {
        World w=World.Instance;
        if(!EditorApplication.isPlaying || !NetworkServer.active || NetworkClient.localPlayer==null || w==null || !w.IsReady || !w.ExpeditionEnabled || !w.materialSaveFileName.Contains("_TEST_"))
            throw new InvalidOperationException("These cheats run ONLY on a host in a save filename containing _TEST_.");
        return w;
    }
    [MenuItem(Menu+"Test Only - Grant All Research")]
    public static void GrantResearch(){var w=TestHost();w.ServerGrantExpeditionResearchForTesting(511);Debug.Log("[Gobmine Expedition] Test-world research granted. This is not available as a client command.");}
    [MenuItem(Menu+"Test Only - Teleport Host to Dry Cavern")]
    public static void Dry()=>Teleport(GobmineFluidKind.None);
    [MenuItem(Menu+"Test Only - Teleport Host to Water Cavern")]
    public static void Water()=>Teleport(GobmineFluidKind.Water);
    [MenuItem(Menu+"Test Only - Teleport Host to Lava Cavern")]
    public static void Lava()=>Teleport(GobmineFluidKind.Lava);
    [MenuItem(Menu+"Test Only - Return Host to Workshop and Restore Health")]
    public static void ReturnHost()
    {
        World w=TestHost();GameObject player=NetworkClient.localPlayer.gameObject;
        var motor=player.GetComponent<PlayerMovement>();var controller=player.GetComponent<CharacterController>();
        bool enabled=motor.enabled;motor.enabled=false;controller.enabled=false;player.transform.position=w.ExpeditionProfile.workshopPosition;
        controller.enabled=true;motor.enabled=enabled;player.GetComponent<GobminePlayerHealth>().ServerRestoreHealthForTesting();Physics.SyncTransforms();
    }
    private static void Teleport(GobmineFluidKind kind)
    {
        World w=TestHost();var generator=new GobmineCavernGenerator(w.seed,w.voxelSize,w.ExpeditionProfile);GobmineCavernGenerator.Chamber found=default;bool has=false;
        for(int y=-1;y>=-24 && !has;y--)for(int x=-2;x<=2 && !has;x++)for(int z=-2;z<=2;z++)
        {var cave=generator.AtGrid(new Vector3Int(x,y,z));if(cave.Exists && cave.Fluid==kind){found=cave;has=true;break;}}
        if(!has)throw new InvalidOperationException("No matching chamber found in this small search; use another seed/profile.");
        GameObject player=NetworkClient.localPlayer.gameObject;var motor=player.GetComponent<PlayerMovement>();var controller=player.GetComponent<CharacterController>();
        bool enabled=motor.enabled;motor.enabled=false;controller.enabled=false;player.transform.position=found.Centre;controller.enabled=true;motor.enabled=enabled;
        var vitals=player.GetComponent<GobminePlayerVitals>();vitals.ServerResetAfterTeleport();Physics.SyncTransforms();
        Debug.Log($"[Gobmine Expedition] Host teleported to {kind} chamber {found.Centre}; depth={w.DepthAt(found.Centre):0.0}m; fluid level={found.FluidLevel:0.00}. Nearby collision loading may pause movement; this test teleport itself causes no fall damage.");
    }
}
#endif
