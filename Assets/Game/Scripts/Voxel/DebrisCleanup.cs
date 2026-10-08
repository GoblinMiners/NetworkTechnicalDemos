using UnityEngine;
using Mirror;

public class DebrisCleanup : NetworkBehaviour
{
    public float lifetime = 5f;

    public override void OnStartServer()
    {
        // Only the server dictates when objects are destroyed
        Invoke(nameof(Despawn), lifetime);
    }

    private void Despawn()
    {
        NetworkServer.Destroy(gameObject);
    }
}