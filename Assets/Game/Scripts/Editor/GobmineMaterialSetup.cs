#if UNITY_EDITOR
using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using Mirror;
using kcp2k;

public static class GobmineMaterialSetup
{
    private const string PlayerPath="Assets/Game/Prefabs/Player.prefab";
    private const string DebrisPath="Assets/Game/Prefabs/Debris.prefab";
    [MenuItem("Tools/Gobmine/Materials/Validate Scene and Prefabs")]
    public static void ValidateSetup()
    {
        int errors=0;
        var catalogue=Resources.Load<TerrainMaterialCatalog>("Gobmine/TerrainMaterialCatalog");
        try {if(catalogue==null)throw new Exception("Catalogue asset is missing.");catalogue.ValidateOrThrow();}
        catch(Exception ex){Debug.LogError(ex.Message);errors++;}
        GameObject debris=AssetDatabase.LoadAssetAtPath<GameObject>(DebrisPath);
        if(debris==null || debris.GetComponent<DebrisCleanup>()==null || debris.GetComponent<Rigidbody>()==null || debris.GetComponent<NetworkIdentity>()==null)
        {Debug.LogError("Debris prefab needs NetworkIdentity, Rigidbody and DebrisCleanup.");errors++;}
        else
        {
            var nt=debris.GetComponent<NetworkTransformReliable>();
            if(nt==null || nt.syncDirection!=SyncDirection.ServerToClient){Debug.LogError("Debris NetworkTransform must be Server To Client.");errors++;}
        }
        var pickup=Resources.Load<GameObject>("Gobmine/OrePickup");
        if(pickup==null || pickup.GetComponent<OrePickup>()==null || pickup.GetComponent<NetworkIdentity>()==null || pickup.GetComponent<Rigidbody>()==null || pickup.GetComponent<BoxCollider>()==null)
        {Debug.LogError("OrePickup prefab needs its pickup script, Rigidbody and solid BoxCollider.");errors++;}
        else if(pickup.GetComponent<NetworkIdentity>().assetId==0){Debug.LogError("OrePickup has no Mirror asset ID. Reimport and save the prefab in Unity.");errors++;}
        var player=AssetDatabase.LoadAssetAtPath<GameObject>(PlayerPath);
        if(player==null || player.GetComponent<PlayerInventory>()==null){Debug.LogError("Player prefab / inventory missing.");errors++;}
        else
        {
            if(player.GetComponent<VoxelMiner>()!=null && player.GetComponent<PlayerMiner>()!=null && player.GetComponent<VoxelMiner>().enabled && player.GetComponent<PlayerMiner>().enabled)
            {Debug.LogError("Enable only one mining component on the player.");errors++;}
            if(player.GetComponent<PlayerInventory>().syncMode!=SyncMode.Owner){Debug.LogError("Inventory Network Sync Mode should be Owner.");errors++;}
        }
        Shader shader=Shader.Find("Gobmine/Terrain Ore URP");
        if(shader==null){Debug.LogError("Terrain shader is missing.");errors++;}
        else foreach(var message in ShaderUtil.GetShaderMessages(shader))
            if(message.severity.ToString()=="Error"){Debug.LogError("Terrain shader: "+message.message);errors++;}
        if(GraphicsSettings.currentRenderPipeline==null)Debug.LogWarning("No active render pipeline asset. The supplied shader requires URP; check Graphics/Quality settings and the original pipeline assets.");
        foreach(World world in UnityEngine.Object.FindObjectsByType<World>(FindObjectsInactive.Include,FindObjectsSortMode.None))
        {
            if(world.transform.position!=Vector3.zero || world.transform.rotation!=Quaternion.identity || world.transform.lossyScale!=Vector3.one)
            {Debug.LogError("World transform must have position 0, rotation 0 and scale 1.");errors++;}
            if(world.GetComponent<NetworkIdentity>().visibility!=Visibility.ForceShown)
                Debug.LogWarning("Set the World NetworkIdentity Visibility to Force Shown when using interest management.");
        }
        Debug.Log($"[Gobmine Setup] Validation finished: {errors} error(s). Shader, runtime physics and networking still need play-mode tests.");
        if(errors>0)throw new Exception("Gobmine setup validation failed. See the Console.");
    }

    [MenuItem("Tools/Gobmine/Materials/Create Isolated Mining Test Scene")]
    public static void CreateTestScene()
    {
        if(EditorApplication.isPlaying)throw new InvalidOperationException("Stop play mode first.");
        if(!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())return;
        const string scenePath="Assets/Game/Scene/GobmineMaterialTest.unity";
        if(File.Exists(scenePath) && !EditorUtility.DisplayDialog("Replace test scene?","Only GobmineMaterialTest.unity will be replaced. World saves and normal game scenes are not changed.","Replace","Cancel"))return;
        GameObject player=AssetDatabase.LoadAssetAtPath<GameObject>(PlayerPath),debris=AssetDatabase.LoadAssetAtPath<GameObject>(DebrisPath);
        var pickup=Resources.Load<GameObject>("Gobmine/OrePickup");
        if(player==null || debris==null || pickup==null)throw new InvalidOperationException("Install the prefabs/assets before creating the test scene.");
        var scene=EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
        var networkObject=new GameObject("Test Network Manager");
        var transport=networkObject.AddComponent<KcpTransport>();
        var manager=networkObject.AddComponent<NetworkManager>();manager.transport=transport;manager.playerPrefab=player;
        manager.networkAddress="localhost";manager.maxConnections=4;manager.autoCreatePlayer=true;
        manager.spawnPrefabs.Add(debris);manager.spawnPrefabs.Add(pickup);networkObject.AddComponent<NetworkManagerHUD>();
        var worldObject=new GameObject("World");var world=worldObject.AddComponent<World>();
        world.customSeed=1729;world.viewDistanceInChunks=4;world.sleepDistanceInChunks=6;
        world.materialSaveFileName="gobmine_material_TEST_v1.dat";world.importLegacyDensitySave=false;
        world.minCeilingThickness=2;world.requireExploringToMine=true;
        world.GetComponent<NetworkIdentity>().visibility=Visibility.ForceShown;
        var pool=new GameObject("Debris Pool").AddComponent<DebrisPool>();
        var poolSettings=new SerializedObject(pool);poolSettings.FindProperty("debrisPrefab").objectReferenceValue=debris;poolSettings.ApplyModifiedPropertiesWithoutUndo();
        var gameObject=new GameObject("Game State");gameObject.AddComponent<NetworkIdentity>();gameObject.AddComponent<GameManager>().currentGameState=GameState.Exploring;
        new GameObject("Player Spawn").AddComponent<NetworkStartPosition>().transform.position=new Vector3(0,-2,0);
        var light=new GameObject("Test Directional Light").AddComponent<Light>();light.type=LightType.Directional;light.intensity=1.2f;light.transform.rotation=Quaternion.Euler(50,-30,0);
        RenderSettings.ambientMode=AmbientMode.Flat;RenderSettings.ambientLight=new Color(0.35f,0.35f,0.35f);
        EditorSceneManager.MarkSceneDirty(scene);EditorSceneManager.SaveScene(scene,scenePath);AssetDatabase.SaveAssets();
        Debug.Log("Created "+scenePath+". Enter play mode and click Host. This scene has its OWN save file. Add it to a test build to run a second client.");
    }

    [MenuItem("Tools/Gobmine/Materials/Save Running World Now")]
    public static void SaveRunningWorld()
    {
        if(World.Instance==null || !World.Instance.isServer || !World.Instance.SaveWorldNow())
            throw new InvalidOperationException("Run a host/server with a valid material world first.");
        Debug.Log("Material world saved to "+Application.persistentDataPath);
    }
}
#endif
