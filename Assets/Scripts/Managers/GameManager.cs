using System;
using System.Collections;
using UnityEngine;
using Unity.Netcode;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

// 전투 세션 소유자. 시작·종료·현재 플레이어.
public class GameManager : MonoBehaviour
{
    private const string OfflineGameSceneName = "InGame";
    private const string OfflineMenuSceneName = "NetworkEntry";

    public static GameManager Instance;
    public static event Action<GameObject> OnLocalPlayerChanged;
    private static int nextSessionId = 1;
    private AugmentResourceLoader resourceLoader;
    private Scene mapScene;
    private GameObject currentPlayer;
    private AsyncOperation pendingMapLoad;
    private UIManager startupUIManager;
    private bool startupCancelled;
    // 네트워크 전투 구분·종료 중 오프라인 동작 차단
    private bool networkCombatMode;
    private bool offlineNavigationInProgress;

    public int SessionId { get; private set; }
    public GameObject CurrentPlayer => currentPlayer;
    public bool IsActiveSession => Instance == this && SessionId != 0;
    // 네트워크 세션 토큰·맵 로드 완료 조건
    public bool IsNetworkCombatActive => networkCombatMode && IsActiveSession &&
        mapScene.IsValid() && mapScene.isLoaded;

    public bool IsPaused { get; private set; }

    [Header("불러올 맵 씬 이름")]
    [SerializeField] private string mapSceneName = "Map";

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        Instance = null;
        OnLocalPlayerChanged = null;
        nextSessionId = 1;
    }

    private void Awake()
    {
        if (Instance == null)
        {
            Instance = this;
        }
        else
        {
            // 중복: 기존 세션 유지
            enabled = false;
            Destroy(gameObject);
        }
    }

    private void OnEnable()
    {
        if (Instance != this) return;
        SceneManager.sceneUnloaded += HandleSceneUnloaded;
    }

    private void OnDisable()
    {
        if (Instance != this) return;
        SceneManager.sceneUnloaded -= HandleSceneUnloaded;
        CancelStartup();
        CloseStartupLoadingUI();
        EndCombatSession(SessionId);
    }

    private void Start()
    {
        if (Instance != this || startupCancelled) return;
        // 네트워크 전용 로딩 대기·로컬 맵 생성 제외
        if (NetworkGameSession.IsNetworkSessionActive)
        {
            networkCombatMode = true;
            StartCoroutine(NetworkLoadingRoutine());
            return;
        }
        GameResult.Clear();
        StartCoroutine(LoadMapAndSpawnRoutine());
    }

    private void OnDestroy()
    {
        if (Instance == this)
        {
            CancelStartup();
            CloseStartupLoadingUI();
            EndCombatSession(SessionId);
            Instance = null;
        }
    }

    // 토큰 일치 + 활성 세션
    public bool BelongsToSession(int expectedSessionId)
    {
        return IsActiveSession && SessionId == expectedSessionId;
    }

    private void CancelStartup()
    {
        startupCancelled = true;
        ReleasePendingMapActivation();
    }

    private void ReleasePendingMapActivation()
    {
        // 씬 로드 취소 불가. 활성화 잠금만 해제.
        if (pendingMapLoad != null) pendingMapLoad.allowSceneActivation = true;
        pendingMapLoad = null;
    }

    private void CloseStartupLoadingUI()
    {
        UIManager manager = startupUIManager;
        startupUIManager = null;
        if (manager != null) manager.ShowLoadingUI(false);
    }

    // 자기 세션만 종료
    public void EndCombatSession(int expectedSessionId)
    {
        if (!BelongsToSession(expectedSessionId)) return;
        SessionId = 0; // 이전 비동기 요청 무효
        GameObject player = currentPlayer;
        try
        {
            RunSessionCleanup(() => SpawnManager.Instance?.EndSession());
            RunSessionCleanup(() => QuestManager.Instance?.ClearSession());
            RunSessionCleanup(() => UIManager.Instance?.ClearSession(false));
            RunSessionCleanup(() => DisablePlayerControls(player));
            RunSessionCleanup(() =>
            {
                if (player != null && player.TryGetComponent(out PlayerSkillController skills))
                    skills.EndSession();
            });
            RunSessionCleanup(() => AugmentManager.Instance?.ClearSession());
            RunSessionCleanup(() =>
            {
                if (player != null && player.TryGetComponent(out PlayerAugments augments))
                    augments.ReleaseAll();
            });
            RunSessionCleanup(() => resourceLoader?.ReleaseResources());
        }
        finally
        {
            SetCurrentPlayer(null);
            // 네트워크 종료 경로의 전역 시간 배율 변경 제외
            bool offlineSession = !NetworkGameSession.IsNetworkSessionActive && !networkCombatMode;
            if (!NetworkGameSession.IsNetworkSessionActive)
                networkCombatMode = false;
            if (offlineSession) Time.timeScale = 1f;
            IsPaused = false;
            SetCursor(true);
        }
    }

    private void RunSessionCleanup(Action cleanup)
    {
        try
        {
            cleanup();
        }
        catch (Exception exception)
        {
            Debug.LogException(exception, this);
        }
    }

    private static void DisablePlayerControls(GameObject player)
    {
        if (player == null) return;

        PlayerInput input = player.GetComponent<PlayerInput>();
        if (input != null) input.enabled = false;

        PlayerSkillInput skillInput = player.GetComponent<PlayerSkillInput>();
        if (skillInput != null) skillInput.enabled = false;

        PlayerController controller = player.GetComponent<PlayerController>();
        if (controller != null) controller.enabled = false;

        // 물리 시뮬레이션 권한 보유 객체만 정지
        if (NetworkAuthority.CanSimulate(player) && player.TryGetComponent(out Rigidbody body))
            body.linearVelocity = Vector3.zero;
    }

    // 오프라인 세션은 종료하고, 네트워크에서는 로컬 플레이어만 해제한다.
    public void NotifyPlayerUnavailable(GameObject player)
    {
        UnregisterLocalPlayer(player);
    }

    public void RestartOfflineMatch()
    {
        NavigateFromOfflineResult(OfflineGameSceneName);
    }

    public void ReturnOfflineToMenu()
    {
        NavigateFromOfflineResult(OfflineMenuSceneName);
    }

    private void NavigateFromOfflineResult(string sceneName)
    {
        // 네트워크 결과의 단독 씬 이동 제외
        if (Instance != this || !isActiveAndEnabled || NetworkGameSession.IsNetworkSessionActive ||
            networkCombatMode || SessionId != 0 || !GameResult.Data.HasValue || offlineNavigationInProgress)
        {
            return;
        }

        if (!Application.CanStreamedLevelBeLoaded(sceneName))
        {
            Debug.LogError("오프라인 결과 씬을 불러올 수 없습니다: " + sceneName, this);
            return;
        }

        offlineNavigationInProgress = true;
        StartCoroutine(NavigateOfflineAfterCleanup(sceneName));
    }

    private IEnumerator NavigateOfflineAfterCleanup(string sceneName)
    {
        EndCombatSession(SessionId);

        AugmentResourceLoader loader = resourceLoader != null ? resourceLoader : AugmentResourceLoader.Instance;
        if (loader != null)
            yield return loader.WaitForRelease();

        AsyncOperation sceneLoad;
        try
        {
            sceneLoad = SceneManager.LoadSceneAsync(sceneName, LoadSceneMode.Single);
        }
        catch (Exception exception)
        {
            offlineNavigationInProgress = false;
            Debug.LogException(exception, this);
            yield break;
        }

        if (sceneLoad == null)
        {
            offlineNavigationInProgress = false;
            Debug.LogError("오프라인 결과 씬 로드를 시작하지 못했습니다: " + sceneName, this);
            yield break;
        }

        while (!sceneLoad.isDone)
            yield return null;
    }

    // 로컬 전투 대상 등록·네트워크 소유권 확인
    public bool RegisterLocalPlayer(GameObject player)
    {
        if (!IsActiveSession || player == null)
            return false;

        if (NetworkGameSession.IsNetworkSessionActive)
        {
            // 스폰 완료된 자기 소유 PlayerObject만 허용
            NetworkObject networkObject = player.GetComponent<NetworkObject>();
            if (!networkCombatMode || networkObject == null || !networkObject.IsSpawned ||
                !networkObject.IsOwner)
                return false;
        }

        if (ReferenceEquals(currentPlayer, player))
            return true;
        if (currentPlayer != null)
        {
            Debug.LogError("A local combat player is already registered for this session.", this);
            return false;
        }

        SetCurrentPlayer(player);
        return true;
    }

    // 네트워크 로컬 플레이어 해제·공유 전투 세션 유지
    public void UnregisterLocalPlayer(GameObject player)
    {
        if (ReferenceEquals(player, null) || !ReferenceEquals(currentPlayer, player)) return;

        if (NetworkGameSession.IsNetworkSessionActive)
        {
            DisablePlayerControls(player);
            RunSessionCleanup(() =>
            {
                if (player != null && player.TryGetComponent(out PlayerSkillController skills))
                    skills.EndSession();
            });
            SetCurrentPlayer(null);
            return;
        }

        EndCombatSession(SessionId);
    }

    private void SetCurrentPlayer(GameObject player)
    {
        if (ReferenceEquals(currentPlayer, player)) return;
        currentPlayer = player;
        Action<GameObject> listeners = OnLocalPlayerChanged;
        if (listeners == null) return;

        foreach (Action<GameObject> listener in listeners.GetInvocationList())
        {
            try
            {
                listener(player);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception, this);
            }
        }
    }

    // 로드 완료된 네트워크 맵 연결·전투 토큰 발급
    public bool BeginNetworkCombat(Scene map)
    {
        if (Instance != this || !isActiveAndEnabled || !NetworkGameSession.IsNetworkSessionActive ||
            !map.IsValid() || !map.isLoaded || map.name != mapSceneName)
            return false;

        // 동일 맵의 중복 시작 허용·다른 세션 덮어쓰기 차단
        if (networkCombatMode && SessionId != 0)
            return mapScene.handle == map.handle && IsNetworkCombatActive;
        if (SessionId != 0 || currentPlayer != null)
            return false;

        networkCombatMode = true;
        mapScene = map;
        SessionId = nextSessionId++;
        IsPaused = false;
        return true;
    }

    private void HandleSceneUnloaded(Scene scene)
    {
        if ((mapScene.IsValid() && scene.handle == mapScene.handle) ||
            scene.handle == gameObject.scene.handle)
            EndCombatSession(SessionId);
    }

    // 네트워크: 모든 참가자의 Map 로드·스폰(Running)까지 로딩 패널 유지.
    private IEnumerator NetworkLoadingRoutine()
    {
        startupUIManager = UIManager.Instance;
        LoadingPanelUI panel = startupUIManager != null ? startupUIManager.loadingPanelUI : null;
        if (panel == null)
        {
            startupUIManager = null;
            yield break;
        }

        startupUIManager.ShowLoadingUI(true);
        try
        {
            while (true)
            {
                if (startupCancelled || !NetworkGameSession.IsNetworkSessionActive) yield break;
                NetworkGameSession session = NetworkGameSession.Instance;
                if (session == null || session.State == NetworkGameSessionState.Stopping) yield break;

                // 참가자 준비 진행률·전투 시작·로딩 연출 완료 대기
                bool running = NetworkGameSession.IsCombatRunning;
                panel.SetTargetProgress(session.MatchLoadingProgress);
                if (running && panel.IsLoadingVisualDone) yield break;
                yield return null;
            }
        }
        finally
        {
            CloseStartupLoadingUI();
        }
    }

    private IEnumerator LoadMapAndSpawnRoutine()
    {
        bool startupSucceeded = false;
        try
        {
            resourceLoader = AugmentResourceLoader.Instance;
            if (resourceLoader == null)
            {
                Debug.LogError("전투 시작에 AugmentResourceLoader가 필요합니다.", this);
                yield break;
            }
            yield return resourceLoader.WaitForRelease();
            if (Instance != this || !isActiveAndEnabled || startupCancelled) yield break;

            startupUIManager = UIManager.Instance;
            if (startupUIManager != null) startupUIManager.ShowLoadingUI(true);

            if (!Application.CanStreamedLevelBeLoaded(mapSceneName))
            {
                Debug.LogError("전투 맵을 불러올 수 없습니다: " + mapSceneName, this);
                yield break;
            }

            AsyncOperation asyncLoad = SceneManager.LoadSceneAsync(mapSceneName, LoadSceneMode.Additive);
            pendingMapLoad = asyncLoad;
            asyncLoad.allowSceneActivation = false;

            while (asyncLoad.progress < 0.9f)
            {
                if (startupCancelled) yield break;
                if (startupUIManager != null && startupUIManager.loadingPanelUI != null)
                {
                    float progress = Mathf.Clamp01(asyncLoad.progress / 0.9f);
                    startupUIManager.loadingPanelUI.SetTargetProgress(progress);
                }
                yield return null;
            }

            if (startupUIManager != null && startupUIManager.loadingPanelUI != null)
            {
                startupUIManager.loadingPanelUI.SetTargetProgress(1f);
                while (!startupUIManager.loadingPanelUI.IsLoadingVisualDone)
                {
                    if (startupCancelled) yield break;
                    yield return null;
                }
            }

            asyncLoad.allowSceneActivation = true;

            while (!asyncLoad.isDone)
            {
                yield return null;
            }
            pendingMapLoad = null;

            if (Instance != this || !isActiveAndEnabled || startupCancelled) yield break;
            Scene loadedScene = SceneManager.GetSceneByName(mapSceneName);
            if (!loadedScene.IsValid() || !loadedScene.isLoaded)
            {
                Debug.LogError("전투 맵 씬 로드가 완료되지 않았습니다: " + mapSceneName, this);
                yield break;
            }
            SceneManager.SetActiveScene(loadedScene);

            mapScene = loadedScene;
            resourceLoader.InitializeMetadata();
            SessionId = nextSessionId++;

            if (SpawnManager.Instance != null)
            {
                SpawnManager.Instance.SpawnAll();
            }
            if (currentPlayer == null)
            {
                Debug.LogError("플레이어 생성에 실패하여 전투 세션을 종료합니다.", this);
                yield break;
            }

            yield return new WaitForSecondsRealtime(0.2f);
            if (startupCancelled || !BelongsToSession(SessionId)) yield break;
            startupSucceeded = true;
        }
        finally
        {
            ReleasePendingMapActivation();
            CloseStartupLoadingUI();
            if (!startupSucceeded && SessionId != 0)
                EndCombatSession(SessionId);
        }

    }

    public void PauseGame()
    {
        // 네트워크 공용 전투의 로컬 일시정지 제외
        if (NetworkGameSession.IsNetworkSessionActive || networkCombatMode) return;
        IsPaused = true;
        Time.timeScale = 0f;
        SetCursor(true);
    }

    public void ResumeGame()
    {
        // 네트워크 전투의 전역 시간 배율 변경 제외
        if (NetworkGameSession.IsNetworkSessionActive || networkCombatMode) return;
        IsPaused = false;
        Time.timeScale = 1f;
        SetCursor(false);
    }

    public void SetCursor(bool visible)
    {
        Cursor.visible = visible;
        Cursor.lockState = visible ? CursorLockMode.None : CursorLockMode.Locked;
    }
}
