using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

public sealed class NetworkEntryUI : MonoBehaviour
{
    private const string InGameSceneName = "InGame";
    private const int MaxRoomRows = 64;

    [SerializeField] private GameObject startPanel;
    [SerializeField] private GameObject browserPanel;
    [SerializeField] private GameObject createRoomPanel;
    [SerializeField] private GameObject loadingPanel;
    [SerializeField] private GameObject leavePanel;
    [SerializeField] private GameObject entryCameraRoot;
    [SerializeField] private GameObject entryEventSystemRoot;
    [SerializeField] private TMP_InputField displayNameInput;
    [SerializeField] private TMP_InputField roomNameInput;
    [SerializeField] private TMP_Text statusText;
    [SerializeField] private TMP_Text errorText;
    [SerializeField] private Transform roomListContent;
    [SerializeField] private LanRoomRowUI roomRowTemplate;
    [SerializeField] private Button gameStartButton;
    [SerializeField] private Button saveProfileButton;
    [SerializeField] private Button refreshButton;
    [SerializeField] private Button openCreateRoomButton;
    [SerializeField] private Button createRoomConfirmButton;
    [SerializeField] private Button createRoomCancelButton;
    [SerializeField] private Button browserBackButton;
    [SerializeField] private Button pendingCancelButton;
    [SerializeField] private Button leaveButton;
    [SerializeField] private Button leaveConfirmButton;
    [SerializeField] private Button leaveCancelButton;

    private readonly List<LanRoomRowUI> roomRows = new List<LanRoomRowUI>(8);
    private NetworkGameSession session;
    private LanRoomDiscovery discovery;
    private bool createDialogOpen;
    private bool leavePanelOpen;
    private bool profileInputInitialized;
    private bool entrySceneLoaded;
    private CursorLockMode previousCursorLockState;
    private bool previousCursorVisible;
    private string localStatusOverride;
    private string localErrorOverride;
    private GameObject menuPlayer;
    private Behaviour[] suspendedInputs;
    private bool[] suspendedInputStates;

    private void OnEnable()
    {
        SceneManager.sceneLoaded += HandleSceneLoaded;
        SceneManager.sceneUnloaded += HandleSceneUnloaded;
        RegisterButtons();
        ConfigureInputText();
        RefreshEntryInfrastructure();
        EnsureSessionBinding();
        Render();
    }

    private void OnDisable()
    {
        SceneManager.sceneLoaded -= HandleSceneLoaded;
        SceneManager.sceneUnloaded -= HandleSceneUnloaded;
        UnregisterButtons();
        UnbindSession();
        CloseLeavePanel();
    }

    private void Update()
    {
        EnsureSessionBinding();
        HandleEscape();
        Render();
    }

    private void HandleSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        RefreshEntryInfrastructure();
    }

    private void HandleSceneUnloaded(Scene scene)
    {
        RefreshEntryInfrastructure();
    }

    private void RefreshEntryInfrastructure()
    {
        Scene inGameScene = SceneManager.GetSceneByName(InGameSceneName);
        entrySceneLoaded = inGameScene.IsValid() && inGameScene.isLoaded;
        SetActiveIfChanged(entryCameraRoot, !entrySceneLoaded);
        SetActiveIfChanged(entryEventSystemRoot, !entrySceneLoaded);
    }

    private void ConfigureInputText()
    {
        if (displayNameInput != null)
        {
            if (!profileInputInitialized)
            {
                displayNameInput.text = LocalPlayerProfile.LoadDisplayName();
                profileInputInitialized = true;
            }
            SetInputRichText(displayNameInput);
        }

        SetInputRichText(roomNameInput);
    }

    private static void SetInputRichText(TMP_InputField input)
    {
        if (input == null)
            return;

        if (input.textComponent != null)
            input.textComponent.richText = false;
        if (input.placeholder is TMP_Text placeholderText)
            placeholderText.richText = false;
    }

    private void RegisterButtons()
    {
        AddListener(gameStartButton, HandleGameStart);
        AddListener(saveProfileButton, HandleSaveProfile);
        AddListener(refreshButton, HandleRefresh);
        AddListener(openCreateRoomButton, HandleOpenCreateRoom);
        AddListener(createRoomConfirmButton, HandleConfirmCreateRoom);
        AddListener(createRoomCancelButton, HandleCancelCreateRoom);
        AddListener(browserBackButton, HandleBrowserBack);
        AddListener(pendingCancelButton, HandlePendingCancel);
        AddListener(leaveButton, OpenLeavePanel);
        AddListener(leaveConfirmButton, HandleLeaveMatch);
        AddListener(leaveCancelButton, HandleCancelLeave);
    }

    private void UnregisterButtons()
    {
        RemoveListener(gameStartButton, HandleGameStart);
        RemoveListener(saveProfileButton, HandleSaveProfile);
        RemoveListener(refreshButton, HandleRefresh);
        RemoveListener(openCreateRoomButton, HandleOpenCreateRoom);
        RemoveListener(createRoomConfirmButton, HandleConfirmCreateRoom);
        RemoveListener(createRoomCancelButton, HandleCancelCreateRoom);
        RemoveListener(browserBackButton, HandleBrowserBack);
        RemoveListener(pendingCancelButton, HandlePendingCancel);
        RemoveListener(leaveButton, OpenLeavePanel);
        RemoveListener(leaveConfirmButton, HandleLeaveMatch);
        RemoveListener(leaveCancelButton, HandleCancelLeave);
    }

    private static void AddListener(Button button, UnityEngine.Events.UnityAction listener)
    {
        if (button != null)
            button.onClick.AddListener(listener);
    }

    private static void RemoveListener(Button button, UnityEngine.Events.UnityAction listener)
    {
        if (button != null)
            button.onClick.RemoveListener(listener);
    }

    private void EnsureSessionBinding()
    {
        NetworkGameSession currentSession = NetworkGameSession.Instance;
        if (session == currentSession)
        {
            BindDiscovery(session != null ? session.Discovery : null);
            return;
        }

        UnbindSession();
        session = currentSession;
        if (session != null)
            session.Changed += HandleSessionChanged;
        BindDiscovery(session != null ? session.Discovery : null);
        RefreshRoomRows();
    }

    private void UnbindSession()
    {
        if (session != null)
            session.Changed -= HandleSessionChanged;
        session = null;
        BindDiscovery(null);
    }

    private void BindDiscovery(LanRoomDiscovery currentDiscovery)
    {
        if (discovery == currentDiscovery)
            return;

        if (discovery != null)
        {
            discovery.StatusChanged -= HandleDiscoveryStatusChanged;
            discovery.RoomsChanged -= HandleRoomsChanged;
        }

        discovery = currentDiscovery;
        if (discovery != null)
        {
            discovery.StatusChanged += HandleDiscoveryStatusChanged;
            discovery.RoomsChanged += HandleRoomsChanged;
        }

        RefreshRoomRows();
    }

    private void HandleSessionChanged(NetworkGameSession changedSession)
    {
        if (changedSession != session)
            return;

        localStatusOverride = null;
        localErrorOverride = null;
        if (session.State != NetworkGameSessionState.Browsing)
            createDialogOpen = false;
        BindDiscovery(session.Discovery);
        RefreshRoomRows();
        Render();
    }

    private void HandleDiscoveryStatusChanged(LanRoomDiscoveryStatus changedStatus)
    {
        Render();
    }

    private void HandleRoomsChanged(IReadOnlyList<LanDiscoveredRoom> rooms)
    {
        RefreshRoomRows(rooms);
        Render();
    }

    private void RefreshRoomRows(IReadOnlyList<LanDiscoveredRoom> rooms = null)
    {
        if (discovery == null || roomListContent == null || roomRowTemplate == null)
        {
            ClearRoomRows();
            return;
        }

        if (rooms == null)
            rooms = discovery.Rooms;

        if (roomRowTemplate.gameObject.scene.IsValid() &&
            (roomRowTemplate.transform.parent == roomListContent || roomRowTemplate.transform.IsChildOf(roomListContent)))
        {
            roomRowTemplate.gameObject.SetActive(false);
        }

        int rowCount = Mathf.Min(rooms != null ? rooms.Count : 0, MaxRoomRows);
        while (roomRows.Count < rowCount)
        {
            LanRoomRowUI row = Instantiate(roomRowTemplate, roomListContent, false);
            row.Clear();
            roomRows.Add(row);
        }

        for (int index = 0; index < rowCount; index++)
            roomRows[index].Bind(rooms[index], HandleJoinRoom);
        for (int index = rowCount; index < roomRows.Count; index++)
            roomRows[index].Clear();
    }

    private void ClearRoomRows()
    {
        for (int index = 0; index < roomRows.Count; index++)
        {
            if (roomRows[index] != null)
                roomRows[index].Clear();
        }
    }

    private void HandleGameStart()
    {
        if (session == null || session.State != NetworkGameSessionState.Entry)
            return;

        createDialogOpen = false;
        ClearLocalMessages();
        session.OpenBrowser();
    }

    private void HandleSaveProfile()
    {
        string input = displayNameInput != null ? displayNameInput.text : string.Empty;
        if (!LocalPlayerProfile.TrySaveDisplayName(input, out string savedName, out string saveError))
        {
            localErrorOverride = saveError;
            localStatusOverride = null;
            Render();
            return;
        }

        if (displayNameInput != null)
            displayNameInput.text = savedName;
        localErrorOverride = null;
        localStatusOverride = "이름을 저장했습니다.";
        Render();
    }

    private void HandleRefresh()
    {
        if (session == null || session.State != NetworkGameSessionState.Browsing)
            return;

        ClearLocalMessages();
        session.OpenBrowser();
    }

    private void HandleOpenCreateRoom()
    {
        if (session == null || session.State != NetworkGameSessionState.Browsing)
            return;

        createDialogOpen = true;
        ClearLocalMessages();
        Render();
    }

    private void HandleConfirmCreateRoom()
    {
        if (session == null || session.State != NetworkGameSessionState.Browsing)
            return;

        string requestedName = roomNameInput != null ? roomNameInput.text : string.Empty;
        if (!LocalPlayerProfile.TryValidateRoomName(requestedName, out string normalizedName, out string validationError))
        {
            localErrorOverride = validationError;
            localStatusOverride = null;
            Render();
            return;
        }

        ClearLocalMessages();
        session.CreateRoom(normalizedName);
        if (session.State != NetworkGameSessionState.Browsing)
            createDialogOpen = false;
        Render();
    }

    private void HandleCancelCreateRoom()
    {
        createDialogOpen = false;
        ClearLocalMessages();
        Render();
    }

    private void HandleBrowserBack()
    {
        if (session == null || session.State != NetworkGameSessionState.Browsing)
            return;

        createDialogOpen = false;
        ClearLocalMessages();
        session.BackToStart();
    }

    private void HandlePendingCancel()
    {
        if (session == null || !IsPendingState(session.State))
            return;

        createDialogOpen = false;
        session.BackToStart();
    }

    private void HandleJoinRoom(LanDiscoveredRoom room)
    {
        if (session == null || session.State != NetworkGameSessionState.Browsing || room == null || !room.IsJoinable)
            return;

        ClearLocalMessages();
        session.JoinRoom(room);
    }

    private void HandleLeaveMatch()
    {
        if (session == null || session.State != NetworkGameSessionState.Running)
            return;

        session.Leave();
    }

    private void HandleCancelLeave()
    {
        if (session == null || session.State != NetworkGameSessionState.Running)
            return;

        CloseLeavePanel();
        Render();
    }

    private void HandleEscape()
    {
        if (session == null || session.State != NetworkGameSessionState.Running || Keyboard.current == null ||
            !Keyboard.current.escapeKey.wasPressedThisFrame)
        {
            return;
        }

        if (leavePanelOpen)
            CloseLeavePanel();
        else
            OpenLeavePanel();
        Render();
    }

    private void OpenLeavePanel()
    {
        if (leavePanelOpen || session == null || session.State != NetworkGameSessionState.Running)
            return;

        previousCursorLockState = Cursor.lockState;
        previousCursorVisible = Cursor.visible;
        leavePanelOpen = true;
        menuPlayer = GameManager.Instance != null ? GameManager.Instance.CurrentPlayer : null;
        if (menuPlayer != null)
        {
            suspendedInputs = new Behaviour[]
            {
                menuPlayer.GetComponent<PlayerInput>(), menuPlayer.GetComponent<PlayerController>(),
                menuPlayer.GetComponent<PlayerLook>(), menuPlayer.GetComponent<PlayerSkillInput>(),
                menuPlayer.GetComponent<WizardSkillTargeting>()
            };
            suspendedInputStates = new bool[suspendedInputs.Length];
            for (int i = 0; i < suspendedInputs.Length; i++)
            {
                if (suspendedInputs[i] == null) continue;
                suspendedInputStates[i] = suspendedInputs[i].enabled;
                suspendedInputs[i].enabled = false;
            }
            menuPlayer.GetComponent<PlayerInputHandler>()?.ClearInput();
        }
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
        Render();
    }

    private void CloseLeavePanel()
    {
        if (!leavePanelOpen)
            return;

        leavePanelOpen = false;
        bool restore = session != null && session.State == NetworkGameSessionState.Running &&
            menuPlayer != null && GameManager.Instance != null && GameManager.Instance.CurrentPlayer == menuPlayer &&
            (!menuPlayer.TryGetComponent(out PlayerHealth health) || !health.IsDead);
        if (restore && suspendedInputs != null)
            for (int i = 0; i < suspendedInputs.Length; i++)
                if (suspendedInputs[i] != null) suspendedInputs[i].enabled = suspendedInputStates[i];
        suspendedInputs = null;
        suspendedInputStates = null;
        menuPlayer = null;
        Cursor.lockState = restore ? previousCursorLockState : CursorLockMode.None;
        Cursor.visible = !restore || previousCursorVisible;
    }

    private void Render()
    {
        NetworkGameSessionState state = session != null ? session.State : NetworkGameSessionState.Entry;
        bool hasSession = session != null;
        bool isEntryOrBrowser = state == NetworkGameSessionState.Entry || state == NetworkGameSessionState.Browsing;
        bool isBrowsing = state == NetworkGameSessionState.Browsing;
        bool isRunning = state == NetworkGameSessionState.Running;
        bool isPending = IsPendingState(state);

        if (!isRunning && leavePanelOpen)
            CloseLeavePanel();

        SetPanel(startPanel, hasSession && state == NetworkGameSessionState.Entry);
        SetPanel(browserPanel, hasSession && isBrowsing);
        SetPanel(createRoomPanel, hasSession && isBrowsing && createDialogOpen);
        SetPanel(loadingPanel, hasSession && isPending);
        SetPanel(leavePanel, hasSession && isRunning && leavePanelOpen);

        SetInteractable(gameStartButton, hasSession && state == NetworkGameSessionState.Entry);
        SetInteractable(saveProfileButton, true);
        SetInteractable(refreshButton, isBrowsing);
        SetInteractable(openCreateRoomButton, isBrowsing);
        SetInteractable(createRoomConfirmButton, isBrowsing && createDialogOpen);
        SetInteractable(createRoomCancelButton, isBrowsing && createDialogOpen);
        SetInteractable(browserBackButton, isBrowsing);
        SetInteractable(pendingCancelButton, isPending);
        SetInteractable(leaveButton, isRunning);
        SetActiveIfChanged(leaveButton != null ? leaveButton.gameObject : null, isRunning);
        SetInteractable(leaveConfirmButton, isRunning && leavePanelOpen);
        SetInteractable(leaveCancelButton, isRunning);

        bool showEntryStatus = isEntryOrBrowser || isPending;
        if (showEntryStatus)
        {
            string status = localStatusOverride;
            if (string.IsNullOrEmpty(status))
                status = hasSession ? session.Status : "Network session is unavailable.";
            SetText(statusText, status);

            string error = localErrorOverride;
            if (string.IsNullOrEmpty(error) && hasSession)
                error = session.Error;
            if (string.IsNullOrEmpty(error) && isBrowsing && discovery != null && discovery.Status != null &&
                discovery.Status.State == LanRoomDiscoveryState.Error)
            {
                error = discovery.Status.Message;
            }
            SetText(errorText, error);
        }
        else
        {
            SetText(statusText, string.Empty);
            SetText(errorText, string.Empty);
        }

        if (entryCameraRoot != null && entryCameraRoot.activeSelf == entrySceneLoaded)
            entryCameraRoot.SetActive(!entrySceneLoaded);
        if (entryEventSystemRoot != null && entryEventSystemRoot.activeSelf == entrySceneLoaded)
            entryEventSystemRoot.SetActive(!entrySceneLoaded);
    }

    private static bool IsPendingState(NetworkGameSessionState state)
    {
        return state == NetworkGameSessionState.Creating || state == NetworkGameSessionState.Joining ||
               state == NetworkGameSessionState.Stopping;
    }

    private static void SetPanel(GameObject panel, bool visible)
    {
        SetActiveIfChanged(panel, visible);
    }

    private static void SetActiveIfChanged(GameObject target, bool active)
    {
        if (target != null && target.activeSelf != active)
            target.SetActive(active);
    }

    private static void SetInteractable(Button button, bool interactable)
    {
        if (button != null)
            button.interactable = interactable;
    }

    private static void SetText(TMP_Text label, string value)
    {
        if (label == null)
            return;

        label.richText = false;
        label.text = value ?? string.Empty;
    }

    private void ClearLocalMessages()
    {
        localStatusOverride = null;
        localErrorOverride = null;
    }
}
