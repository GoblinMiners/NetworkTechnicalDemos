using UnityEngine;
using Mirror;

public enum GameState { Lobby, Exploring, Extracting, GameOver }

public class GameManager : NetworkBehaviour
{
    public static GameManager Instance { get; private set; }

    [SyncVar(hook = nameof(OnStateChanged))] 
    public GameState currentGameState = GameState.Lobby;

    public delegate void GameStateDelegate(GameState newState);
    public event GameStateDelegate OnGameStateChanged;

    private void Awake()
    {
        if (Instance == null) Instance = this;
        else Destroy(gameObject);
    }

    public void SetGameState(GameState newState)
    {
        if (!isServer) return;
        currentGameState = newState;
    }

    private void OnStateChanged(GameState oldState, GameState newState)
    {
        OnGameStateChanged?.Invoke(newState);
        if (newState == GameState.Exploring)
        {
            GameEvents.TriggerGameSessionStarted();
        }
    }
}