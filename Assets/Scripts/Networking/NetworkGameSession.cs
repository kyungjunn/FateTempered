using System;
using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;
using UnityEngine.SceneManagement;

// 로컬 세션·UI 단계; 서버 복제 NetworkLobbyPhase와 구분.
public enum NetworkGameSessionState
{
    Entry, // 시작 화면.
    Browsing, // LAN 탐색.
    Creating, // 호스트·로비 생성 대기.
    Joining, // 연결·초기 동기화 대기.
    Lobby, // 선택·준비.
    LoadingMatch, // 명단 고정·전투 씬 로드.
    Running, // 서버 시작·로컬 준비 완료.
    Ended, // 결과 표시; 방·씬 유지.
    ReturningToLobby, // 전투 씬 해제·동일 방 복귀.
    Stopping // 연결·탐색·소유 씬 정리.
}

// 로컬 방·NGO·공유 씬·정리 수명 소유자; 표시 이름과 방 식별자 분리.
[DefaultExecutionOrder(-200)]
[RequireComponent(typeof(NetworkManager), typeof(UnityTransport), typeof(LanRoomDiscovery))]
public sealed class NetworkGameSession : MonoBehaviour
{
    // 통신·제한 시간·공유 씬 정의; 의존성 역순 언로드.
    private const ushort GamePort = 7777;
    private const double LobbySynchronizationTimeout = 30.0;
    private const double SceneLoadTimeout = 60.0;
    private const double CleanupTimeout = 30.0;
    private const string LobbySceneName = "TeamLobby";
    private const string InGameSceneName = "InGame";
    private const string MapSceneName = "Map";
    private const string EntrySceneName = "NetworkEntry";
    private static readonly string[] SceneUnloadOrder = { MapSceneName, InGameSceneName, LobbySceneName };

    // PlayerCharacterId 인덱스별 NGO 등록 프리팹.
    [SerializeField] private GameObject[] playerPrefabs = new GameObject[3];

    [Header("Downed / Revive")]
    [Tooltip("쓰러진 팀원이 부활하기까지 다른 팀원이 버텨야 하는 시간(초).")]
    [SerializeField] private float reviveDelay = 10f;
    [SerializeField, Range(0.05f, 1f)] private float reviveHealthRatio = 0.5f;
    [SerializeField] private float reviveInvulnerableSeconds = 2f;

    // 서버 판 규칙·서버 시각 및 공유 복귀 언로드 결과.
    private MatchFlow matchFlow;
    private double matchStartedAt;
    private NetworkSceneManager.OnEventCompletedDelegateHandler unloadCompletedCallback;
    private string expectedUnloadSceneName;
    private bool pendingUnloadComplete;
    private bool pendingUnloadFailed;
    private string pendingUnloadFailure;
    private bool matchResultReceived;

    // 마지막 판 결과를 받았을 때 (서버·클라이언트 공통).
    public event Action<MatchResult> MatchResultReceived;

    private static NetworkGameSession instance;
    private static bool networkSessionActive;

    // 로컬 서비스·복제 로비 참조 및 화면 표시 상태.
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
    // 로컬 역할·서버 입장 정책·로컬 동기화와 전투 준비 상태.
    private bool isHost;
    private bool admissionOpen;
    private bool admissionLocked;
    private bool serverStarted;
    private bool localSynchronizationPending;
    private bool localCombatPrepared;
    private bool localCombatReady;
    // 정리 진행·오류 고정 및 정리 후 목적지.
    private bool teardownStarted;
    private bool cleanupFaulted;
    private bool quitting;
    private bool pendingBrowserStart;
    private bool returnToBrowser;
    private bool returnToEntry;
    private bool unloadStarted;
    // NGO 콜백 결과를 Update에서 처리할 로컬 씬 전이 기록.
    private bool pendingSceneLoadComplete;
    private bool pendingSceneLoadFailed;
    private string pendingSceneLoadFailure;
    private string expectedSceneName;
    private string queuedSceneName;
    private SceneLoadPurpose expectedScenePurpose;
    // 로컬 비배율 제한 시간·이전 세션 콜백 차단 세대.
    private double actionDeadline;
    private double synchronizationDeadline;
    private double teardownDeadline;
    private int generation;
    private byte[] connectionPayload;
    private IPAddress joinAddress;
    private ushort joinPort;
    // 서버 고정 명단·전원 완료 집계·임시 입장 예약.
    private NetworkLobbySnapshot frozenRoster;
    private readonly HashSet<ulong> frozenClientIds = new HashSet<ulong>();
    private readonly HashSet<ulong> completedSceneClients = new HashSet<ulong>();
    private readonly HashSet<ulong> completedUnloadClients = new HashSet<ulong>();
    private readonly Dictionary<ulong, PendingAdmission> pendingAdmissions = new Dictionary<ulong, PendingAdmission>(1);
    // 로컬 소유 씬만 정리; 세션 이전 씬 보호.
    private readonly Dictionary<int, string> ownedScenes = new Dictionary<int, string>();
    private readonly HashSet<int> scenesPresentBeforeSession = new HashSet<int>();
    private readonly List<AsyncOperation> pendingSceneLoads = new List<AsyncOperation>(3);
    private readonly Dictionary<ulong, NetworkObject> spawnedPlayers = new Dictionary<ulong, NetworkObject>(2);
    private readonly List<int> cleanupSceneHandles = new List<int>(3);
    private AsyncOperation cleanupUnloadOperation;
    private AsyncOperation mapLoadOperation;
    private int cleanupUnloadSceneHandle;
    private int cleanupUnloadIndex;

    // 세대 검증·정확한 구독 해제용 콜백 보관.
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

    // 탐색 종료 후 실행할 NGO 시작 방식.
    private enum PendingStart
    {
        None,
        Host,
        Client
    }

    // 서버 공유 씬 로드 단계의 목적.
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
    // 도메인 재사용 시 정적 세션 초기화.
    private static void ResetStatics()
    {
        instance = null;
        networkSessionActive = false;
    }

    // 단일 영속 세션·프로필·로컬 이벤트 초기화.
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

    // 지연 오류·비동기 완료·제한 시간·서버 판 규칙 전진.
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
        ProcessCompletedReturnUnload();

        if (state == NetworkGameSessionState.Stopping)
            AdvanceCleanup(now);
    }

    // 앱 종료 시 세션 또는 탐색 종료 요청.
    private void OnApplicationQuit()
    {
        quitting = true;
        if (networkSessionActive)
            BeginStop(null, false, true);
        else
            discovery.Stop();
    }

    // 로컬 구독·NGO·스킬 세션 최종 해제.
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

    // 이전 탐색 종료 확인 후 방 목록 열기.
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

    // 연결·탐색 정리 후 시작 화면 복귀.
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

    // 호스트 입력 검증·새 방 ID 발급·NGO 시작 예약.
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

    // 탐색 방·프로필·프리팹 검증 후 해당 방 접속 예약.
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

    // 세션 이탈·정리 후 탐색 화면 복귀.
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

    // 로비에서만 서버 챔피언 선택 요청.
    public void SelectChampion(PlayerCharacterId champion)
    {
        if (state != NetworkGameSessionState.Lobby || lobby == null || !Enum.IsDefined(typeof(PlayerCharacterId), champion))
            return;
        lobby.SelectChampion(champion);
    }

    // 로비 준비 변경 요청 전달.
    public void SetReady(bool ready)
    {
        if (state != NetworkGameSessionState.Lobby || lobby == null)
            return;
        lobby.SetReady(ready);
    }

    // 시작 요청 전달; 권한·명단은 서버 검증.
    public void RequestStart()
    {
        if (state != NetworkGameSessionState.Lobby || lobby == null)
            return;
        lobby.RequestStart();
    }

    // 콜백 내부 정리 재진입 방지용 다음 프레임 오류.
    private string deferredFailure;

    // 활성·정리 중 세션과 중복 사용자 작업 차단.
    private bool CanStartUserOperation()
    {
        if (instance != this || state == NetworkGameSessionState.Stopping || networkSessionActive)
            return false;
        return state == NetworkGameSessionState.Entry || state == NetworkGameSessionState.Browsing;
    }

    // 프로필 확인·LAN 탐색 시작·로컬 상태 공개.
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

    // 저장 표시 이름 정규화·검증.
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

    // 캐릭터 ID·NetworkObject·NGO 프리팹 등록 일치 확인.
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

    // 정원·방 ID·이름·IPv4 접속 대상 검증.
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

    // 접속 금지 IPv4 멀티캐스트 대역 판별.
    private static bool IsIpv4Multicast(IPAddress address)
    {
        byte[] bytes = address.GetAddressBytes();
        return bytes.Length == 4 && bytes[0] >= 224 && bytes[0] <= 239;
    }

    // 현재 세대 확인·수동 스폰 설정·호스트 또는 클라이언트 시작.
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

    // 세대별 NGO 구독; 이전 세션 지연 콜백 무시.
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

    // 세션 소유 NGO 구독만 해제.
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

    // 서버 시작 후 공유 TeamLobby 가산 로드 예약.
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

    // NGO 씬 관리 연결·클라이언트 초기 동기화 대기.
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

    // 씬 이벤트·허용 정책 연결; 서버가 가산 동기화 방식 결정.
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
            unloadCompletedCallback = (sceneName, mode, completed, timedOut) =>
            {
                if (callbackGeneration == generation) HandleNetworkUnloadCompleted(sceneName, mode, completed, timedOut);
            };
            networkSceneManager.OnLoad += sceneLoadCallback;
            networkSceneManager.OnLoadEventCompleted += loadCompletedCallback;
            networkSceneManager.OnUnloadEventCompleted += unloadCompletedCallback;
            networkSceneManager.OnSynchronizeComplete += synchronizeCompleteCallback;
        }

        sceneManager.VerifySceneBeforeLoading = VerifySceneBeforeLoading;
        sceneManager.VerifySceneBeforeUnloading = VerifySceneBeforeUnloading;
        sceneManager.PostSynchronizationSceneUnloading = true;
        if (asServer)
            sceneManager.SetClientSynchronizationMode(LoadSceneMode.Additive);
    }

    // NGO 씬 관리자 구독·참조 해제.
    private void UnsubscribeNetworkSceneEvents()
    {
        if (networkSceneManager != null)
        {
            if (sceneLoadCallback != null) networkSceneManager.OnLoad -= sceneLoadCallback;
            if (loadCompletedCallback != null) networkSceneManager.OnLoadEventCompleted -= loadCompletedCallback;
            if (unloadCompletedCallback != null) networkSceneManager.OnUnloadEventCompleted -= unloadCompletedCallback;
            if (synchronizeCompleteCallback != null) networkSceneManager.OnSynchronizeComplete -= synchronizeCompleteCallback;
        }
        networkSceneManager = null;
        sceneLoadCallback = null;
        loadCompletedCallback = null;
        unloadCompletedCallback = null;
        synchronizeCompleteCallback = null;
    }

    // 허용 공유 씬의 가산 로드만 승인; Entry 제외.
    private bool VerifySceneBeforeLoading(int buildIndex, string sceneName, LoadSceneMode mode)
    {
        return mode == LoadSceneMode.Additive && sceneName != EntrySceneName &&
               (sceneName == LobbySceneName || sceneName == InGameSceneName || sceneName == MapSceneName);
    }

    // NGO 동기화 후 불필요 씬 정리 허용 여부.
    private bool VerifySceneBeforeUnloading(Scene scene)
    {
        // true이면 DefaultSceneManagerHandler가 언로드; Entry 유지.
        return scene.IsValid() && scene.name != EntrySceneName;
    }

    // 서버 공유 로드 실행; 중복·수명 단계 불일치 차단.
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

    // 로컬 비동기 로드 추적; 진행 UI·정리 대기 기준.
    private void HandleNetworkSceneLoadStarted(ulong clientId, string sceneName, LoadSceneMode mode,
        AsyncOperation operation)
    {
        if (mode != LoadSceneMode.Additive || !IsOwnedSceneName(sceneName) || operation == null)
            return;
        if (sceneName == MapSceneName) mapLoadOperation = operation;
        if (pendingSceneLoads.Contains(operation)) return;
        pendingSceneLoads.Add(operation);
    }

    // 서버 공유 씬 로드 완료 집계; 전이 처리는 Update.
    private void HandleNetworkLoadCompleted(string sceneName, LoadSceneMode mode,
        List<ulong> clientsCompleted, List<ulong> clientsTimedOut)
    {
        // 서버의 현재 로드 결과 집계; 실제 전이는 Update에서 처리.
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

    // 고정 명단 전원 완료 확인 후 TeamLobby → InGame → Map 전진.
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

    // 서버 로비 초기화·입장 개방·LAN 광고 시작.
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

    // 서버는 입장 확정 시도; 클라이언트는 방·호스트 검증 후 로비 연결.
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

    // 서버 방 ID·프로필·정원 검증 후 동기화 대기 슬롯 확보; 자동 스폰 금지.
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

    // 사유 포함 접속 거절 응답 확정.
    private static void RejectConnection(NetworkManager.ConnectionApprovalResponse response, string reason)
    {
        response.Approved = false;
        response.CreatePlayerObject = false;
        response.Pending = false;
        response.Reason = reason;
    }

    // 서버: 입장 확정 시도. 클라이언트: 로비 동기화 대기.
    private void HandleClientConnected(ulong clientId)
    {
        // 서버 연결 완료 기록·입장 확정 시도; 클라이언트는 동기화 대기.
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

    // 연결·씬 동기화가 모두 완료된 참가자만 서버 명단에 추가.
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

    // 이탈 처리: 로드·복귀 중단, 전투 중 명단 유지.
    private void HandleClientDisconnected(ulong clientId)
    {
        // 단계별 이탈 처리; 로드·복귀 중 중단, 전투 중 남은 명단 유지.
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

        if (state == NetworkGameSessionState.ReturningToLobby)
        {
            BeginStop("A participant disconnected while the room was returning to the lobby.", true, false);
            return;
        }

        if (state == NetworkGameSessionState.Running || state == NetworkGameSessionState.Ended)
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
            matchFlow?.Remove(clientId, manager.ServerTime.Time);
            if (lobby != null) lobby.RemoveMember(clientId);
            if (state == NetworkGameSessionState.Running && matchFlow != null && !matchFlow.Ended)
                PublishReviveSnapshot();
            UpdateHostAdvertisement();
        }
    }

    // 서버 승인 대기 만료자 제거·연결 해제·광고 갱신.
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

    // 복제 로비를 로컬 화면·전투 수명에 반영; 다른 방 상태 거부.
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
            {
                ResetMatchResult();
                SetState(NetworkGameSessionState.LoadingMatch, "The host locked the roster; loading the match.", null);
            }
            else if (current.Phase == NetworkLobbyPhase.Running && state != NetworkGameSessionState.Running &&
                     state != NetworkGameSessionState.Ended)
            {
                if (state == NetworkGameSessionState.Lobby)
                    ResetMatchResult();
                SetState(NetworkGameSessionState.LoadingMatch, "Waiting for local combat services to become ready.", null);
            }
            else if (current.Phase == NetworkLobbyPhase.Ended &&
                     (state == NetworkGameSessionState.Running || state == NetworkGameSessionState.LoadingMatch ||
                      state == NetworkGameSessionState.Lobby))
                EnterEnded();
            else if (current.Phase == NetworkLobbyPhase.Lobby &&
                     (state == NetworkGameSessionState.Ended || state == NetworkGameSessionState.ReturningToLobby))
            {
                localCombatPrepared = false;
                localCombatReady = false;
                SetState(NetworkGameSessionState.Lobby, "Back in the room. Ready up for the next match.", null);
            }
            else if (current.Phase == NetworkLobbyPhase.Stopping)
                BeginStop("The host is closing the room.", true, false);
        }

        RaiseChanged();
    }

    // 서버 호스트 전용 명단 잠금·접속 확인·전투 로드 예약.
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

        ResetMatchResult();
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

    // 서버 전원 로드 확인 후 서비스 준비·스폰·Running 공개.
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
        matchStartedAt = manager.ServerTime.Time;
        foreach (KeyValuePair<ulong, NetworkObject> pair in spawnedPlayers)
            if (pair.Value != null && pair.Value.TryGetComponent(out PlayerHealth health))
                matchFlow.Add(pair.Key, health);
        matchFlow.MatchEnded += HandleMatchEnded;
    }

    // Running 서버에서만 판 규칙 진행·부활 상태 복제.
    private void TickMatchFlow()
    {
        if (matchFlow == null || matchFlow.Ended || manager == null || !manager.IsServer ||
            state != NetworkGameSessionState.Running)
            return;
        matchFlow.Tick(manager.ServerTime.Time);
        if (!matchFlow.Ended)
            PublishReviveSnapshot();
    }

    // 현재 판 시작 세대와 일치하는 부활 예약만 서버 게시.
    private void PublishReviveSnapshot()
    {
        if (lobby == null || !lobby.IsSpawned || matchFlow == null || matchFlow.Ended || manager == null ||
            !manager.IsServer || lobby.Snapshot.Phase != NetworkLobbyPhase.Running)
        {
            return;
        }

        NetworkLobbySnapshot current = lobby.Snapshot;
        if (current.StartRevision != frozenRoster.StartRevision)
            return;

        ReviveSnapshot revives = new ReviveSnapshot
        {
            StartRevision = current.StartRevision,
            Member0 = BuildReviveSlot(current.Member0),
            Member1 = BuildReviveSlot(current.Member1)
        };
        lobby.PublishRevives(revives);
    }

    // 멤버 부활 예약의 서버 시각 기반 복제 슬롯 생성.
    private ReviveSnapshotSlot BuildReviveSlot(NetworkLobbyMemberSnapshot member)
    {
        if (!member.Occupied || !matchFlow.TryGetReviveTime(member.ClientId, out double reviveAtServerTime))
            return default;

        return new ReviveSnapshotSlot
        {
            ClientId = member.ClientId,
            Pending = true,
            ReviveAtServerTime = reviveAtServerTime
        };
    }

    // 서버: 살아 있는 멤버 0명. 적 생성·AI를 멈춘다. 결과 공개·로비 복귀는 Ended 단계에서 처리한다.
    private void HandleMatchEnded()
    {
        float seconds = (float)(matchFlow.EndedAt - matchStartedAt);
        Debug.Log($"[Match] All players are down after {seconds:F1}s. Match ended.");
        try
        {
            SpawnManager.Instance?.EndSession();
        }
        catch (Exception exception)
        {
            Debug.LogException(exception, this);
        }

        MatchResult result = CaptureMatchResult(seconds);
        if (lobby == null || !lobby.PublishEnded(result))
        {
            BeginStop("The server could not publish the match result.", true, false);
            return;
        }
        EnterEnded();
    }

    // 서버: 명단 순서(방장 먼저)로 플레이어별 기록을 캡처한다. 이탈한 팀원은 결과에 없다.
    private MatchResult CaptureMatchResult(float seconds)
    {
        var players = new List<PlayerResult>(2);
        NetworkLobbySnapshot snapshot = lobby != null ? lobby.Snapshot : frozenRoster;
        NetworkLobbyMemberSnapshot[] members = { snapshot.Member0, snapshot.Member1 };
        foreach (NetworkLobbyMemberSnapshot member in members)
        {
            if (!member.Occupied || !spawnedPlayers.TryGetValue(member.ClientId, out NetworkObject player) ||
                player == null)
                continue;
            players.Add(PlayerResult.Capture(player.gameObject, member.DisplayName, member.ClientId));
        }
        return new MatchResult { MatchSeconds = seconds, Players = players.ToArray() };
    }

    // 서버·클라이언트: 판 종료. 전투 세션을 닫고 결과 화면을 기다린다. 씬과 NGO는 유지한다.
    private void EnterEnded(bool authoritativeResultReceived = false)
    {
        bool activeMatch = state == NetworkGameSessionState.Running || state == NetworkGameSessionState.LoadingMatch ||
            (state == NetworkGameSessionState.Lobby &&
             (authoritativeResultReceived || (lobby != null && lobby.Snapshot.Phase == NetworkLobbyPhase.Ended)));
        if (!activeMatch) return;
        localCombatReady = false;
        GameManager gameManager = GameManager.Instance;
        if (gameManager != null)
        {
            try
            {
                gameManager.EndCombatSession(gameManager.SessionId);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception, this);
            }
        }
        SetState(NetworkGameSessionState.Ended, "The match is over.", null);
    }

    // 권위 결과 단발 수신·로컬 저장·구독자별 예외 격리.
    private void HandleMatchResult(MatchResult result)
    {
        if (matchResultReceived)
            return;

        if (state != NetworkGameSessionState.Ended)
            EnterEnded(true);
        if (state != NetworkGameSessionState.Ended)
        {
            deferredFailure = "The room received a match result outside an active match.";
            return;
        }

        matchResultReceived = true;
        GameResult.Data = result;
        Action<MatchResult> listeners = MatchResultReceived;
        if (listeners == null) return;
        foreach (Action<MatchResult> listener in listeners.GetInvocationList())
        {
            try
            {
                listener(result);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception, this);
            }
        }
    }

    // 다음 판 전 로컬 결과·중복 수신 방지 상태 초기화.
    private void ResetMatchResult()
    {
        matchResultReceived = false;
        GameResult.Clear();
    }

    public bool CanReturnToLobby => state == NetworkGameSessionState.Ended && isHost && !teardownStarted &&
        manager != null && manager.IsListening && manager.IsServer && networkSceneManager != null &&
        lobby != null && lobby.IsSpawned && lobby.Snapshot.Phase == NetworkLobbyPhase.Ended;

    // 방장: 결과 화면에서 같은 방으로 돌아간다. Map → InGame 순으로 모든 참가자에서 언로드한다.
    public void ReturnToLobby()
    {
        if (!CanReturnToLobby) return;
        localCombatReady = false;
        SetState(NetworkGameSessionState.ReturningToLobby, "Returning to the room.", null);
        if (state != NetworkGameSessionState.ReturningToLobby) return;

        foreach (KeyValuePair<ulong, NetworkObject> pair in spawnedPlayers)
        {
            try
            {
                EndPlayerSkillSession(pair.Key);
                if (pair.Value != null && pair.Value.IsSpawned)
                    pair.Value.Despawn(true);
            }
            catch (Exception exception)
            {
                BeginStop("Could not remove a match PlayerObject before returning to the room: " + exception.Message,
                    true, false);
                return;
            }
        }
        spawnedPlayers.Clear();
        matchFlow = null;
        expectedUnloadSceneName = null;
        pendingUnloadComplete = false;
        pendingUnloadFailed = false;
        pendingUnloadFailure = null;
        completedSceneClients.Clear();
        completedUnloadClients.Clear();
        expectedSceneName = null;
        queuedSceneName = null;
        expectedScenePurpose = SceneLoadPurpose.None;
        pendingSceneLoadComplete = false;
        pendingSceneLoadFailed = false;
        pendingSceneLoadFailure = null;
        QueueNextReturnUnload();
    }

    // 서버 소유권·로비 유지 확인 후 Map → InGame 순차 해제.
    private void QueueNextReturnUnload()
    {
        if (state != NetworkGameSessionState.ReturningToLobby || manager == null || !manager.IsListening ||
            !manager.IsServer || networkSceneManager == null)
        {
            BeginStop("The server lost ownership of the shared scene return operation.", true, false);
            return;
        }

        Scene lobbyScene = SceneManager.GetSceneByName(LobbySceneName);
        if (!lobbyScene.IsValid() || !lobbyScene.isLoaded)
        {
            BeginStop("TeamLobby was not present while returning from the match.", true, false);
            return;
        }

        Scene mapScene = SceneManager.GetSceneByName(MapSceneName);
        Scene inGameScene = SceneManager.GetSceneByName(InGameSceneName);
        string sceneName = mapScene.IsValid() && mapScene.isLoaded ? MapSceneName
            : inGameScene.IsValid() && inGameScene.isLoaded ? InGameSceneName : null;
        if (sceneName == null)
        {
            CompleteReturnToLobby();
            return;
        }

        Scene scene = SceneManager.GetSceneByName(sceneName);
        if (!scene.IsValid() || !scene.isLoaded || expectedUnloadSceneName != null)
        {
            BeginStop("The next session scene was not ready for its ordered unload: " + sceneName + ".", true, false);
            return;
        }
        expectedUnloadSceneName = sceneName;
        pendingUnloadComplete = false;
        pendingUnloadFailed = false;
        pendingUnloadFailure = null;
        completedUnloadClients.Clear();
        actionDeadline = Time.unscaledTimeAsDouble + SceneLoadTimeout;
        SceneEventProgressStatus status;
        try
        {
            status = networkSceneManager.UnloadScene(scene);
        }
        catch (Exception exception)
        {
            BeginStop("NGO failed to unload " + sceneName + ": " + exception.Message, true, false);
            return;
        }
        if (status != SceneEventProgressStatus.Started)
            BeginStop("NGO did not start the " + sceneName + " unload (" + status + ").", true, false);
    }

    // 서버 복귀 언로드 완료 집계; 로비 멤버 전원 필수.
    private void HandleNetworkUnloadCompleted(string sceneName, LoadSceneMode mode,
        List<ulong> clientsCompleted, List<ulong> clientsTimedOut)
    {
        // 서버 복귀 결과 집계; 현재 로비 멤버 전원 완료 필수.
        if (state != NetworkGameSessionState.ReturningToLobby || sceneName != expectedUnloadSceneName)
            return;

        pendingUnloadComplete = true;
        if (manager == null || !manager.IsServer || mode != LoadSceneMode.Additive)
        {
            pendingUnloadFailed = true;
            pendingUnloadFailure = "NGO reported an invalid scene unload completion.";
            return;
        }

        completedUnloadClients.Clear();
        if (clientsCompleted != null)
        {
            for (int i = 0; i < clientsCompleted.Count; i++)
                completedUnloadClients.Add(clientsCompleted[i]);
        }

        if (clientsTimedOut != null && clientsTimedOut.Count > 0)
        {
            pendingUnloadFailed = true;
            pendingUnloadFailure = "A participant timed out while unloading " + sceneName + ".";
            return;
        }

        NetworkLobbySnapshot current = lobby != null ? lobby.Snapshot : default;
        if (lobby == null || !lobby.IsSpawned || current.Phase != NetworkLobbyPhase.Ended ||
            !current.Member0.Occupied || !current.Member0.IsHost ||
            !IsCurrentMemberUnloadComplete(current.Member0) ||
            !IsCurrentMemberUnloadComplete(current.Member1))
        {
            pendingUnloadFailed = true;
            pendingUnloadFailure = "Not every connected room member completed unloading " + sceneName + ".";
        }
    }

    // 빈 슬롯 허용·점유자의 연결·동기화·언로드 완료 확인.
    private bool IsCurrentMemberUnloadComplete(NetworkLobbyMemberSnapshot member)
    {
        if (!member.Occupied) return true;
        return member.Connected && member.Synchronized && manager.ConnectedClients.ContainsKey(member.ClientId) &&
               completedUnloadClients.Contains(member.ClientId);
    }

    // NGO 완료와 로컬 씬 해제 확인 후 다음 복귀 단계 실행.
    private void ProcessCompletedReturnUnload()
    {
        if (!pendingUnloadComplete || state != NetworkGameSessionState.ReturningToLobby)
            return;
        string sceneName = expectedUnloadSceneName;
        if (pendingUnloadFailed)
        {
            BeginStop(pendingUnloadFailure ?? "The room could not confirm unloading " + sceneName + ".", true, false);
            return;
        }

        Scene unloadedScene = SceneManager.GetSceneByName(sceneName);
        if (unloadedScene.IsValid() && unloadedScene.isLoaded)
            return;

        pendingUnloadComplete = false;
        pendingUnloadFailure = null;
        expectedUnloadSceneName = null;
        if (sceneName == MapSceneName)
            mapLoadOperation = null;
        RemoveCompletedSceneOperations();
        if (pendingSceneLoads.Count != 0)
        {
            BeginStop("An NGO scene load remained pending after unloading " + sceneName + ".", true, false);
            return;
        }
        QueueNextReturnUnload();
    }

    // 서버 로비 재공개·판 기록 초기화·입장과 LAN 광고 재개.
    private void CompleteReturnToLobby()
    {
        RemoveCompletedSceneOperations();
        if (pendingSceneLoads.Count != 0)
        {
            BeginStop("An NGO scene load remained pending after returning from the match.", true, false);
            return;
        }

        Scene lobbyScene = SceneManager.GetSceneByName(LobbySceneName);
        if (manager == null || !manager.IsListening || !manager.IsServer || networkSceneManager == null ||
            !lobbyScene.IsValid() || !lobbyScene.isLoaded ||
            SceneManager.GetSceneByName(MapSceneName).isLoaded || SceneManager.GetSceneByName(InGameSceneName).isLoaded ||
            lobby == null || !lobby.IsSpawned || lobby.Snapshot.Phase != NetworkLobbyPhase.Ended || !lobby.ReturnToLobby())
        {
            BeginStop("The room could not return to the lobby phase.", true, false);
            return;
        }

        expectedUnloadSceneName = null;
        pendingUnloadComplete = false;
        pendingUnloadFailed = false;
        pendingUnloadFailure = null;
        frozenClientIds.Clear();
        completedSceneClients.Clear();
        completedUnloadClients.Clear();
        frozenRoster = default;
        matchStartedAt = 0d;
        matchFlow = null;
        mapLoadOperation = null;
        expectedSceneName = null;
        queuedSceneName = null;
        expectedScenePurpose = SceneLoadPurpose.None;
        pendingSceneLoadComplete = false;
        pendingSceneLoadFailed = false;
        pendingSceneLoadFailure = null;
        actionDeadline = 0d;
        localCombatPrepared = false;
        localCombatReady = false;
        admissionLocked = false;
        admissionOpen = true;
        SetState(NetworkGameSessionState.Lobby, "Back in the room. Ready up for the next match.", null);

        NetworkLobbySnapshot current = lobby.Snapshot;
        byte occupancy = (byte)Mathf.Min(2, current.MemberCount + pendingAdmissions.Count);
        bool accepting = admissionOpen && !admissionLocked && occupancy < 2;
        if (!discovery.StartHosting(roomInstanceId, roomName, GamePort, occupancy, accepting, out string discoveryError))
        {
            BeginStop("The LAN room responder could not restart: " + discoveryError, true, false);
            return;
        }
        UpdateHostAdvertisement();
    }

    // 서버 고정 명단을 Map에 소유권 지정 생성; 부분 실패 시 개별 정리.
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

    // 각 피어의 로컬 준비와 서버 Running 복제가 모두 끝난 뒤 전투 허용.
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

    // 로컬 Map·매니저·스폰 지점 검증 및 전투 세션 시작.
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

    // TeamLobby의 유일한 스폰 로비 검색; 중복은 오류 취급.
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

    // 현재 세대 로비 변경·시작·결과 이벤트 구독.
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
        lobby.MatchResultReceived += HandleMatchResult;
    }

    // 로비 구독·로컬 참조 해제.
    private void DetachLobby()
    {
        if (lobby != null)
        {
            if (lobbyChangedCallback != null) lobby.Changed -= lobbyChangedCallback;
            if (lobbyStartCallback != null) lobby.StartRequested -= lobbyStartCallback;
            lobby.MatchResultReceived -= HandleMatchResult;
        }
        lobby = null;
        lobbyChangedCallback = null;
        lobbyStartCallback = null;
    }

    // 호스트 광고 갱신; 동기화 대기자도 정원에 포함.
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

    // 탐색 상태·오류 반영; 활성 호스트 광고 오류는 세션 종료.
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

    // 탐색 화면의 방 목록 변경 통지.
    private void HandleDiscoveryRoomsChanged(IReadOnlyList<LanDiscoveredRoom> rooms)
    {
        if (state == NetworkGameSessionState.Browsing)
            RaiseChanged();
    }

    // 세션 이후 생성된 공유 씬의 로컬 소유권 기록.
    private void HandleLocalSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (!networkSessionActive || !IsOwnedSceneName(scene.name) || scenesPresentBeforeSession.Contains(scene.handle))
            return;
        ownedScenes[scene.handle] = scene.name;
    }

    // 소유권 제거·정상 종료 경로 외 공유 씬 유실 감지.
    private void HandleLocalSceneUnloaded(Scene scene)
    {
        ownedScenes.Remove(scene.handle);
        if (networkSessionActive && state != NetworkGameSessionState.Stopping &&
            state != NetworkGameSessionState.ReturningToLobby &&
            !(state == NetworkGameSessionState.Ended && (scene.name == MapSceneName || scene.name == InGameSceneName)) &&
            IsOwnedSceneName(scene.name) && scene.name != EntrySceneName)
        {
            deferredFailure = "A session-owned scene was unloaded unexpectedly: " + scene.name + ".";
        }
    }

    // 탐색 종료·동기화·공유 로드·복귀 언로드 제한 시간 점검.
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
        if (expectedUnloadSceneName != null && now >= actionDeadline &&
            (!pendingUnloadComplete ||
             (SceneManager.GetSceneByName(expectedUnloadSceneName).IsValid() &&
              SceneManager.GetSceneByName(expectedUnloadSceneName).isLoaded)))
        {
            BeginStop("Returning to the room timed out while unloading " + expectedUnloadSceneName + ".", true, false);
            return;
        }
        if (expectedSceneName != null && !pendingSceneLoadComplete && now >= actionDeadline)
            BeginStop("The shared " + expectedSceneName + " scene load timed out.", true, false);
    }

    // 로컬 씬 핸들로 현재 로드된 씬 검색.
    private static Scene FindLoadedScene(int handle)
    {
        for (int i = 0; i < SceneManager.sceneCount; i++)
        {
            Scene scene = SceneManager.GetSceneAt(i);
            if (scene.handle == handle) return scene;
        }
        return default;
    }

    // 중복 정리 차단·입장 폐쇄·Stopping 공개·스킬 종료 후 NGO 종료 요청.
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

    // NGO·로컬 플레이어 스킬 중복 없이 종료·로컬 등록 해제.
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

    // 스킬 세션 중복 종료 방지용 수집.
    private static void AddSkillSession(List<PlayerSkillController> sessions, PlayerSkillController skills)
    {
        if (skills != null && !sessions.Contains(skills))
            sessions.Add(skills);
    }

    // 스킬 정리 실패 기록·재시작 차단 상태 고정.
    private void RecordSkillCleanupFault(Exception exception)
    {
        string message = "A player's skill cleanup failed before NGO teardown: " + exception.Message;
        Debug.LogException(exception, this);
        cleanupFaulted = true;
        error = string.IsNullOrEmpty(error) ? message : error + " " + message;
        RaiseChanged();
    }

    // 특정 NGO 플레이어의 스킬 종료·실패 격리.
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

    // 탐색·NGO·진행 로드 종료 대기 후 소유 씬만 역순 정리.
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

    // 완료된 로컬 비동기 로드 추적 항목 제거.
    private void RemoveCompletedSceneOperations()
    {
        for (int i = pendingSceneLoads.Count - 1; i >= 0; i--)
        {
            AsyncOperation operation = pendingSceneLoads[i];
            if (operation == null || operation.isDone)
                pendingSceneLoads.RemoveAt(i);
        }
    }

    // 정리 시간 초과의 재시작 차단 오류 기록.
    private void CheckCleanupTimeout(double now, string message)
    {
        if (now >= teardownDeadline)
            RecordCleanupFault(message + " Restart remains blocked until cleanup is complete.");
    }

    // 첫 정리 오류 보존·Stopping 유지.
    private void RecordCleanupFault(string message)
    {
        if (status.StartsWith("Cleanup fault: ", StringComparison.Ordinal))
            return;
        cleanupFaulted = true;
        if (string.IsNullOrEmpty(error)) error = message;
        status = "Cleanup fault: " + message;
        SetState(NetworkGameSessionState.Stopping, status, error);
    }

    // 구독·기록 해제; 정리 오류가 없을 때만 시작·탐색 화면 복귀.
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
        completedUnloadClients.Clear();
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
        matchStartedAt = 0d;
        matchResultReceived = false;
        frozenRoster = default;
        expectedUnloadSceneName = null;
        pendingUnloadComplete = false;
        pendingUnloadFailed = false;
        pendingUnloadFailure = null;
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

    // 새 방 작업 전 이전 판·씬·입장 추적 초기화.
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
        completedUnloadClients.Clear();
        matchStartedAt = 0d;
        matchResultReceived = false;
        frozenRoster = default;
        expectedUnloadSceneName = null;
        pendingUnloadComplete = false;
        pendingUnloadFailed = false;
        pendingUnloadFailure = null;
        expectedSceneName = null;
        queuedSceneName = null;
        pendingSceneLoadComplete = false;
        pendingSceneLoadFailed = false;
        pendingSceneLoadFailure = null;
        expectedScenePurpose = SceneLoadPurpose.None;
        cleanupFaulted = false;
        unloadStarted = false;
    }

    // 로컬 상태·메시지 갱신 및 UI 통지.
    private void SetState(NetworkGameSessionState newState, string newStatus, string newError)
    {
        state = newState;
        status = newStatus ?? string.Empty;
        error = newError;
        RaiseChanged();
    }

    // 로컬 상태 변경 통지·구독 예외 기록.
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

    // 정리에서 보호할 기존 로컬 씬 핸들 기록.
    private void CapturePreexistingScenes()
    {
        scenesPresentBeforeSession.Clear();
        for (int i = 0; i < SceneManager.sceneCount; i++)
            scenesPresentBeforeSession.Add(SceneManager.GetSceneAt(i).handle);
    }

    // 세션이 소유 가능한 공유 씬 이름 판별.
    private static bool IsOwnedSceneName(string sceneName)
    {
        return sceneName == LobbySceneName || sceneName == InGameSceneName || sceneName == MapSceneName;
    }

    // 서버 승인 후 연결·씬 동기화를 각각 기다리는 임시 입장 기록.
    private struct PendingAdmission
    {
        public readonly string DisplayName;
        public readonly double Deadline;
        public bool Connected;
        public bool Synchronized;

        // 표시 이름·로컬 제한 시간 고정 및 완료 조건 초기화.
        public PendingAdmission(string displayName, double deadline)
        {
            DisplayName = displayName;
            Deadline = deadline;
            Connected = false;
            Synchronized = false;
        }
    }
}
