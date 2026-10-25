using System;
using UnityEngine;

public static class GameEvents
{
    public static event Action<Vector3, int> OnClusterCollapsed;
    public static void TriggerClusterCollapsed(Vector3 position, int size) => OnClusterCollapsed?.Invoke(position, size);

    public static event Action<MineralData, int> OnMineralCollected;
    public static void TriggerMineralCollected(MineralData mineral, int amount) => OnMineralCollected?.Invoke(mineral, amount);

    public static event Action OnGameSessionStarted;
    public static void TriggerGameSessionStarted() => OnGameSessionStarted?.Invoke();
}