using UnityEngine;
using Mirror;
using UnityEngine.InputSystem;

public class PlayerSetup : NetworkBehaviour
{
    [Header("Local Player Components")]
    public Camera playerCamera;
    public AudioListener audioListener;
    public Behaviour[] scriptsToDisable; // Drag PlayerMovement, VoxelMiner, etc. here
    public PlayerInput playerInput; // If you are using the PlayerInput component

    private void Start()
    {
        // isLocalPlayer is a Mirror variable that is true ONLY for the player sitting at this computer
        if (!isLocalPlayer)
        {
            // Disable the camera and audio so they don't hijack the host's screen
            if (playerCamera != null) playerCamera.gameObject.SetActive(false);
            if (audioListener != null) audioListener.enabled = false;

            // Disable the input system for this specific clone
            if (playerInput != null) playerInput.enabled = false;

            // Disable movement and mining scripts so remote players only move when the server tells them to
            foreach (Behaviour script in scriptsToDisable)
            {
                if (script != null) script.enabled = false;
            }
        }
    }
}