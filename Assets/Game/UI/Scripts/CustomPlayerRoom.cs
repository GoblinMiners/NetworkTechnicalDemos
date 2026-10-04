using Mirror;
using UnityEngine;

public class CustomPlayerRoom : NetworkRoomPlayer
{
    public override void OnClientEnterRoom()
    {
        if (MenuController.Instance != null) MenuController.Instance.UpdatePlayerList();
    }

    public override void OnClientExitRoom()
    {
        if (MenuController.Instance != null) MenuController.Instance.UpdatePlayerList();
    }

    public override void ReadyStateChanged(bool oldReadyState, bool newReadyState)
    {
        if (MenuController.Instance != null) MenuController.Instance.UpdatePlayerList();
    }
    
}
