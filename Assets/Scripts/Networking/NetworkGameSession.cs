using System;
using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;
using UnityEngine.SceneManagement;

public enum NetworkGameSessionState
{
    Entry,
    Browsing,
    Creating,
    Joining,
    Lobby,
    LoadingMatch,
    Running,
    Stopping
}

// Owns the local room, NGO, shared scene and cleanup lifetimes. Display labels are not identities.
[DefaultExecutionOrder(-200)]
[RequireComponent(typeof(NetworkManager), typeof(UnityTransport), typeof(LanRoomDiscovery))]
public sealed class NetworkGameSession : MonoBehaviour
{
    private const ushort GamePort = 7777;
    private const double LobbySynchronizationTimeout = 30.0;
    private const double SceneLoadTimeout = 60.0;
    private const double CleanupTimeout = 30.0;
    private const string LobbySceneName = "TeamLobby";
    private const string InGameSceneName = "InGame";
    private const string MapSceneName = "Map";
    private const string EntrySceneName = "NetworkEntry";
    private static readonly string[] SceneUnloadOrder = { MapSceneName, InGameSceneName, LobbySceneName };

    [SerializeField] private GameObject[] playerPrefabs = new GameObject[3];

    [Header("Downed / Revive")]
    [Tooltip("쓰러진 팀원이 부활하기까지 다른 팀원이 버텨야 하는 시간(초).")]
    [SerializeField] private float reviveDelay = 10f;
    [SerializeField, Range(0.05f, 1f)] private float reviveHealthRatio = 0.5f;
    [SerializeField] private float reviveInvulnerableSeconds = 2f;

    private MatchFlow matchFlow;
    private double matchStartedAt;

    private static NetworkGameSession instance;
    private static bool networkSessionActive;

    private NetworkManager manager;
    private UnityTransport transport;
    private LanRoomDiscovery discovery;
    private NetworkSceneManager networkSceneManager;
    private NetworkLobbyState lobby;
    private NetworkGameSessionState state = NetworkGameSessionState.Entry;
    private string status = "Ready.";
    private string error;
    private string displayName;
    private string roomName;
    private Guid roomInstanceId;
    private bool isHost;
    private bool admissionOpen;
    private bool admissionLocked;
    private bool serverStarted;
    private bool localSynchronizationPending;
    private bool localCombatPrepared;
    private bool localCombatReady;
    private bool teardownStarted;
    private bool cleanupFaulted;
    private bool quitting;
    private bool pendingBrowserStart;
    private bool returnToBrowser;
    private bool returnToEntry;
    private bool unloadStarted;
    private bool pendingSceneLoadComplete;
    private bool pendingSceneLoadFailed;
    private string pendingSceneLoadFailure;
    private string expectedSceneName;
    private string queuedSceneName;
    private SceneLoadPurpose expectedScenePurpose;
    private double actionDeadline;
    private double synchronizationDeadline;
    private double teardownDeadline;
    private int generation;
    private byte[] connectionPayload;
    private IPAddress joinAddress;
    private ushort joinPort;
    private NetworkLobbySnapshot frozenRoster;
    private readonly HashSet<ulong> frozenClientIds = new HashSet<ulong>();
    private readonly HashSet<ulong> completedSceneClients = new HashSet<ulong>();
    private readonly Dictionary<ulong, PendingAdmission> pendingAdmissions = new Dictionary<ulong, PendingAdmission>(1);
    private readonly Dictionary<int, string> ownedScenes = new Dictionary<int, string>();
    private readonly HashSet<int> scenesPresentBeforeSession = new HashSet<int>();
    private readonly List<AsyncOperation> pendingSceneLoads = new List<AsyncOperation>(3);
    private readonly Dictionary<ulong, NetworkObject> spawnedPlayers = new Dictionary<ulong, NetworkObject>(2);
    private readonly List<int> cleanupSceneHandles = new List<int>(3);
    private AsyncOperation cleanupUnloadOperation;
    private AsyncOperation mapLoadOperation;
    private int cleanupUnloadSceneHandle;
    private int cleanupUnloadIndex;

    private Action<NetworkManager.ConnectionApprovalRequest, NetworkManager.ConnectionApprovalResponse> approvalCallback;
    private Action<ulong> clientConnectedCallback;
    private Action<ulong> clientDisconnectedCallback;
    private Action transportFailureCallback;
    private Action serverStartedCallback;
    private Action clientStartedCallback;
    private NetworkSceneManager.OnLoadDelegateHandler sceneLoadCallback;
    private NetworkSceneManager.OnEventCompletedDelegateHandler loadCompletedCallback;
    private NetworkSceneManager.OnSynchronizeCompleteDelegateHandler synchronizeCompleteCallback;
    private Action<NetworkLobbyState> lobbyChangedCallback;
    private Action<NetworkLobbyState> lobbyStartCallback;

    private enum PendingStart
    {
        None,
        Host,
        Client
    }

    private enum SceneLoadPurpose
    {
        None,
        TeamLobby,
        InGame,
        Map
    }

    private PendingStart pendingStart;
    private int activeGeneration;

    public static NetworkGameSession Instance => instance;
    public static bool IsNetworkSessionActive => networkSessionActive;
    public static bool IsCombatRunning => instance != null && instance.localCombatReady &&
        instance.lobby != null && instance.lobby.Snapshot.Phase == NetworkLobbyPhase.Running;

    public event Action<NetworkGameSession> Changed;

    public NetworkGameSessionState State => state;

    // 로컬 로딩 UI용 진행도. InGame 로드 0.1, Map 로드 0.1~0.9, 모든 참가자 준비(Running) 1.
    public float MatchLoadingProgress
    {
        get
        {
            if (IsCombatRunning) return 1f;
            Scene map = SceneManager.GetSceneByName(MapSceneName);
            if (map.IsValid() && map.isLoaded) return 0.9f;
            if (mapLoadOperation != null)
                return 0.1f + 0.8f * Mathf.Clamp01(mapLoadOperation.progress / 0.9f);
            return 0.1f;
        }
    }
    public string Status => status;
    public string Error => error;
    public string DisplayName => displayName;
    public string RoomName => roomName;
    public Guid RoomInstanceId => roomInstanceId;
    public bool IsHost => isHost;
    public NetworkLobbyState Lobby => lobby;
    public LanRoomDiscovery Discovery => discovery;
    public NetworkManager NetworkManager => manager;
    public bool CanStart => state == NetworkGameSessionState.Lobby && isHost && !admissionLocked &&
        pendingAdmissions.Count == 0 && expectedSceneName == null && queuedSceneName == null &&
        lobby != null && lobby.CanStart && manager != null && manager.IsListening && manager.IsServer;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        instance = null;
        networkSessionActive = false;
    }

    private void Awake()
    {
        if (instance != null && instance != this)
        {
            enabled = false;
            Destroy(gameObject);
            return;
        }

        instance = this;
        manager = GetComponent<NetworkManager>();
        transport = GetComponent<UnityTransport>();
        discovery = GetComponent<LanRoomDiscovery>();
        displayName = LocalPlayerProfile.LoadDisplayName();
        if (!LocalPlayerProfile.TryValidateDisplayName(displayName, out displayName, out _))
            displayName = LocalPlayerProfile.DefaultDisplayName;

        DontDestroyOnLoad(gameObject);
        SceneManager.sceneLoaded += HandleLocalSceneLoaded;
        SceneManager.sceneUnloaded += HandleLocalSceneUnloaded;
        discovery.StatusChanged += HandleDiscoveryStatusChanged;
        discovery.RoomsChanged += HandleDiscoveryRoomsChanged;
    }

    private void Update()
    {
        if (instance != this) return;

        double now = Time.unscaledTimeAsDouble;
        if (!string.IsNullOrEmpty(deferredFailure) && state != NetworkGameSessionState.Stopping)
        {
            string failure = deferredFailure;
            deferredFailure = null;
            BeginStop(failure, true, false);
        }

        if (pendingBrowserStart && discovery.IsFullyStopped)
        {
            pendingBrowserStart = false;
            StartBrowsingNow();
        }

        if (pendingStart != PendingStart.None && discovery.IsFullyStopped && state != NetworkGameSessionState.Stopping)
            StartPendingNetwork(now);

        CheckPendingAdmissionDeadlines(now);
        CheckLifecycleDeadlines(now);
        ProcessCompletedNetworkSceneLoad();
        ProcessQueuedNetworkSceneLoad();
        TryPrepareLocalCombat();
        TickMatchFlow();

        if (state == NetworkGameSessionState.Stopping)
            AdvanceCleanup(now);
    }

    private void OnApplicationQuit()
    {
        quitting = true;
        if (networkSessionActive)
            BeginStop(null, false, true);
        else
            discovery.Stop();
    }

    private void OnDestroy()
    {
        if (instance != this) return;

        SceneManager.sceneLoaded -= HandleLocalSceneLoaded;
        SceneManager.sceneUnloaded -= HandleLocalSceneUnloaded;
        if (discovery != null)
        {
            discovery.StatusChanged -= HandleDiscoveryStatusChanged;
            discovery.RoomsChanged -= HandleDiscoveryRoomsChanged;
            discovery.Stop();
        }

        if (networkSessionActive && manager != null && manager.IsListening && !manager.ShutdownInProgress)
        {
            try
            {
                EndAllPlayerSkillSessions();
            }
            catch (Exception exception)
            {
                Debug.LogException(exception, this);
            }
            manager.Shutdown();
        }
        UnsubscribeNetworkCallbacks();
        UnsubscribeNetworkSceneEvents();
        DetachLobby();
        instance = null;
    }

    public void OpenBrowser()
    {
        if (!CanStartUserOperation()) return;
        if (cleanupFaulted)
        {
            SetState(NetworkGameSessionState.Entry, "Room restart is blocked because cleanup previously faulted.", error);
            return;
        }

        error = null;
        displayName = LocalPlayerProfile.LoadDisplayName();
        if (!discovery.IsFullyStopped)
        {
            pendingBrowserStart = true;
            discovery.Stop();
            SetState(NetworkGameSessionState.Browsing, "Stopping the previous LAN discovery worker.", null);
            return;
        }
        StartBrowsingNow();
    }

    public void BackToStart()
    {
        if (teardownStarted || state == NetworkGameSessionState.Stopping)
            return;

        if (networkSessionActive)
        {
            returnToEntry = true;
            returnToBrowser = false;
            BeginStop(null, false, false);
            return;
        }

        pendingBrowserStart = false;
        returnToEntry = true;
        returnToBrowser = false;
        if (!discovery.IsFullyStopped)
        {
            teardownStarted = true;
            teardownDeadline = Time.unscaledTimeAsDouble + CleanupTimeout;
            discovery.Stop();
            SetState(NetworkGameSessionState.Stopping, "Stopping LAN discovery.", null);
            return;
        }
        SetState(NetworkGameSessionState.Entry, "Ready.", null);
    }

    public void CreateRoom(string requestedRoomName)
    {
        if (!CanStartUserOperation()) return;
        if (cleanupFaulted)
        {
            SetState(state, "Room creation is blocked because cleanup previously faulted.", error);
            return;
        }
        if (manager == null || manager.IsListening || manager.ShutdownInProgress)
        {
            SetState(state, "The previous NGO session has not fully stopped.", "NetworkManager is still active or shutting down.");
            return;
        }

        if (!LocalPlayerProfile.TryValidateRoomName(requestedRoomName, out string normalizedRoomName, out string nameError))
        {
            SetState(state, nameError, nameError);
            return;
        }
        if (!ValidateLocalDisplayName(out string validatedDisplayName, out string profileError))
        {
            SetState(state, profileError, profileError);
            return;
        }
        if (!ValidatePlayerPrefabs(out string prefabError))
        {
            SetState(state, prefabError, prefabError);
            return;
        }
        generation++;
        activeGeneration = generation;
        networkSessionActive = true;
        isHost = true;
        displayName = validatedDisplayName;
        roomName = normalizedRoomName;
        roomInstanceId = Guid.NewGuid();
        if (!RoomConnectionPayload.TryEncode(roomInstanceId, displayName, out connectionPayload, out string payloadError))
        {
            BeginStop(payloadError, true, false);
            return;
        }

        pendingStart = PendingStart.Host;
        pendingBrowserStart = false;
        returnToBrowser = true;
        returnToEntry = false;
        admissionOpen = false;
        admissionLocked = false;
        teardownStarted = false;
        localSynchronizationPending = false;
        localCombatPrepared = false;
        localCombatReady = false;
        serverStarted = false;
        ClearRunRecords();
        CapturePreexistingScenes();
        actionDeadline = Time.unscaledTimeAsDouble + CleanupTimeout;
        error = null;
        if (!discovery.IsFullyStopped) discovery.Stop();
        SetState(NetworkGameSessionState.Creating, "Preparing the host room.", null);
        if (discovery.IsFullyStopped)
            StartPendingNetwork(Time.unscaledTimeAsDouble);
    }

    public void JoinRoom(LanDiscoveredRoom room)
    {
        if (!CanStartUserOperation()) return;
        if (cleanupFaulted)
        {
            SetState(state, "Joining is blocked because cleanup previously faulted.", error);
            return;
        }
        if (manager == null || manager.IsListening || manager.ShutdownInProgress)
        {
            SetState(state, "The previous NGO session has not fully stopped.", "NetworkManager is still active or shutting down.");
            return;
        }
        if (!ValidateDiscoveredRoom(room, out string roomError))
        {
            SetState(state, roomError, roomError);
            return;
        }
        if (!ValidateLocalDisplayName(out string validatedDisplayName, out string profileError))
        {
            SetState(state, profileError, profileError);
            return;
        }
        if (!ValidatePlayerPrefabs(out string prefabError))
        {
            SetState(state, prefabError, prefabError);
            return;
        }
        if (!RoomConnectionPayload.TryEncode(room.RoomInstanceId, validatedDisplayName,
            out byte[] payload, out string payloadError))
        {
            SetState(state, payloadError, payloadError);
            return;
        }

        generation++;
        activeGeneration = generation;
        networkSessionActive = true;
        isHost = false;
        displayName = validatedDisplayName;
        roomName = room.RoomName;
        roomInstanceId = room.RoomInstanceId;
        connectionPayload = payload;
        joinAddress = room.Address;
        joinPort = room.GamePort;
        pendingStart = PendingStart.Client;
        pendingBrowserStart = false;
        returnToBrowser = true;
        returnToEntry = false;
        admissionOpen = false;
        admissionLocked = false;
        teardownStarted = false;
        localSynchronizationPending = false;
        localCombatPrepared = false;
        localCombatReady = false;
        serverStarted = false;
        ClearRunRecords();
        CapturePreexistingScenes();
        actionDeadline = Time.unscaledTimeAsDouble + CleanupTimeout;
        error = null;
        if (!discovery.IsFullyStopped) discovery.Stop();
        SetState(NetworkGameSessionState.Joining, "Stopping LAN discovery before connecting.", null);
        if (discovery.IsFullyStopped)
            StartPendingNetwork(Time.unscaledTimeAsDouble);
    }

    public void Leave()
    {
        if (state == NetworkGameSessionState.Stopping) return;
        if (!networkSessionActive)
        {
            if (state == NetworkGameSessionState.Browsing)
                BackToStart();
            return;
        }

        returnToBrowser = true;
        returnToEntry = false;
        BeginStop(null, true, false);
    }

    public void SelectChampion(PlayerCharacterId champion)
    {
        if (state != NetworkGameSessionState.Lobby || lobby == null || !Enum.IsDefined(typeof(PlayerCharacterId), champion))
            return;
        lobby.SelectChampion(champion);
    }

    public void SetReady(bool ready)
    {
        if (state != NetworkGameSessionState.Lobby || lobby == null)
            return;
        lobby.SetReady(ready);
    }

    public void RequestStart()
    {
        if (state != NetworkGameSessionState.Lobby || lobby == null)
            return;
        lobby.RequestStart();
    }

    private string deferredFailure;

    private bool CanStartUserOperation()
    {
        if (instance != this || state == NetworkGameSessionState.Stopping || networkSessionActive)
            return false;
        return state == NetworkGameSessionState.Entry || state == NetworkGameSessionState.Browsing;
    }

    private void StartBrowsingNow()
    {
        if (cleanupFaulted)
        {
            SetState(NetworkGameSessionState.Entry, "Room restart is blocked because cleanup previously faulted.", error);
            return;
        }

        if (!LocalPlayerProfile.TryValidateDisplayName(LocalPlayerProfile.LoadDisplayName(), out displayName, out string profileError))
        {
            displayName = LocalPlayerProfile.DefaultDisplayName;
            SetState(NetworkGameSessionState.Browsing, profileError, profileError);
        }

        if (!discovery.StartBrowsing())
        {
            error = discovery.Status == null ? "LAN discovery could not start." : discovery.Status.Message;
            SetState(NetworkGameSessionState.Browsing, error, error);
            return;
        }

        pendingBrowserStart = false;
        SetState(NetworkGameSessionState.Browsing, discovery.Status.Message, error);
    }

    private bool ValidateLocalDisplayName(out string normalizedName, out string validationError)
    {
        normalizedName = null;
        validationError = null;
        string loaded = LocalPlayerProfile.LoadDisplayName();
        if (!LocalPlayerProfile.TryValidateDisplayName(loaded, out normalizedName, out validationError))
            return false;
        displayName = normalizedName;
        return true;
    }

    private bool ValidatePlayerPrefabs(out string validationError)
    {
        validationError = null;
        if (playerPrefabs == null || playerPrefabs.Length != 3)
        {
            validationError = "Assign exactly three player prefabs indexed by Sword, Ninja and Wizard.";
            return false;
        }

        for (int index = 0; index < playerPrefabs.Length; index++)
        {
            GameObject prefab = playerPrefabs[index];
            if (prefab == null || !prefab.TryGetComponent(out PlayerCharacter character) ||
                (int)character.CharacterId != index || !prefab.TryGetComponent(out NetworkObject networkObject) ||
                networkObject.IsSpawned || manager == null || !manager.NetworkConfig.Prefabs.Contains(prefab))
            {
                validationError = "Player prefab slot " + index + " must match its PlayerCharacterId and be registered with NGO.";
                return false;
            }
        }
        return true;
    }

    private static bool ValidateDiscoveredRoom(LanDiscoveredRoom room, out string validationError)
    {
        validationError = null;
        if (room == null || room.RoomInstanceId == Guid.Empty || !room.IsJoinable || room.Capacity != 2 ||
            room.Occupancy < 1 || room.Occupancy >= room.Capacity || room.GamePort == 0 || room.Address == null ||
            room.Address.AddressFamily != AddressFamily.InterNetwork || room.Address.Equals(IPAddress.Any) ||
            room.Address.Equals(IPAddress.Broadcast) || IsIpv4Multicast(room.Address))
        {
            validationError = "The selected LAN room is full, stale, or has an invalid IPv4 endpoint.";
            return false;
        }

        if (!LocalPlayerProfile.TryValidateRoomName(room.RoomName, out string normalizedName, out validationError) ||
            !string.Equals(normalizedName, room.RoomName, StringComparison.Ordinal))
        {
            validationError = validationError ?? "The selected LAN room name is not canonical.";
            return false;
        }
        return true;
    }

    private static bool IsIpv4Multicast(IPAddress address)
    {
        byte[] bytes = address.GetAddressBytes();
        return bytes.Length == 4 && bytes[0] >= 224 && bytes[0] <= 239;
    }

    private void StartPendingNetwork(double now)
    {
        PendingStart start = pendingStart;
        pendingStart = PendingStart.None;
        if (activeGeneration != generation || !networkSessionActive || cleanupFaulted)
            return;
        if (manager == null || manager.IsListening || manager.ShutdownInProgress)
        {
            BeginStop("NetworkManager did not finish its previous shutdown.", true, false);
            return;
        }

        manager.NetworkConfig.EnableSceneManagement = true;
        manager.NetworkConfig.ConnectionApproval = true;
        manager.NetworkConfig.PlayerPrefab = null;
        manager.NetworkConfig.LoadSceneTimeOut = (int)SceneLoadTimeout;
        manager.NetworkConfig.ConnectionData = connectionPayload;
        if (start == PendingStart.Host)
        {
            transport.SetConnectionData("127.0.0.1", GamePort, "0.0.0.0");
        }
        else if (start == PendingStart.Client)
        {
            transport.SetConnectionData(joinAddress.ToString(), joinPort);
        }
        else
        {
            BeginStop("The requested room operation was cancelled.", true, false);
            return;
        }

        SubscribeNetworkCallbacks(activeGeneration);
        SetState(state, start == PendingStart.Host ? "Starting host and TeamLobby." : "Connecting to the selected room.", null);
        bool started = start == PendingStart.Host ? manager.StartHost() : manager.StartClient();
        if (!started)
        {
            BeginStop(start == PendingStart.Host
                ? "The host could not bind or start NGO on UDP port 7777."
                : "The NGO client could not start.", true, false);
            return;
        }

        if (start == PendingStart.Client && state != NetworkGameSessionState.Stopping)
        {
            localSynchronizationPending = true;
            synchronizationDeadline = now + LobbySynchronizationTimeout;
        }
    }

    private void SubscribeNetworkCallbacks(int callbackGeneration)
    {
        UnsubscribeNetworkCallbacks();
        approvalCallback = (request, response) =>
        {
            if (callbackGeneration == generation) ApproveConnection(request, response);
            else RejectConnection(response, "This room session has expired.");
        };
        clientConnectedCallback = clientId =>
        {
            if (callbackGeneration == generation) HandleClientConnected(clientId);
        };
        clientDisconnectedCallback = clientId =>
        {
            if (callbackGeneration == generation) HandleClientDisconnected(clientId);
        };
        transportFailureCallback = () =>
        {
            if (callbackGeneration == generation && state != NetworkGameSessionState.Stopping)
                BeginStop("The UDP transport failed. Check port 7777 and the peer connection.", true, false);
        };
        serverStartedCallback = () =>
        {
            if (callbackGeneration == generation) HandleServerStarted();
        };
        clientStartedCallback = () =>
        {
            if (callbackGeneration == generation) HandleClientStarted();
        };

        manager.ConnectionApprovalCallback = approvalCallback;
        manager.OnClientConnectedCallback += clientConnectedCallback;
        manager.OnClientDisconnectCallback += clientDisconnectedCallback;
        manager.OnTransportFailure += transportFailureCallback;
        manager.OnServerStarted += serverStartedCallback;
        manager.OnClientStarted += clientStartedCallback;
    }

    private void UnsubscribeNetworkCallbacks()
    {
        if (manager == null) return;
        if (clientConnectedCallback != null) manager.OnClientConnectedCallback -= clientConnectedCallback;
        if (clientDisconnectedCallback != null) manager.OnClientDisconnectCallback -= clientDisconnectedCallback;
        if (transportFailureCallback != null) manager.OnTransportFailure -= transportFailureCallback;
        if (serverStartedCallback != null) manager.OnServerStarted -= serverStartedCallback;
        if (clientStartedCallback != null) manager.OnClientStarted -= clientStartedCallback;
        if (approvalCallback != null && manager.ConnectionApprovalCallback == approvalCallback)
            manager.ConnectionApprovalCallback = null;
        approvalCallback = null;
        clientConnectedCallback = null;
        clientDisconnectedCallback = null;
        transportFailureCallback = null;
        serverStartedCallback = null;
        clientStartedCallback = null;
    }

    private void HandleServerStarted()
    {
        if (state == NetworkGameSessionState.Stopping || !manager.IsServer)
            return;
        serverStarted = true;
        try
        {
            ConfigureNetworkSceneManager(true);
            queuedSceneName = LobbySceneName;
            expectedScenePurpose = SceneLoadPurpose.TeamLobby;
            actionDeadline = Time.unscaledTimeAsDouble + SceneLoadTimeout;
            SetState(NetworkGameSessionState.Creating, "Host started; loading the shared TeamLobby.", null);
        }
        catch (Exception exception)
        {
            deferredFailure = "Could not configure NGO additive scene synchronization: " + exception.Message;
        }
    }

    private void HandleClientStarted()
    {
        if (state == NetworkGameSessionState.Stopping)
            return;
        try
        {
            ConfigureNetworkSceneManager(manager.IsServer);
            if (!manager.IsServer)
            {
                localSynchronizationPending = true;
                synchronizationDeadline = Time.unscaledTimeAsDouble + LobbySynchronizationTimeout;
                SetState(NetworkGameSessionState.Joining, "Connected; synchronizing the shared lobby.", null);
            }
        }
        catch (Exception exception)
        {
            deferredFailure = "Could not configure NGO scene synchronization: " + exception.Message;
        }
    }

    private void ConfigureNetworkSceneManager(bool asServer)
    {
        NetworkSceneManager sceneManager = manager.SceneManager;
        if (sceneManager == null)
            throw new InvalidOperationException("NGO scene management is unavailable.");

        if (networkSceneManager != sceneManager)
        {
            UnsubscribeNetworkSceneEvents();
            networkSceneManager = sceneManager;
            int callbackGeneration = generation;
            sceneLoadCallback = (clientId, sceneName, mode, operation) =>
            {
                if (callbackGeneration == generation) HandleNetworkSceneLoadStarted(clientId, sceneName, mode, operation);
            };
            loadCompletedCallback = (sceneName, mode, completed, timedOut) =>
            {
                if (callbackGeneration == generation) HandleNetworkLoadCompleted(sceneName, mode, completed, timedOut);
            };
            synchronizeCompleteCallback = clientId =>
            {
                if (callbackGeneration == generation) HandleSynchronizeComplete(clientId);
            };
            networkSceneManager.OnLoad += sceneLoadCallback;
            networkSceneManager.OnLoadEventCompleted += loadCompletedCallback;
            networkSceneManager.OnSynchronizeComplete += synchronizeCompleteCallback;
        }

        sceneManager.VerifySceneBeforeLoading = VerifySceneBeforeLoading;
        sceneManager.VerifySceneBeforeUnloading = VerifySceneBeforeUnloading;
        sceneManager.PostSynchronizationSceneUnloading = true;
        if (asServer)
            sceneManager.SetClientSynchronizationMode(LoadSceneMode.Additive);
    }

    private void UnsubscribeNetworkSceneEvents()
    {
        if (networkSceneManager != null)
        {
            if (sceneLoadCallback != null) networkSceneManager.OnLoad -= sceneLoadCallback;
            if (loadCompletedCallback != null) networkSceneManager.OnLoadEventCompleted -= loadCompletedCallback;
            if (synchronizeCompleteCallback != null) networkSceneManager.OnSynchronizeComplete -= synchronizeCompleteCallback;
        }
        networkSceneManager = null;
        sceneLoadCallback = null;
        loadCompletedCallback = null;
        synchronizeCompleteCallback = null;
    }

    private bool VerifySceneBeforeLoading(int buildIndex, string sceneName, LoadSceneMode mode)
    {
        return mode == LoadSceneMode.Additive && sceneName != EntrySceneName &&
               (sceneName == LobbySceneName || sceneName == InGameSceneName || sceneName == MapSceneName);
    }

    private bool VerifySceneBeforeUnloading(Scene scene)
    {
        // DefaultSceneManagerHandler unloads when this predicate returns true.
        return scene.IsValid() && scene.name != EntrySceneName;
    }

    private void ProcessQueuedNetworkSceneLoad()
    {
        if (string.IsNullOrEmpty(queuedSceneName) || state == NetworkGameSessionState.Stopping)
            return;

        string sceneName = queuedSceneName;
        queuedSceneName = null;
        if (!manager.IsListening || !manager.IsServer || networkSceneManager == null ||
            expectedSceneName != null || !Application.CanStreamedLevelBeLoaded(sceneName))
        {
            BeginStop("Required network scene could not be loaded: " + sceneName + ".", true, false);
            return;
        }

        if (SceneManager.GetSceneByName(sceneName).IsValid() && SceneManager.GetSceneByName(sceneName).isLoaded)
        {
            BeginStop("Network scene was already loaded before its session-owned load: " + sceneName + ".", true, false);
            return;
        }

        SceneLoadPurpose purpose = expectedScenePurpose;
        if ((sceneName == LobbySceneName && purpose != SceneLoadPurpose.TeamLobby) ||
            (sceneName == InGameSceneName && purpose != SceneLoadPurpose.InGame) ||
            (sceneName == MapSceneName && purpose != SceneLoadPurpose.Map))
        {
            BeginStop("The queued network scene did not match the active lifecycle phase.", true, false);
            return;
        }

        expectedSceneName = sceneName;
        pendingSceneLoadComplete = false;
        pendingSceneLoadFailed = false;
        pendingSceneLoadFailure = null;
        completedSceneClients.Clear();
        actionDeadline = Time.unscaledTimeAsDouble + SceneLoadTimeout;
        SceneEventProgressStatus loadStatus;
        try
        {
            loadStatus = networkSceneManager.LoadScene(sceneName, LoadSceneMode.Additive);
        }
        catch (Exception exception)
        {
            expectedSceneName = null;
            BeginStop("NGO failed to queue " + sceneName + ": " + exception.Message, true, false);
            return;
        }

        if (loadStatus != SceneEventProgressStatus.Started)
        {
            expectedSceneName = null;
            BeginStop("NGO did not start the " + sceneName + " load (" + loadStatus + ").", true, false);
            return;
        }
        if (sceneName == InGameSceneName)
            SetState(NetworkGameSessionState.LoadingMatch, "Loading InGame on every participant.", null);
        else if (sceneName == MapSceneName)
            SetState(NetworkGameSessionState.LoadingMatch, "Loading Map on every participant.", null);
    }

    private void HandleNetworkSceneLoadStarted(ulong clientId, string sceneName, LoadSceneMode mode,
        AsyncOperation operation)
    {
        if (mode != LoadSceneMode.Additive || !IsOwnedSceneName(sceneName) || operation == null)
            return;
        if (sceneName == MapSceneName) mapLoadOperation = operation;
        if (pendingSceneLoads.Contains(operation)) return;
        pendingSceneLoads.Add(operation);
    }

    private void HandleNetworkLoadCompleted(string sceneName, LoadSceneMode mode,
        List<ulong> clientsCompleted, List<ulong> clientsTimedOut)
    {
        if (!manager.IsServer || mode != LoadSceneMode.Additive || sceneName != expectedSceneName ||
            state == NetworkGameSessionState.Stopping)
        {
            return;
        }

        completedSceneClients.Clear();
        if (clientsCompleted != null)
        {
            for (int i = 0; i < clientsCompleted.Count; i++)
                completedSceneClients.Add(clientsCompleted[i]);
        }

        if (clientsTimedOut != null && clientsTimedOut.Count > 0)
        {
            pendingSceneLoadFailed = true;
            pendingSceneLoadFailure = "One or more participants timed out while loading " + sceneName + ".";
        }
        pendingSceneLoadComplete = true;
    }

    private void ProcessCompletedNetworkSceneLoad()
    {
        if (!pendingSceneLoadComplete || state == NetworkGameSessionState.Stopping)
            return;

        string completedSceneName = expectedSceneName;
        SceneLoadPurpose completedPurpose = expectedScenePurpose;
        pendingSceneLoadComplete = false;
        expectedSceneName = null;
        if (pendingSceneLoadFailed)
        {
            BeginStop(pendingSceneLoadFailure ?? "A participant failed to load " + completedSceneName + ".", true, false);
            return;
        }

        IEnumerable<ulong> expectedClients = completedPurpose == SceneLoadPurpose.TeamLobby
            ? new[] { manager.LocalClientId }
            : frozenClientIds;
        foreach (ulong clientId in expectedClients)
        {
            if (!completedSceneClients.Contains(clientId))
            {
                BeginStop("Not every frozen participant completed " + completedSceneName + ".", true, false);
                return;
            }
        }

        if (completedPurpose == SceneLoadPurpose.TeamLobby)
        {
            CompleteHostLobbySetup();
        }
        else if (completedPurpose == SceneLoadPurpose.InGame)
        {
            expectedScenePurpose = SceneLoadPurpose.Map;
            queuedSceneName = MapSceneName;
        }
        else if (completedPurpose == SceneLoadPurpose.Map)
        {
            CompleteSharedMapLoad();
        }
    }

    private void CompleteHostLobbySetup()
    {
        NetworkLobbyState loadedLobby = FindLobbyState();
        if (loadedLobby == null || !loadedLobby.IsServer || !loadedLobby.IsSpawned ||
            !loadedLobby.InitializeHost(roomInstanceId, roomName, manager.LocalClientId, displayName))
        {
            BeginStop("TeamLobby must contain exactly one spawned NetworkLobbyState that can initialize the room.", true, false);
            return;
        }

        AttachLobby(loadedLobby);
        admissionOpen = true;
        SetState(NetworkGameSessionState.Lobby, "Room created. Select a champion to begin.", null);
        if (!discovery.StartHosting(roomInstanceId, roomName, GamePort, 1, true, out string discoveryError))
        {
            BeginStop("The LAN room responder could not start: " + discoveryError, true, false);
            return;
        }
        UpdateHostAdvertisement();
    }

    private void HandleSynchronizeComplete(ulong clientId)
    {
        if (state == NetworkGameSessionState.Stopping || !networkSessionActive)
            return;

        if (manager.IsServer)
        {
            if (!pendingAdmissions.TryGetValue(clientId, out PendingAdmission pending))
                return;
            pending.Synchronized = true;
            pendingAdmissions[clientId] = pending;
            TryCommitPendingAdmission(clientId);
            return;
        }

        if (clientId != manager.LocalClientId || !localSynchronizationPending)
            return;
        NetworkLobbyState synchronizedLobby = FindLobbyState();
        if (synchronizedLobby == null)
        {
            BeginStop("The connected server did not synchronize the expected lobby.", true, false);
            return;
        }

        NetworkLobbySnapshot synchronizedSnapshot = synchronizedLobby.Snapshot;
        if (synchronizedSnapshot.RoomInstanceId != roomInstanceId ||
            synchronizedSnapshot.Phase != NetworkLobbyPhase.Lobby ||
            !synchronizedSnapshot.Member0.Occupied || !synchronizedSnapshot.Member0.IsHost ||
            synchronizedSnapshot.Member0.ClientId != NetworkManager.ServerClientId)
        {
            BeginStop("The connected server did not synchronize the expected lobby state.", true, false);
            return;
        }
        if (!LocalPlayerProfile.TryValidateRoomName(synchronizedSnapshot.RoomName,
            out string synchronizedRoomName, out _) ||
            !string.Equals(synchronizedRoomName, synchronizedSnapshot.RoomName, StringComparison.Ordinal))
        {
            BeginStop("The connected server advertised an invalid room name.", true, false);
            return;
        }

        localSynchronizationPending = false;
        roomName = synchronizedRoomName;
        AttachLobby(synchronizedLobby);
        SetState(NetworkGameSessionState.Lobby, "Joined " + roomName + ". Choose a champion and ready up.", null);
    }

    private void ApproveConnection(NetworkManager.ConnectionApprovalRequest request,
        NetworkManager.ConnectionApprovalResponse response)
    {
        response.Approved = false;
        response.CreatePlayerObject = false;
        response.Pending = false;
        response.Reason = "This room is not accepting connections.";

        if (!manager.IsServer || !networkSessionActive || state == NetworkGameSessionState.Stopping)
            return;
        if (!RoomConnectionPayload.TryDecode(request.Payload, out Guid payloadRoomId, out string remoteDisplayName,
            out string payloadError))
        {
            response.Reason = payloadError ?? "The room connection payload is invalid.";
            return;
        }
        if (payloadRoomId != roomInstanceId)
        {
            response.Reason = "The room instance is stale or has changed.";
            return;
        }

        if (request.ClientNetworkId == NetworkManager.ServerClientId)
        {
            if (!isHost || !string.Equals(remoteDisplayName, displayName, StringComparison.Ordinal))
            {
                response.Reason = "The host profile did not match the room setup.";
                return;
            }
            response.Approved = true;
            response.Reason = string.Empty;
            return;
        }

        if (!admissionOpen || admissionLocked || state != NetworkGameSessionState.Lobby || lobby == null ||
            lobby.Snapshot.Phase != NetworkLobbyPhase.Lobby)
        {
            response.Reason = "The room is not accepting new lobby members.";
            return;
        }

        if (pendingAdmissions.TryGetValue(request.ClientNetworkId, out PendingAdmission existing))
        {
            if (!string.Equals(existing.DisplayName, remoteDisplayName, StringComparison.Ordinal))
            {
                response.Reason = "A different admission is already pending for this client.";
                return;
            }
        }
        else
        {
            if (lobby.Snapshot.MemberCount + pendingAdmissions.Count >= 2)
            {
                response.Reason = "The room is full.";
                return;
            }
            pendingAdmissions.Add(request.ClientNetworkId,
                new PendingAdmission(remoteDisplayName, Time.unscaledTimeAsDouble + LobbySynchronizationTimeout));
            UpdateHostAdvertisement();
        }

        response.Approved = true;
        response.CreatePlayerObject = false;
        response.Reason = string.Empty;
    }

    private static void RejectConnection(NetworkManager.ConnectionApprovalResponse response, string reason)
    {
        response.Approved = false;
        response.CreatePlayerObject = false;
        response.Pending = false;
        response.Reason = reason;
    }

    private void HandleClientConnected(ulong clientId)
    {
        if (state == NetworkGameSessionState.Stopping)
            return;
        if (!manager.IsServer)
        {
            if (clientId == manager.LocalClientId && state == NetworkGameSessionState.Joining)
                SetState(NetworkGameSessionState.Joining, "Connected; waiting for lobby synchronization.", null);
            return;
        }
        if (clientId == NetworkManager.ServerClientId)
            return;
        if (!pendingAdmissions.TryGetValue(clientId, out PendingAdmission pending))
        {
            if (admissionLocked || state != NetworkGameSessionState.Lobby)
                manager.DisconnectClient(clientId, "The room is no longer accepting players.");
            return;
        }

        pending.Connected = true;
        pendingAdmissions[clientId] = pending;
        TryCommitPendingAdmission(clientId);
    }

    private void TryCommitPendingAdmission(ulong clientId)
    {
        if (!pendingAdmissions.TryGetValue(clientId, out PendingAdmission pending) ||
            !pending.Connected || !pending.Synchronized)
        {
            return;
        }
        if (state != NetworkGameSessionState.Lobby || admissionLocked || lobby == null ||
            !manager.ConnectedClients.ContainsKey(clientId) || lobby.Snapshot.MemberCount >= 2)
        {
            pendingAdmissions.Remove(clientId);
            manager.DisconnectClient(clientId, "The room changed while synchronization was completing.");
            return;
        }

        if (!lobby.AddSynchronizedMember(clientId, pending.DisplayName))
        {
            pendingAdmissions.Remove(clientId);
            manager.DisconnectClient(clientId, "The server could not commit this lobby membership.");
            return;
        }

        pendingAdmissions.Remove(clientId);
        UpdateHostAdvertisement();
        SetState(NetworkGameSessionState.Lobby, pending.DisplayName + " joined the room.", null);
    }

    private void HandleClientDisconnected(ulong clientId)
    {
        if (!networkSessionActive)
            return;

        if (!manager.IsServer)
        {
            if (state != NetworkGameSessionState.Stopping)
            {
                string reason = string.IsNullOrWhiteSpace(manager.DisconnectReason)
                    ? "The connection to the host closed."
                    : "The room connection closed: " + manager.DisconnectReason;
                BeginStop(reason, true, false);
            }
            return;
        }

        if (clientId == NetworkManager.ServerClientId)
        {
            if (state != NetworkGameSessionState.Stopping)
                BeginStop("The local host disconnected.", true, false);
            return;
        }

        if (pendingAdmissions.Remove(clientId))
        {
            if (state == NetworkGameSessionState.Lobby)
                UpdateHostAdvertisement();
            return;
        }

        if (state == NetworkGameSessionState.LoadingMatch)
        {
            BeginStop("A participant disconnected while the shared match was loading.", true, false);
            return;
        }

        if (state == NetworkGameSessionState.Lobby && lobby != null)
        {
            if (lobby.RemoveMember(clientId))
                UpdateHostAdvertisement();
            return;
        }

        if (state == NetworkGameSessionState.Running)
        {
            EndPlayerSkillSession(clientId);
            if (spawnedPlayers.TryGetValue(clientId, out NetworkObject spawned) && spawned != null)
            {
                if (spawned.IsSpawned)
                    spawned.Despawn(true);
                else
                    Destroy(spawned.gameObject);
            }
            spawnedPlayers.Remove(clientId);
            matchFlow?.Remove(clientId, Time.timeAsDouble);
            if (lobby != null) lobby.RemoveMember(clientId);
            UpdateHostAdvertisement();
        }
    }

    private void CheckPendingAdmissionDeadlines(double now)
    {
        if (!manager.IsServer || pendingAdmissions.Count == 0 || state == NetworkGameSessionState.Stopping)
            return;

        List<ulong> expired = null;
        foreach (KeyValuePair<ulong, PendingAdmission> entry in pendingAdmissions)
        {
            if (now >= entry.Value.Deadline)
            {
                if (expired == null) expired = new List<ulong>(entry.Value.Connected ? 1 : 1);
                expired.Add(entry.Key);
            }
        }

        if (expired == null) return;
        for (int i = 0; i < expired.Count; i++)
        {
            ulong clientId = expired[i];
            pendingAdmissions.Remove(clientId);
            if (manager.ConnectedClients.ContainsKey(clientId))
                manager.DisconnectClient(clientId, "Lobby synchronization timed out.");
        }
        UpdateHostAdvertisement();
    }

    private void OnLobbyChanged(NetworkLobbyState changedLobby)
    {
        if (changedLobby != lobby || !networkSessionActive || state == NetworkGameSessionState.Stopping)
            return;

        NetworkLobbySnapshot current = lobby.Snapshot;
        if (current.RoomInstanceId != roomInstanceId)
        {
            deferredFailure = "The replicated lobby belongs to a different room instance.";
            return;
        }

        if (manager.IsServer && state == NetworkGameSessionState.Lobby)
            UpdateHostAdvertisement();

        if (!manager.IsServer)
        {
            if (current.Phase == NetworkLobbyPhase.LoadingMatch && state == NetworkGameSessionState.Lobby)
                SetState(NetworkGameSessionState.LoadingMatch, "The host locked the roster; loading the match.", null);
            else if (current.Phase == NetworkLobbyPhase.Running && state != NetworkGameSessionState.Running)
                SetState(NetworkGameSessionState.LoadingMatch, "Waiting for local combat services to become ready.", null);
            else if (current.Phase == NetworkLobbyPhase.Stopping)
                BeginStop("The host is closing the room.", true, false);
        }

        RaiseChanged();
    }

    private void OnLobbyStartRequested(NetworkLobbyState requestedLobby)
    {
        if (requestedLobby != lobby || state != NetworkGameSessionState.Lobby || !manager.IsServer || !isHost)
            return;
        if (pendingAdmissions.Count != 0 || expectedSceneName != null || queuedSceneName != null || admissionLocked)
        {
            SetState(state, "Start is unavailable while a teammate is still synchronizing.", null);
            return;
        }

        admissionLocked = true;
        admissionOpen = false;
        if (!lobby.TryLockForStart(out frozenRoster))
        {
            admissionLocked = false;
            admissionOpen = true;
            UpdateHostAdvertisement();
            SetState(state, "Choose a valid host champion and require every teammate to be ready.", null);
            return;
        }

        frozenClientIds.Clear();
        frozenClientIds.Add(frozenRoster.Member0.ClientId);
        if (frozenRoster.Member1.Occupied)
            frozenClientIds.Add(frozenRoster.Member1.ClientId);
        foreach (ulong clientId in frozenClientIds)
        {
            if (!manager.ConnectedClients.ContainsKey(clientId))
            {
                admissionLocked = false;
                deferredFailure = "A frozen lobby participant disconnected as Start was accepted.";
                return;
            }
        }

        discovery.Stop();
        expectedScenePurpose = SceneLoadPurpose.InGame;
        queuedSceneName = InGameSceneName;
        SetState(NetworkGameSessionState.LoadingMatch, "Roster locked. Loading the shared match.", null);
    }

    private void CompleteSharedMapLoad()
    {
        if (teardownStarted || state != NetworkGameSessionState.LoadingMatch)
            return;

        Scene map = SceneManager.GetSceneByName(MapSceneName);
        string readinessError = null;
        if (!map.IsValid() || !map.isLoaded || !TryPrepareLocalCombatServices(map, out readinessError))
        {
            BeginStop(string.IsNullOrEmpty(readinessError)
                ? "Map did not load as a valid local scene."
                : readinessError, true, false);
            return;
        }
        if (!manager.IsServer || !isHost || lobby == null || lobby.Snapshot.Phase != NetworkLobbyPhase.LoadingMatch)
        {
            BeginStop("Only the host may finalize the authoritative match roster.", true, false);
            return;
        }
        if (!SpawnFrozenPlayers(map, out string spawnError))
        {
            BeginStop(spawnError, true, false);
            return;
        }
        if (!lobby.PublishRunning())
        {
            BeginStop("The lobby could not publish the completed Running phase.", true, false);
            return;
        }

        localCombatReady = localCombatPrepared;
        SetState(NetworkGameSessionState.Running, "All participants loaded and spawned. Combat is running.", null);
        if (SpawnManager.Instance == null || !SpawnManager.Instance.BeginNetworkEnemySpawning(map, spawnedPlayers.Count))
        {
            BeginStop("The server could not start network enemy spawning.", true, false);
            return;
        }
        StartMatchFlow();
    }

    public MatchFlow MatchFlow => matchFlow;

    // 서버: 판 시작 시 명단 전원을 등록한다.
    private void StartMatchFlow()
    {
        matchFlow = new MatchFlow(reviveDelay, reviveHealthRatio, reviveInvulnerableSeconds);
        matchStartedAt = Time.timeAsDouble;
        foreach (KeyValuePair<ulong, NetworkObject> pair in spawnedPlayers)
            if (pair.Value != null && pair.Value.TryGetComponent(out PlayerHealth health))
                matchFlow.Add(pair.Key, health);
        matchFlow.MatchEnded += HandleMatchEnded;
    }

    private void TickMatchFlow()
    {
        if (matchFlow == null || matchFlow.Ended || manager == null || !manager.IsServer ||
            state != NetworkGameSessionState.Running)
            return;
        matchFlow.Tick(Time.timeAsDouble);
    }

    // 서버: 살아 있는 멤버 0명. 적 생성·AI를 멈춘다. 결과 공개·로비 복귀는 Ended 단계에서 처리한다.
    private void HandleMatchEnded()
    {
        Debug.Log($"[Match] All players are down after {matchFlow.EndedAt - matchStartedAt:F1}s. Match ended.");
        try
        {
            SpawnManager.Instance?.EndSession();
        }
        catch (Exception exception)
        {
            Debug.LogException(exception, this);
        }
    }

    private bool SpawnFrozenPlayers(Scene map, out string spawnError)
    {
        spawnError = null;
        if (teardownStarted || state != NetworkGameSessionState.LoadingMatch || !manager.IsServer || !isHost)
        {
            spawnError = "The room is stopping or no longer owns the shared spawn operation.";
            return false;
        }

        if (SpawnManager.Instance == null || !SpawnManager.Instance.TryGetNetworkSpawnPoints(map, out Transform[] spawnPoints) ||
            spawnPoints == null || spawnPoints.Length != 2)
        {
            spawnError = "The loaded Map must provide exactly two valid network player spawn points.";
            return false;
        }

        NetworkLobbyMemberSnapshot[] members = frozenRoster.Member1.Occupied
            ? new[] { frozenRoster.Member0, frozenRoster.Member1 }
            : new[] { frozenRoster.Member0 };
        for (int index = 0; index < members.Length; index++)
        {
            NetworkLobbyMemberSnapshot member = members[index];
            if (!member.Occupied || !member.Connected || !member.Synchronized ||
                !manager.ConnectedClients.ContainsKey(member.ClientId) ||
                manager.SpawnManager.GetPlayerNetworkObject(member.ClientId) != null)
            {
                spawnError = "A frozen participant is missing, disconnected, or already has a PlayerObject.";
                return false;
            }
            if (!Enum.IsDefined(typeof(PlayerCharacterId), member.Champion))
            {
                spawnError = "A frozen participant selected an invalid champion.";
                return false;
            }

            GameObject prefab = playerPrefabs[member.Champion];
            if (prefab == null || !prefab.TryGetComponent(out PlayerCharacter character) ||
                (int)character.CharacterId != member.Champion)
            {
                spawnError = "The frozen champion prefab is missing or does not match its PlayerCharacterId.";
                return false;
            }

            GameObject player = SpawnManager.Instance.SpawnNetworkPlayer(prefab,
                spawnPoints[index].position, spawnPoints[index].rotation, map);
            if (player == null)
            {
                spawnError = "SpawnManager could not create the selected network player in Map.";
                return false;
            }

            NetworkObject networkObject = player.GetComponent<NetworkObject>();
            try
            {
                if (networkObject == null || networkObject.IsSpawned || player.scene.handle != map.handle)
                    throw new InvalidOperationException("The network player is not an unspawned Map object.");
                networkObject.SpawnAsPlayerObject(member.ClientId, true);
                if (!networkObject.IsSpawned || !networkObject.IsPlayerObject ||
                    networkObject.OwnerClientId != member.ClientId || player.scene.handle != map.handle)
                {
                    throw new InvalidOperationException("NGO did not assign the expected PlayerObject in Map.");
                }
                spawnedPlayers.Add(member.ClientId, networkObject);
            }
            catch (Exception exception)
            {
                if (player.TryGetComponent(out PlayerSkillController skills))
                {
                    try
                    {
                        skills.EndSession();
                    }
                    catch (Exception skillException)
                    {
                        RecordSkillCleanupFault(skillException);
                    }
                }
                if (networkObject != null && networkObject.IsSpawned && manager.IsServer)
                {
                    try { networkObject.Despawn(true); }
                    catch (Exception despawnException) { Debug.LogException(despawnException, this); }
                }
                else if (player != null)
                {
                    Destroy(player);
                }
                spawnError = "Could not spawn a PlayerObject for client " + member.ClientId + ": " + exception.Message;
                return false;
            }
        }

        if (spawnedPlayers.Count != members.Length)
        {
            spawnError = "The complete frozen PlayerObject set was not created.";
            return false;
        }
        return true;
    }

    private void TryPrepareLocalCombat()
    {
        if (!networkSessionActive || state == NetworkGameSessionState.Stopping || localCombatReady ||
            !manager.IsListening || lobby == null)
        {
            return;
        }

        if (lobby.Snapshot.Phase != NetworkLobbyPhase.LoadingMatch &&
            lobby.Snapshot.Phase != NetworkLobbyPhase.Running)
        {
            return;
        }

        if (!localCombatPrepared)
        {
            Scene map = SceneManager.GetSceneByName(MapSceneName);
            if (!map.IsValid() || !map.isLoaded)
                return;
            if (!TryPrepareLocalCombatServices(map, out string readinessError))
            {
                BeginStop(readinessError, true, false);
                return;
            }
            localCombatPrepared = true;
        }
        if (lobby.Snapshot.Phase == NetworkLobbyPhase.Running)
        {
            localCombatReady = true;
            if (state != NetworkGameSessionState.Running)
                SetState(NetworkGameSessionState.Running, "Combat services are ready.", null);
        }
    }

    private bool TryPrepareLocalCombatServices(Scene map, out string readinessError)
    {
        readinessError = null;
        if (!map.IsValid() || !map.isLoaded || map.name != MapSceneName)
        {
            readinessError = "Map is not loaded and ready for network combat.";
            return false;
        }
        GameManager gameManager = GameManager.Instance;
        SpawnManager spawnManager = SpawnManager.Instance;
        if (gameManager == null || !gameManager.isActiveAndEnabled || spawnManager == null || !spawnManager.isActiveAndEnabled)
        {
            readinessError = "InGame must provide an active GameManager and SpawnManager before combat can begin.";
            return false;
        }
        if (!spawnManager.TryGetNetworkSpawnPoints(map, out Transform[] points) || points == null || points.Length != 2)
        {
            readinessError = "Map must contain exactly two valid PlayerSpawnPoint transforms.";
            return false;
        }
        if (SceneManager.GetActiveScene().handle != map.handle && !SceneManager.SetActiveScene(map))
        {
            readinessError = "Unity could not make the loaded Map scene active.";
            return false;
        }
        if (!gameManager.BeginNetworkCombat(map) || !gameManager.IsNetworkCombatActive || !gameManager.IsActiveSession)
        {
            readinessError = "GameManager could not begin the local network combat session.";
            return false;
        }
        return true;
    }

    private NetworkLobbyState FindLobbyState()
    {
        Scene lobbyScene = SceneManager.GetSceneByName(LobbySceneName);
        if (!lobbyScene.IsValid() || !lobbyScene.isLoaded)
            return null;

        NetworkLobbyState found = null;
        foreach (GameObject root in lobbyScene.GetRootGameObjects())
        {
            NetworkLobbyState[] states = root.GetComponentsInChildren<NetworkLobbyState>(true);
            for (int i = 0; i < states.Length; i++)
            {
                NetworkLobbyState candidate = states[i];
                if (candidate == null || !candidate.IsSpawned || candidate.gameObject.scene.handle != lobbyScene.handle)
                    continue;
                if (found != null)
                    return null;
                found = candidate;
            }
        }
        return found;
    }

    private void AttachLobby(NetworkLobbyState newLobby)
    {
        DetachLobby();
        lobby = newLobby;
        int callbackGeneration = generation;
        lobbyChangedCallback = changed =>
        {
            if (callbackGeneration == generation) OnLobbyChanged(changed);
        };
        lobbyStartCallback = requested =>
        {
            if (callbackGeneration == generation) OnLobbyStartRequested(requested);
        };
        lobby.Changed += lobbyChangedCallback;
        lobby.StartRequested += lobbyStartCallback;
    }

    private void DetachLobby()
    {
        if (lobby != null)
        {
            if (lobbyChangedCallback != null) lobby.Changed -= lobbyChangedCallback;
            if (lobbyStartCallback != null) lobby.StartRequested -= lobbyStartCallback;
        }
        lobby = null;
        lobbyChangedCallback = null;
        lobbyStartCallback = null;
    }

    private void UpdateHostAdvertisement()
    {
        if (!isHost || lobby == null || state != NetworkGameSessionState.Lobby ||
            discovery == null || discovery.Status == null ||
            (discovery.Status.State != LanRoomDiscoveryState.Starting &&
             discovery.Status.State != LanRoomDiscoveryState.Hosting))
        {
            return;
        }

        NetworkLobbySnapshot current = lobby.Snapshot;
        if (current.RoomInstanceId != roomInstanceId || current.Phase != NetworkLobbyPhase.Lobby)
            return;
        int occupancy = Mathf.Min(2, current.MemberCount + pendingAdmissions.Count);
        bool accepting = admissionOpen && !admissionLocked && occupancy < 2;
        if (!discovery.UpdateAdvertisement(roomName, (byte)occupancy, accepting, out string updateError) &&
            !string.IsNullOrEmpty(updateError))
        {
            deferredFailure = "The hosted LAN room advertisement could not be updated: " + updateError;
        }
    }

    private void HandleDiscoveryStatusChanged(LanRoomDiscoveryStatus changedStatus)
    {
        if (changedStatus == null) return;
        if (changedStatus.State == LanRoomDiscoveryState.Error)
        {
            if (isHost && networkSessionActive && state != NetworkGameSessionState.Stopping)
            {
                BeginStop(changedStatus.Message, true, false);
                return;
            }
            if (state == NetworkGameSessionState.Browsing)
            {
                error = changedStatus.Message;
                SetState(NetworkGameSessionState.Browsing, changedStatus.Message, error);
                return;
            }
        }

        if (state == NetworkGameSessionState.Browsing || state == NetworkGameSessionState.Lobby)
            SetState(state, changedStatus.Message, error);
    }

    private void HandleDiscoveryRoomsChanged(IReadOnlyList<LanDiscoveredRoom> rooms)
    {
        if (state == NetworkGameSessionState.Browsing)
            RaiseChanged();
    }

    private void HandleLocalSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (!networkSessionActive || !IsOwnedSceneName(scene.name) || scenesPresentBeforeSession.Contains(scene.handle))
            return;
        ownedScenes[scene.handle] = scene.name;
    }

    private void HandleLocalSceneUnloaded(Scene scene)
    {
        ownedScenes.Remove(scene.handle);
        if (networkSessionActive && state != NetworkGameSessionState.Stopping &&
            IsOwnedSceneName(scene.name) && scene.name != EntrySceneName)
        {
            deferredFailure = "A session-owned scene was unloaded unexpectedly: " + scene.name + ".";
        }
    }

    private void CheckLifecycleDeadlines(double now)
    {
        if (state == NetworkGameSessionState.Stopping)
            return;

        if (pendingStart != PendingStart.None && now >= actionDeadline)
        {
            BeginStop("LAN discovery did not stop in time to start the room.", true, false);
            return;
        }
        if (state == NetworkGameSessionState.Creating && serverStarted &&
            expectedSceneName == null && queuedSceneName == null && actionDeadline > 0 && now >= actionDeadline)
        {
            BeginStop("The host did not finish TeamLobby setup before its deadline.", true, false);
            return;
        }
        if (localSynchronizationPending && now >= synchronizationDeadline)
        {
            BeginStop("Lobby synchronization timed out.", true, false);
            return;
        }
        if (expectedSceneName != null && !pendingSceneLoadComplete && now >= actionDeadline)
            BeginStop("The shared " + expectedSceneName + " scene load timed out.", true, false);
    }

    private static Scene FindLoadedScene(int handle)
    {
        for (int i = 0; i < SceneManager.sceneCount; i++)
        {
            Scene scene = SceneManager.GetSceneAt(i);
            if (scene.handle == handle) return scene;
        }
        return default;
    }

    private void BeginStop(string reason, bool browseAfterStop, bool appIsQuitting)
    {
        if (teardownStarted)
            return;

        teardownStarted = true;
        pendingStart = PendingStart.None;
        pendingBrowserStart = false;
        admissionOpen = false;
        admissionLocked = true;
        localSynchronizationPending = false;
        localCombatReady = false;
        queuedSceneName = null;
        expectedScenePurpose = SceneLoadPurpose.None;
        teardownDeadline = Time.unscaledTimeAsDouble + CleanupTimeout;
        returnToBrowser = browseAfterStop && !returnToEntry;
        if (string.IsNullOrEmpty(reason) && !returnToEntry)
            error = null;
        else if (!string.IsNullOrEmpty(reason))
            error = reason;

        string capturedReason = reason;
        SetState(NetworkGameSessionState.Stopping,
            string.IsNullOrEmpty(capturedReason) ? "Leaving the room and cleaning up." : "Closing the room: " + capturedReason,
            error);

        if (lobby != null && manager != null && manager.IsServer && manager.IsListening)
        {
            try
            {
                lobby.PublishStopping();
            }
            catch (Exception exception)
            {
                RecordCleanupFault("Could not publish the stopping lobby phase: " + exception.Message);
            }
        }
        if (discovery != null)
        {
            try
            {
                discovery.Stop();
            }
            catch (Exception exception)
            {
                RecordCleanupFault("Could not stop LAN discovery: " + exception.Message);
            }
        }
        try
        {
            EndAllPlayerSkillSessions();
        }
        catch (Exception exception)
        {
            RecordCleanupFault("Could not end every player skill session: " + exception.Message);
        }
        if (manager != null && manager.IsListening && !manager.ShutdownInProgress)
        {
            try
            {
                manager.Shutdown();
            }
            catch (Exception exception)
            {
                RecordCleanupFault("NGO shutdown threw an exception: " + exception.Message);
            }
        }

        if (appIsQuitting || quitting)
            return;
    }

    private void EndAllPlayerSkillSessions()
    {
        List<PlayerSkillController> affectedSkills = new List<PlayerSkillController>(2);
        if (manager != null && manager.SpawnManager != null)
        {
            foreach (NetworkObject networkObject in manager.SpawnManager.SpawnedObjectsList)
            {
                if (networkObject == null || !networkObject.IsPlayerObject)
                    continue;
                if (networkObject.TryGetComponent(out PlayerSkillController skills))
                    AddSkillSession(affectedSkills, skills);
            }
        }

        GameManager gameManager = GameManager.Instance;
        GameObject currentPlayer = gameManager != null ? gameManager.CurrentPlayer : null;
        if (currentPlayer != null && currentPlayer.TryGetComponent(out PlayerSkillController localSkills))
            AddSkillSession(affectedSkills, localSkills);

        for (int i = 0; i < affectedSkills.Count; i++)
        {
            try
            {
                affectedSkills[i].EndSession();
            }
            catch (Exception exception)
            {
                RecordSkillCleanupFault(exception);
            }
        }

        if (gameManager != null && currentPlayer != null)
        {
            try
            {
                gameManager.UnregisterLocalPlayer(currentPlayer);
            }
            catch (Exception exception)
            {
                RecordSkillCleanupFault(exception);
            }
        }
    }

    private static void AddSkillSession(List<PlayerSkillController> sessions, PlayerSkillController skills)
    {
        if (skills != null && !sessions.Contains(skills))
            sessions.Add(skills);
    }

    private void RecordSkillCleanupFault(Exception exception)
    {
        string message = "A player's skill cleanup failed before NGO teardown: " + exception.Message;
        Debug.LogException(exception, this);
        cleanupFaulted = true;
        error = string.IsNullOrEmpty(error) ? message : error + " " + message;
        RaiseChanged();
    }

    private void EndPlayerSkillSession(ulong clientId)
    {
        if (manager == null || manager.SpawnManager == null)
            return;
        NetworkObject player = manager.SpawnManager.GetPlayerNetworkObject(clientId);
        if (player != null && player.TryGetComponent(out PlayerSkillController skills))
        {
            try
            {
                skills.EndSession();
            }
            catch (Exception exception)
            {
                RecordSkillCleanupFault(exception);
            }
        }
    }

    private void AdvanceCleanup(double now)
    {
        if (!teardownStarted) return;
        if (discovery != null && !discovery.IsFullyStopped)
        {
            CheckCleanupTimeout(now, "LAN discovery worker did not stop.");
            return;
        }
        if (manager != null && (manager.IsListening || manager.ShutdownInProgress))
        {
            CheckCleanupTimeout(now, "NetworkManager shutdown did not complete.");
            return;
        }

        RemoveCompletedSceneOperations();
        if (pendingSceneLoads.Count != 0)
        {
            CheckCleanupTimeout(now, "An NGO-owned scene load is still completing.");
            return;
        }
        if (quitting)
        {
            FinishTeardown();
            return;
        }

        if (cleanupUnloadOperation != null)
        {
            if (!cleanupUnloadOperation.isDone)
            {
                CheckCleanupTimeout(now, "A session-owned scene unload is still completing.");
                return;
            }

            Scene unloadedScene = FindLoadedScene(cleanupUnloadSceneHandle);
            if (unloadedScene.IsValid() && unloadedScene.isLoaded)
            {
                RecordCleanupFault("Unity completed an unload without releasing the session-owned scene.");
                return;
            }
            ownedScenes.Remove(cleanupUnloadSceneHandle);
            cleanupUnloadOperation = null;
            cleanupUnloadSceneHandle = 0;
        }

        if (!unloadStarted)
        {
            unloadStarted = true;
            cleanupUnloadIndex = 0;
        }

        while (cleanupUnloadIndex < SceneUnloadOrder.Length)
        {
            string sceneName = SceneUnloadOrder[cleanupUnloadIndex];
            cleanupSceneHandles.Clear();
            foreach (KeyValuePair<int, string> entry in ownedScenes)
                if (entry.Value == sceneName) cleanupSceneHandles.Add(entry.Key);
            if (cleanupSceneHandles.Count == 0)
            {
                cleanupUnloadIndex++;
                continue;
            }

            int sceneHandle = cleanupSceneHandles[0];
            Scene scene = FindLoadedScene(sceneHandle);
            if (!scene.IsValid() || !scene.isLoaded)
            {
                ownedScenes.Remove(sceneHandle);
                continue;
            }

            try
            {
                cleanupUnloadSceneHandle = sceneHandle;
                cleanupUnloadOperation = SceneManager.UnloadSceneAsync(scene);
            }
            catch (Exception exception)
            {
                RecordCleanupFault("Could not unload session-owned " + sceneName + ": " + exception.Message);
                return;
            }
            if (cleanupUnloadOperation == null)
            {
                RecordCleanupFault("Unity refused to unload session-owned " + sceneName + ".");
                return;
            }
            CheckCleanupTimeout(now, "Session-owned scene cleanup exceeded its deadline.");
            return;
        }

        if (ownedScenes.Count != 0)
        {
            RecordCleanupFault("Unrecognized session-owned scenes remain loaded.");
            return;
        }
        FinishTeardown();
    }

    private void RemoveCompletedSceneOperations()
    {
        for (int i = pendingSceneLoads.Count - 1; i >= 0; i--)
        {
            AsyncOperation operation = pendingSceneLoads[i];
            if (operation == null || operation.isDone)
                pendingSceneLoads.RemoveAt(i);
        }
    }

    private void CheckCleanupTimeout(double now, string message)
    {
        if (now >= teardownDeadline)
            RecordCleanupFault(message + " Restart remains blocked until cleanup is complete.");
    }

    private void RecordCleanupFault(string message)
    {
        if (status.StartsWith("Cleanup fault: ", StringComparison.Ordinal))
            return;
        cleanupFaulted = true;
        if (string.IsNullOrEmpty(error)) error = message;
        status = "Cleanup fault: " + message;
        SetState(NetworkGameSessionState.Stopping, status, error);
    }

    private void FinishTeardown()
    {
        UnsubscribeNetworkCallbacks();
        UnsubscribeNetworkSceneEvents();
        DetachLobby();
        pendingAdmissions.Clear();
        spawnedPlayers.Clear();
        matchFlow = null;
        frozenClientIds.Clear();
        completedSceneClients.Clear();
        pendingSceneLoads.Clear();
        mapLoadOperation = null;
        ownedScenes.Clear();
        cleanupSceneHandles.Clear();
        scenesPresentBeforeSession.Clear();
        connectionPayload = null;
        joinAddress = null;
        joinPort = 0;
        roomInstanceId = Guid.Empty;
        roomName = null;
        isHost = false;
        admissionOpen = false;
        admissionLocked = false;
        serverStarted = false;
        localCombatPrepared = false;
        localCombatReady = false;
        expectedSceneName = null;
        queuedSceneName = null;
        pendingSceneLoadComplete = false;
        pendingSceneLoadFailed = false;
        deferredFailure = null;
        networkSessionActive = false;
        teardownStarted = false;
        unloadStarted = false;
        cleanupUnloadOperation = null;

        if (manager != null && manager.NetworkConfig != null)
            manager.NetworkConfig.ConnectionData = Array.Empty<byte>();

        if (cleanupFaulted)
        {
            SetState(NetworkGameSessionState.Stopping,
                "Cleanup faulted; room restart is blocked.", error);
            return;
        }

        bool shouldBrowse = returnToBrowser;
        bool shouldEnter = returnToEntry;
        returnToBrowser = false;
        returnToEntry = false;
        if (shouldEnter)
        {
            SetState(NetworkGameSessionState.Entry, string.IsNullOrEmpty(error) ? "Ready." : error, error);
        }
        else if (shouldBrowse)
        {
            SetState(NetworkGameSessionState.Browsing,
                string.IsNullOrEmpty(error) ? "Room closed. Refreshing the LAN browser." : error, error);
            StartBrowsingNow();
        }
        else
        {
            SetState(NetworkGameSessionState.Entry, string.IsNullOrEmpty(error) ? "Ready." : error, error);
        }
    }

    private void ClearRunRecords()
    {
        pendingAdmissions.Clear();
        ownedScenes.Clear();
        pendingSceneLoads.Clear();
        mapLoadOperation = null;
        spawnedPlayers.Clear();
        matchFlow = null;
        frozenClientIds.Clear();
        completedSceneClients.Clear();
        expectedSceneName = null;
        queuedSceneName = null;
        pendingSceneLoadComplete = false;
        pendingSceneLoadFailed = false;
        pendingSceneLoadFailure = null;
        expectedScenePurpose = SceneLoadPurpose.None;
        cleanupFaulted = false;
        unloadStarted = false;
    }

    private void SetState(NetworkGameSessionState newState, string newStatus, string newError)
    {
        state = newState;
        status = newStatus ?? string.Empty;
        error = newError;
        RaiseChanged();
    }

    private void RaiseChanged()
    {
        try
        {
            Changed?.Invoke(this);
        }
        catch (Exception exception)
        {
            Debug.LogException(exception, this);
        }
    }

    private void CapturePreexistingScenes()
    {
        scenesPresentBeforeSession.Clear();
        for (int i = 0; i < SceneManager.sceneCount; i++)
            scenesPresentBeforeSession.Add(SceneManager.GetSceneAt(i).handle);
    }

    private static bool IsOwnedSceneName(string sceneName)
    {
        return sceneName == LobbySceneName || sceneName == InGameSceneName || sceneName == MapSceneName;
    }

    private struct PendingAdmission
    {
        public readonly string DisplayName;
        public readonly double Deadline;
        public bool Connected;
        public bool Synchronized;

        public PendingAdmission(string displayName, double deadline)
        {
            DisplayName = displayName;
            Deadline = deadline;
            Connected = false;
            Synchronized = false;
        }
    }
}
