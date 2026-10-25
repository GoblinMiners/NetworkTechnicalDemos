using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;
using Mirror;

public class MenuController : MonoBehaviour
{
    [Header("Menu State")]
    private PanelRenderer _panelRenderer;
    private int _uiVersion = 0;
    
    public static MenuController Instance;

    // Containers
    private VisualElement _mainMenuContainer;
    private VisualElement _hostJoinContainer;
    private VisualElement _startNewLobbyContainer;
    private VisualElement _joinLobbyContainer;
    private VisualElement _waitingRoomContainer; 

    // Main Menu Buttons
    private Button _startButton;
    
    // Host/Join Buttons
    private Button _hostGameButton;
    private Button _joinGameButton;
    private Button _hostGameBackButton;

    // Start New Lobby
    private Button _createLobbyButton;
    private Button _startLobbyBackButton;
    private TextField _lobbyName; 
    private TextField _lobbyCode; 

    // Join Lobby
    private Button _joinLobbyButton;
    private Button _joinLobbyBackButton;
    private TextField _enterCode;
    private Label _errorField;
    
    [Header("Waiting Room UI")]
    private Button _startLobbyButton;
    private Button _waitingRoomBackButton;
    private Label[] _playerNames = new Label[4];
    private Toggle[] _playerReadies = new Toggle[4];

    private void Awake()
    {
        if (Instance == null) Instance = this;
    }

    private void OnEnable()
    {
        _panelRenderer = GetComponentInChildren<PanelRenderer>(true);
        if (_panelRenderer != null) _panelRenderer.RegisterUIReloadCallback(OnUIReload);
    }

    private void OnDisable()
    {
        if (_panelRenderer != null) _panelRenderer.UnregisterUIReloadCallback(OnUIReload);
    }
    
    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    private void OnUIReload(PanelRenderer renderer, VisualElement root, int version)
    {
        if (root == null || version == _uiVersion) return;
        _uiVersion = version;

        // 1. Bind Containers
        _mainMenuContainer = root.Q<VisualElement>("MainMenuContainer");
        _hostJoinContainer = root.Q<VisualElement>("HostJoinContainer");
        _startNewLobbyContainer = root.Q<VisualElement>("StartNewLobbyContainer");
        _joinLobbyContainer = root.Q<VisualElement>("JoinLobbyContainer");
        _waitingRoomContainer = root.Q<VisualElement>("WaitingRoomContainer"); 

        // 2. Bind Main Menu
        _startButton = root.Q<Button>("StartButton");
        if (_startButton != null) _startButton.clicked += () => SwitchMenu(_hostJoinContainer);

        // 3. Bind Host/Join Menu
        _hostGameButton = root.Q<Button>("HostGameButton");
        _joinGameButton = root.Q<Button>("JoinGameButton");
        _hostGameBackButton = root.Q<Button>("HostGameBackButton");

        if (_hostGameButton != null) _hostGameButton.clicked += () => SwitchMenu(_startNewLobbyContainer);
        if (_joinGameButton != null) _joinGameButton.clicked += () => SwitchMenu(_joinLobbyContainer);
        if (_hostGameBackButton != null) _hostGameBackButton.clicked += () => SwitchMenu(_mainMenuContainer);

        // 4. Bind Start New Lobby
        _createLobbyButton = root.Q<Button>("CreateLobbyButton");
        _startLobbyBackButton = root.Q<Button>("StartLobbyBackButton");
        _lobbyName = root.Q<TextField>("LobbyName");
        _lobbyCode = root.Q<TextField>("LobbyCode");

        if (_startLobbyBackButton != null) _startLobbyBackButton.clicked += () => SwitchMenu(_hostJoinContainer);
        if (_createLobbyButton != null) _createLobbyButton.clicked += CreateLobby;

        // 5. Bind Join Lobby
        _joinLobbyButton = root.Q<Button>("JoinLobbyButton");
        _joinLobbyBackButton = root.Q<Button>("JoinLobbyBackButton");
        _enterCode = root.Q<TextField>("EnterCode");
        _errorField = root.Q<Label>("ErrorField");

        if (_joinLobbyBackButton != null) _joinLobbyBackButton.clicked += () => SwitchMenu(_hostJoinContainer);
        if (_joinLobbyButton != null) _joinLobbyButton.clicked += JoinLobby;

        // 6. Bind Waiting Room (Added here so the buttons actually wire up!)
        BindWaitingRoom(root);

        // Force initial state
        SwitchMenu(_mainMenuContainer);
    }

    private void BindWaitingRoom(VisualElement root)
    {
        _startLobbyButton = root.Q<Button>("StartLobbyButton");
        _waitingRoomBackButton = root.Q<Button>("WaitingRoomBackButton");
        
        if (_waitingRoomBackButton != null) 
        {
            _waitingRoomBackButton.clicked += () => {
                if (NetworkServer.active && NetworkClient.isConnected)
                    NetworkManager.singleton.StopHost();
                else
                    NetworkManager.singleton.StopClient();
                    
                SwitchMenu(_hostJoinContainer);
            };
        }

        if (_startLobbyButton != null)
        {
            _startLobbyButton.clicked += () => {
                // Only the host can start the game, and only if all players are ready
                if (NetworkServer.active && (NetworkManager.singleton as NetworkRoomManager).allPlayersReady)
                {
                    (NetworkManager.singleton as NetworkRoomManager).ServerChangeScene((NetworkManager.singleton as NetworkRoomManager).GameplayScene);
                }
            };
        }

        // Find the 4 player slots based on your naming convention
        for (int i = 0; i < 4; i++)
        {
            int playerNum = i + 1; // 1, 2, 3, 4
            _playerNames[i] = root.Q<Label>($"Player{playerNum}Name");
            _playerReadies[i] = root.Q<Toggle>($"Player{playerNum}Ready");
    
            if (_playerReadies[i] != null) 
            {
                _playerReadies[i].RegisterValueChangedCallback(evt => {
                    if (NetworkClient.connection == null || NetworkClient.connection.identity == null) return;
    
                    CustomPlayerRoom localPlayer = NetworkClient.connection.identity.GetComponent<CustomPlayerRoom>();
                    if (localPlayer != null)
                    {
                        localPlayer.CmdChangeReadyState(evt.newValue);
                    }
                });
            }
        }
    }
    
    public void UpdatePlayerList()
    {
        
        // SAFETY CATCH: If the UI is currently being destroyed by a scene change, abort the update
        if (_playerReadies == null || _playerReadies[0] == null || _playerReadies[0].panel == null) return;
        
        NetworkRoomManager roomManager = NetworkManager.singleton as NetworkRoomManager;
        if (roomManager == null) return;

        // Hide the Start button if this player is just a client joining
        if (_startLobbyButton != null)
        {
            _startLobbyButton.style.display = NetworkServer.active ? DisplayStyle.Flex : DisplayStyle.None;
        }

        // Convert the Mirror HashSet into a standard List and sort it by index
        System.Collections.Generic.List<NetworkRoomPlayer> playerList = new System.Collections.Generic.List<NetworkRoomPlayer>(roomManager.roomSlots);
        playerList.Sort((a, b) => a.index.CompareTo(b.index));

        // Loop through the 4 UI slots and update them based on connected players
        for (int i = 0; i < 4; i++)
        {
            if (i < playerList.Count)
            {
                CustomPlayerRoom player = playerList[i] as CustomPlayerRoom;
                if (_playerNames[i] != null) _playerNames[i].text = $"Player {i + 1}";
            
                // Only allow the local player to physically click their own toggle
                if (_playerReadies[i] != null) 
                {
                    _playerReadies[i].SetValueWithoutNotify(player.readyToBegin);
                    _playerReadies[i].SetEnabled(player.isLocalPlayer);
                }
            }
            else
            {
                // Empty slot
                if (_playerNames[i] != null) _playerNames[i].text = "Waiting for player...";
                if (_playerReadies[i] != null) 
                {
                    _playerReadies[i].SetValueWithoutNotify(false);
                    _playerReadies[i].SetEnabled(false);
                }
            }
        }
    }

    // Helper method to turn off all menus and turn on the target
    private void SwitchMenu(VisualElement activeContainer)
    {
        if (_mainMenuContainer != null) _mainMenuContainer.style.display = DisplayStyle.None;
        if (_hostJoinContainer != null) _hostJoinContainer.style.display = DisplayStyle.None;
        if (_startNewLobbyContainer != null) _startNewLobbyContainer.style.display = DisplayStyle.None;
        if (_joinLobbyContainer != null) _joinLobbyContainer.style.display = DisplayStyle.None;
        if (_waitingRoomContainer != null) _waitingRoomContainer.style.display = DisplayStyle.None;

        if (activeContainer != null) activeContainer.style.display = DisplayStyle.Flex;
    }

    private void CreateLobby()
    {
        Debug.Log("Starting Host...");
        if (NetworkManager.singleton != null)
        {
            NetworkManager.singleton.StartHost();
            SwitchMenu(_waitingRoomContainer);
        }
    }

    private void JoinLobby()
    {
        Debug.Log("Joining Client...");
        if (NetworkManager.singleton != null)
        {
            // Standard Mirror uses IP addresses. If you type "localhost" or a local IP into your EnterCode field, this will connect.
            if (_enterCode != null && !string.IsNullOrEmpty(_enterCode.value))
            {
                NetworkManager.singleton.networkAddress = _enterCode.value;
            }
            
            NetworkManager.singleton.StartClient();
            SwitchMenu(_waitingRoomContainer);
        }
    }
}