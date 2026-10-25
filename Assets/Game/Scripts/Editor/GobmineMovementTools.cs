#if UNITY_EDITOR
using System;
using System.IO;
using Mirror;
using UnityEditor;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>Editor-only installer. Do not attach to a GameObject.</summary>
public static class GobmineMovementTools
{
    private const string Menu = "Tools/Gobmine/Movement/";

    [MenuItem(Menu + "Install Player Controller")]
    public static void Install()
    {
        if (EditorApplication.isPlaying) { Debug.LogError("Stop Play mode before installing the player controller."); return; }
        string path = "Assets/Game/Prefabs/Player.prefab";
        GameObject selected = Selection.activeObject as GameObject;
        if (selected != null && AssetDatabase.Contains(selected) && selected.GetComponent<PlayerMovement>() != null)
            path = AssetDatabase.GetAssetPath(selected);
        if (AssetDatabase.LoadAssetAtPath<GameObject>(path) == null)
        { Debug.LogError("Select the Player prefab containing PlayerMovement in the Project window, then run this command."); return; }
        if (!EditorUtility.DisplayDialog("Install collision-aware movement?",
            "Updates " + path + ": adds/configures CharacterController; removes the ROOT solid CapsuleCollider and ROOT Rigidbody; " +
            "preserves camera/mining/health components and child objects; assigns your existing input asset without editing bindings. " +
            "Keeps the player's NetworkTransform Client To Server (local-authority movement). A backup is saved outside Assets. " +
            "Prefabs with missing scripts, joints, NetworkRigidbody, or an active NavMeshAgent are rejected. Update all builds together.",
            "Back up and install", "Cancel")) return;
        GameObject root = PrefabUtility.LoadPrefabContents(path);
        try
        {
            foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
                if (GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(child.gameObject) > 0)
                    throw new InvalidOperationException("Missing script on " + child.name + ". Repair it first; no changes were saved.");
            PlayerMovement movement = root.GetComponent<PlayerMovement>();
            if (movement == null) throw new InvalidOperationException("The prefab ROOT must contain your existing PlayerMovement component.");
            if (root.GetComponents<PlayerMovement>().Length != 1) throw new InvalidOperationException("Keep only one PlayerMovement component.");
            if (root.GetComponent<Joint>() != null) throw new InvalidOperationException("Root joints need a dedicated physics-controller migration; not modified.");
            if (Vector3.Distance(root.transform.localScale, Vector3.one) > 0.001f ||
                Mathf.Abs(Vector3.Dot(root.transform.up, Vector3.up) - 1f) > 0.001f)
                throw new InvalidOperationException("Use an upright, unit-scale player root; scale/tilt only a visual child.");
            NetworkBehaviour transformSync = null;
            foreach (Component component in root.GetComponents<Component>())
            {
                string type = component.GetType().Name;
                if (type.Contains("NetworkRigidbody")) throw new InvalidOperationException("Remove/migrate NetworkRigidbody before using a CharacterController.");
                if (type == "NavMeshAgent" && component is Behaviour behaviour && behaviour.enabled)
                    throw new InvalidOperationException("An active NavMeshAgent is another position writer. Disable it before installing this manual player controller.");
                if (component is NetworkBehaviour network && type.StartsWith("NetworkTransform", StringComparison.Ordinal))
                {
                    if (transformSync != null) throw new InvalidOperationException("More than one root NetworkTransform found.");
                    transformSync = network;
                }
            }
            if (transformSync == null) throw new InvalidOperationException("Keep/add one Mirror NetworkTransform on the player root before installing.");
            CharacterController controller = root.GetComponent<CharacterController>();
            CapsuleCollider capsule = null;
            foreach (Collider collider in root.GetComponents<Collider>())
            {
                if (collider == controller || collider.isTrigger) continue;
                if (!(collider is CapsuleCollider c) || c.direction != 1 || capsule != null)
                    throw new InvalidOperationException("Unexpected extra root solid collider. This installer migrates one upright CapsuleCollider only.");
                capsule = c;
            }
            string backupFolder = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "GobmineBackups");
            Directory.CreateDirectory(backupFolder);
            string backup = Path.Combine(backupFolder, DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "-Movement-" + Path.GetFileName(path));
            File.Copy(path, backup, false);
            if (File.Exists(path + ".meta")) File.Copy(path + ".meta", backup + ".meta", false);
            if (controller == null)
            {
                controller = root.AddComponent<CharacterController>();
                controller.radius = capsule != null ? capsule.radius : 0.5f;
                controller.height = capsule != null ? capsule.height : 2f;
                controller.center = capsule != null ? capsule.center : Vector3.zero;
            }
            // Root movement has exactly one collision shape. Trigger sensors are kept.
            if (capsule != null) UnityEngine.Object.DestroyImmediate(capsule);
            Rigidbody body = root.GetComponent<Rigidbody>();
            if (body != null) UnityEngine.Object.DestroyImmediate(body);
            controller.enabled = true; controller.detectCollisions = true; controller.enableOverlapRecovery = true;
            controller.skinWidth = Mathf.Max(0.02f, controller.radius * 0.1f);
            controller.minMoveDistance = 0f; controller.slopeLimit = 50f;
            controller.stepOffset = Mathf.Min(0.3f, controller.height * 0.2f);
            controller.includeLayers = 0; controller.excludeLayers = 0;
            movement.enabled = true; movement.jumpHeight = 1.1f; movement.gravity = -24f;
            movement.terminalFallSpeed = 25f; movement.jumpBufferSeconds = 0.12f; movement.coyoteSeconds = 0.1f;
            movement.groundSnapDistance = 0.15f; movement.maximumMotorDeltaTime = 0.05f;
            movement.guardTerrainStreaming = true; movement.rollbackSolidPenetration = true;
            if (movement.inputActions == null)
                movement.inputActions = AssetDatabase.LoadAssetAtPath<InputActionAsset>("Assets/Game/Input/PlayerControls.inputactions");
            var sync = new SerializedObject(transformSync);
            SerializedProperty direction = sync.FindProperty("syncDirection");
            if (direction == null) throw new InvalidOperationException("NetworkTransform has no syncDirection field; inspect the installed Mirror version.");
            direction.intValue = (int)SyncDirection.ClientToServer;
            SerializedProperty fixedUpdate = sync.FindProperty("useFixedUpdate");
            if (fixedUpdate != null) fixedUpdate.boolValue = false;
            SerializedProperty target = sync.FindProperty("target");
            if (target != null) target.objectReferenceValue = root.transform;
            sync.ApplyModifiedPropertiesWithoutUndo();
            bool saved;
            PrefabUtility.SaveAsPrefabAsset(root, path, out saved);
            if (!saved) throw new InvalidOperationException("Unity refused to save. Restore/inspect backup: " + backup);
            Debug.Log("[Gobmine Movement] Installed on " + path + ". Backup: " + backup +
                ". Existing Move/Look/camera fields were retained. Jump=1.1m, gravity=-24, step=0.3m, slope=50 degrees. No scenes, bindings or saves overwritten.");
            InputAction jump = movement.inputActions != null ? movement.inputActions.FindAction("Player/Jump", false) : null;
            if (jump == null || jump.bindings.Count == 0)
                Debug.LogWarning("[Gobmine Movement] Bind Player/Jump to <Keyboard>/space (Button). Runtime Space/gamepad fallback is available. Generated PlayerControls.cs is not used by this motor.");
            foreach (World world in UnityEngine.Object.FindObjectsByType<World>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (Physics.GetIgnoreLayerCollision(root.layer, world.terrainLayer))
                    Debug.LogError("[Gobmine Movement] Player and terrain layers do not collide in Project Settings > Physics. Enable that pair before testing.", world);
        }
        catch (Exception ex) { Debug.LogError("[Gobmine Movement] " + ex.Message); }
        finally { PrefabUtility.UnloadPrefabContents(root); }
    }

    [MenuItem(Menu + "Log Movement and Collision State")]
    public static void LogState()
    {
        if (!EditorApplication.isPlaying) { Debug.LogWarning("Enter Play mode and start a host/client first."); return; }
        foreach (PlayerMovement player in UnityEngine.Object.FindObjectsByType<PlayerMovement>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
        {
            if (!player.isLocalPlayer) continue;
            CharacterController c = player.GetComponent<CharacterController>();
            Debug.Log($"[Gobmine Movement] Local player {player.netId}: {player.MovementStatus}; position={player.transform.position}; " +
                $"grounded={player.IsGrounded}; verticalSpeed={player.VerticalVelocity:0.00}; waitChecks={player.TerrainWaitFrames}; rollbacks={player.PenetrationRollbacks}; " +
                $"controller={(c != null && c.enabled)}; root Rigidbody={(player.GetComponent<Rigidbody>() != null)}", player);
        }
        World w = World.Instance;
        if (w != null) Debug.Log($"[Gobmine Movement] World ready={w.IsReady}; collision waits={w.PlayerCollisionWaits}; priority builds={w.PlayerCollisionPriorityBuilds}; " +
            $"chunk builds pending={w.PendingChunkBuilds}; meshes pending={w.PendingMeshRebuilds}; active chunks={w.ActiveChunkCount}", w);
    }
}
#endif
