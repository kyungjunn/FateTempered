using System;
using System.Collections;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

// 전투 세션 소유자. 시작·종료·현재 플레이어.
public class GameManager : MonoBehaviour
{
    public static GameManager Instance;
    private static int nextSessionId = 1;
    private AugmentResourceLoader resourceLoader;
    private Scene mapScene;
    private GameObject currentPlayer;
    private AsyncOperation pendingMapLoad;
    private UIManager startupUIManager;
    private bool startupCancelled;

    public int SessionId { get; private set; }
    public GameObject CurrentPlayer => currentPlayer;
    public bool IsActiveSession => Instance == this && SessionId != 0;

    public bool IsPaused { get; private set; }

    [Header("불러올 맵 씬 이름")]
    [SerializeField] private string mapSceneName = "Map";

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        Instance = null;
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
        SpawnManager.OnPlayerSpawned += BindPlayer;
        SceneManager.sceneUnloaded += HandleSceneUnloaded;
    }

    private void OnDisable()
    {
        if (Instance != this) return;
        SpawnManager.OnPlayerSpawned -= BindPlayer;
        SceneManager.sceneUnloaded -= HandleSceneUnloaded;
        CancelStartup();
        CloseStartupLoadingUI();
        EndCombatSession(SessionId);
    }

    private void Start()
    {
        if (Instance != this || startupCancelled) return;
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
        currentPlayer = null;
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
            IsPaused = false;
            Time.timeScale = 1f;
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

        if (NetworkPlayerMotor.CanSimulate(player) && player.TryGetComponent(out Rigidbody body))
            body.linearVelocity = Vector3.zero;
    }

    // 소유 플레이어 소실 → 세션 종료
    public void NotifyPlayerUnavailable(GameObject player)
    {
        if (ReferenceEquals(currentPlayer, player))
            EndCombatSession(SessionId);
    }

    private void BindPlayer(GameObject player)
    {
        if (!IsActiveSession || ReferenceEquals(currentPlayer, player)) return;
        // 교체 = 새 세션. 이전 적용 권한 차단.
        if (!ReferenceEquals(currentPlayer, null))
        {
            EndCombatSession(SessionId);
            return;
        }
        currentPlayer = player;
    }

    private void HandleSceneUnloaded(Scene scene)
    {
        if (scene.handle == mapScene.handle || scene.handle == gameObject.scene.handle)
            EndCombatSession(SessionId);
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
            SceneManager.SetActiveScene(loadedScene);

            mapScene = loadedScene;
            resourceLoader.InitializeMetadata();
            SessionId = nextSessionId++;

            if (SpawnManager.Instance != null)
            {
                SpawnManager.Instance.SpawnAll();
                BindPlayer(SpawnManager.Instance.SpawnedPlayer);
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
        IsPaused = true;
        Time.timeScale = 0f;
        SetCursor(true);
    }

    public void ResumeGame()
    {
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
