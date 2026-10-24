using System.Collections;
using UnityEngine;
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
        SpawnManager.OnPlayerSpawned -= BindPlayer;
        SceneManager.sceneUnloaded -= HandleSceneUnloaded;
        CancelStartup();
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
        // 씬 로드 취소 불가. 활성화 잠금만 해제.
        if (pendingMapLoad != null) pendingMapLoad.allowSceneActivation = true;
        pendingMapLoad = null;
    }

    // 자기 세션만 종료
    public void EndCombatSession(int expectedSessionId)
    {
        if (!BelongsToSession(expectedSessionId)) return;
        SessionId = 0; // 이전 비동기 요청 무효
        GameObject player = currentPlayer;
        currentPlayer = null;
        UIManager.Instance?.ClearSession(false);
        AugmentManager.Instance?.ClearSession();
        QuestManager.Instance?.ClearSession();
        if (player != null && player.TryGetComponent(out PlayerAugments augments))
            augments.ReleaseAll();
        resourceLoader?.ReleaseResources();
        IsPaused = false;
        Time.timeScale = 1f;
        SetCursor(true);
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
        resourceLoader = AugmentResourceLoader.Instance;
        if (resourceLoader == null)
        {
            Debug.LogError("전투 시작에 AugmentResourceLoader가 필요합니다.", this);
            yield break;
        }
        yield return resourceLoader.WaitForRelease();
        if (Instance != this || !isActiveAndEnabled || startupCancelled) yield break;

        if (UIManager.Instance != null)
        {
            UIManager.Instance.ShowLoadingUI(true);
        }

        AsyncOperation asyncLoad = SceneManager.LoadSceneAsync(mapSceneName, LoadSceneMode.Additive);
        pendingMapLoad = asyncLoad;
        asyncLoad.allowSceneActivation = false;

        while (asyncLoad.progress < 0.9f)
        {
            if (startupCancelled) yield break;
            if (UIManager.Instance != null && UIManager.Instance.loadingPanelUI != null)
            {
                float progress = Mathf.Clamp01(asyncLoad.progress / 0.9f);
                UIManager.Instance.loadingPanelUI.SetTargetProgress(progress);
            }
            yield return null;
        }

        if (UIManager.Instance != null && UIManager.Instance.loadingPanelUI != null)
        {
            UIManager.Instance.loadingPanelUI.SetTargetProgress(1f);
            while (!UIManager.Instance.loadingPanelUI.IsLoadingVisualDone)
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
            EndCombatSession(SessionId);
            Debug.LogError("플레이어 생성에 실패하여 전투 세션을 종료합니다.", this);
            yield break;
        }

        yield return new WaitForSecondsRealtime(0.2f);

        if (UIManager.Instance != null)
        {
            UIManager.Instance.ShowLoadingUI(false);
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
