using System.Collections;
using UnityEngine;
using Mirror;

public class PlayerNetworkRegistration : NetworkBehaviour
{
    private Coroutine _registration;
    public override void OnStartClient() { base.OnStartClient(); RegisterWithWorld(); }
    public override void OnStartServer() { base.OnStartServer(); RegisterWithWorld(); }
    public override void OnStopClient() { Unregister(); base.OnStopClient(); }
    public override void OnStopServer() { Unregister(); base.OnStopServer(); }

    private void RegisterWithWorld()
    {
        if (World.Instance != null) World.Instance.RegisterPlayer(transform);
        else if (_registration == null) _registration = StartCoroutine(DelayedRegistration());
    }
    private IEnumerator DelayedRegistration()
    {
        while (World.Instance == null) yield return null;
        World.Instance.RegisterPlayer(transform);
        _registration = null;
    }
    private void Unregister()
    {
        if (_registration != null) { StopCoroutine(_registration); _registration = null; }
        if (World.Instance != null) World.Instance.UnregisterPlayer(transform);
    }
}
