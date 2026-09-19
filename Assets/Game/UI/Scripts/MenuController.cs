
using Mirror;
using UnityEngine;
using UnityEngine.UIElements;

public class MenuController : MonoBehaviour
{
    private VisualElement _mainMenu;
    private VisualElement _lobbyMenu;
    private IntegerField _seedInput;
    private Button _hostButton;
    private Button _startGameButton;

    private PanelRenderer _panelRenderer;
    private int _uiVersion = -1;

    private void OnEnable()
    {
        _panelRenderer = GetComponentInChildren<PanelRenderer>(true);

        if (_panelRenderer == null)
        {
            Debug.LogError(
                "CRITICAL: No PanelRenderer found on UI_Manager or its children!"
            );

            return;
        }

        _panelRenderer.RegisterUIReloadCallback(OnUIReload);
    }

    private void OnDisable()
    {
        if (_panelRenderer != null)
        {
            _panelRenderer.UnregisterUIReloadCallback(OnUIReload);
        }

        if (_hostButton != null)
        {
            _hostButton.clicked -= OpenLobbyScreen;
        }
        
        if( _startGameButton != null)
        {
            _startGameButton.clicked -= StartHostWithSeed;
        }
    }

    private void OnUIReload(
        PanelRenderer renderer,
        VisualElement rootElement,
        int version)
    {
        Debug.Log($"UI Reloaded. Version: {version}");

        if (_uiVersion == version)
        {
            return;
        }

        _uiVersion = version;

        // Match the names in your UXML.
        _mainMenu = rootElement.Q<VisualElement>("MainMenuContainer");
        _lobbyMenu = rootElement.Q<VisualElement>("LobbyContainer");

        _seedInput = rootElement.Q<IntegerField>("SeedInput");

        _hostButton = rootElement.Q<Button>("HostButton");
        _startGameButton = rootElement.Q<Button>("StartGameButton");

        if (_hostButton == null)
        {
            Debug.LogError(
                "HostButton could not be found in the PanelRenderer UI!"
            );

            return;
        }

        Debug.Log("HostButton successfully found!");

       // _hostButton.clicked -= OpenLobbyScreen;
        _hostButton.clicked += OpenLobbyScreen;
        
       // _startGameButton.clicked -= StartHostWithSeed;
        _startGameButton.clicked += StartHostWithSeed;

        Debug.Log("HostButton click event registered successfully.");
    }

    private void OpenLobbyScreen()
    {
        Debug.Log("Host button clicked!");

        if (_mainMenu != null)
        {
            _mainMenu.style.display = DisplayStyle.None;
        }

        if (_lobbyMenu != null)
        {
            _lobbyMenu.style.display = DisplayStyle.Flex;
        }
    }
    
    private void StartHostWithSeed()
    {
        Debug.Log("Start Game button clicked! Booting server...");
        
        if (World.Instance != null && _seedInput != null)
        {
            World.Instance.customSeed = _seedInput.value;
        }
        
        if (NetworkManager.singleton != null)
        {
            NetworkManager.singleton.StartHost();
        }
        
        if (_panelRenderer != null)
        {
            _panelRenderer.gameObject.SetActive(false);
        }
    }
}

