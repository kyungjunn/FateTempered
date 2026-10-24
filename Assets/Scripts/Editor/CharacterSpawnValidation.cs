using System;
using System.Collections;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

// 세션 소유·플레이어 연결 회귀. batchmode 전용.
[InitializeOnLoad]
public static class CharacterSpawnValidation
{
    private const string Pending = "CharacterSpawnValidation.Pending";

    static CharacterSpawnValidation()
    {
        EditorApplication.playModeStateChanged += state =>
        {
            if (state != PlayModeStateChange.EnteredPlayMode || !SessionState.GetBool(Pending, false)) return;
            SessionState.SetBool(Pending, false);
            var host = new GameObject("CharacterSpawnValidationRunner");
            Object.DontDestroyOnLoad(host);
            host.AddComponent<CharacterSpawnValidationRunner>();
        };
    }

    public static void RunBatch()
    {
        // 유저 Editor PlayMode 금지
        if (!Application.isBatchMode)
            throw new InvalidOperationException("Batch mode required.");
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        SessionState.SetBool(Pending, true);
        EditorApplication.EnterPlaymode();
    }

    internal static object Field(object target, string name)
    {
        return target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target);
    }

    internal static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void DisableLateSubscribers(Scene scene, LoadSceneMode mode)
    {
        if (scene.name != "InGame") return;
        SceneManager.sceneLoaded -= DisableLateSubscribers;
        AugmentManager.Instance.enabled = false;
        QuestManager.Instance.enabled = false;
    }

    private static void RequireRejectedSpawn(SpawnManager manager, GameObject prefab, string expectedError)
    {
        typeof(SpawnManager).GetField("playerPrefab", BindingFlags.Instance | BindingFlags.NonPublic)
            .SetValue(manager, prefab);
        int errors = 0;
        int notifications = 0;
        Application.LogCallback observeError = (message, stack, type) =>
        {
            if (type == LogType.Error && message == expectedError) errors++;
        };
        Action<GameObject> observeSpawn = player => notifications++;
        Application.logMessageReceived += observeError;
        SpawnManager.OnPlayerSpawned += observeSpawn;
        try
        {
            bool spawned = (bool)typeof(SpawnManager)
                .GetMethod("SpawnPlayer", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(manager, null);
            Require(!spawned && manager.SpawnedPlayer == null && notifications == 0 && errors == 1,
                "Invalid spawn configuration must fail explicitly without spawning or notifying: " + expectedError);
        }
        finally
        {
            Application.logMessageReceived -= observeError;
            SpawnManager.OnPlayerSpawned -= observeSpawn;
        }
    }

    internal static IEnumerator Validate()
    {
        var ninjaPrefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Player/NinjaPlayer.prefab");
        var managerPrefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Manager/Managers.prefab");
        Require(ninjaPrefab != null && managerPrefab != null
            && (GameObject)Field(managerPrefab.GetComponent<SpawnManager>(), "playerPrefab") == ninjaPrefab,
            "Managers prefab must directly bind the default Ninja prefab");
        var spawnFixture = new GameObject("ValidationSpawnFixture");
        spawnFixture.SetActive(false);
        var invalidPrefab = new GameObject("InvalidPlayerPrefab");
        invalidPrefab.SetActive(false);
        try
        {
            var manager = spawnFixture.AddComponent<SpawnManager>();
            const string invalidError = "Player Prefab must be assigned and have a valid PlayerCharacter.";
            RequireRejectedSpawn(manager, null, invalidError);
            RequireRejectedSpawn(manager, invalidPrefab, invalidError);
            invalidPrefab.AddComponent<PlayerCharacter>().SetCharacterId((PlayerCharacterId)99);
            RequireRejectedSpawn(manager, invalidPrefab, invalidError);
            RequireRejectedSpawn(manager, ninjaPrefab, "PlayerSpawnPoint tag could not be found.");
            Require(SpawnManager.Instance == null && GameObject.FindGameObjectsWithTag("Player").Length == 0,
                "Invalid spawn fixtures changed global player ownership");
        }
        finally
        {
            Object.Destroy(spawnFixture);
            Object.Destroy(invalidPrefab);
        }
        Debug.Log("DIRECT_PREFAB_INVALID_CONFIGURATION_PASS (expected errors above)");

        yield return SceneManager.LoadSceneAsync("InGame");
        AsyncOperation abandonedLoad = null;
        float startupDeadline = Time.realtimeSinceStartup + 45f;
        while (abandonedLoad == null)
        {
            Require(Time.realtimeSinceStartup < startupDeadline, "Startup cancellation setup timed out");
            if (GameManager.Instance != null)
                abandonedLoad = (AsyncOperation)Field(GameManager.Instance, "pendingMapLoad");
            if (abandonedLoad == null) yield return null;
        }
        Require(!abandonedLoad.allowSceneActivation, "Expected activation-gated map load");
        Object.Destroy(GameManager.Instance.gameObject);
        yield return null;
        Require(abandonedLoad.allowSceneActivation, "Destroyed owner left scene activation blocked");
        AsyncOperation recovery = SceneManager.LoadSceneAsync("CharacterTest");
        while (!recovery.isDone)
        {
            Require(Time.realtimeSinceStartup < startupDeadline, "Abandoned load blocked scene operation queue");
            yield return null;
        }
        Require(GameManager.Instance == null, "Cancelled startup created a session owner");
        Require(SceneManager.GetSceneByName("CharacterTest").isLoaded,
            "Cancellation recovery scene did not finish loading");
        var recoveryController = Object.FindFirstObjectByType<SkillTestSceneController>();
        while (recoveryController != null && recoveryController.CurrentPlayer == null)
        {
            Require(Time.realtimeSinceStartup < startupDeadline, "Recovery test player timed out");
            yield return null;
        }
        Require(recoveryController != null && recoveryController.CurrentPlayer != null
            && GameManager.Instance == null && SpawnManager.Instance == null,
            "Recovery must contain only its isolated test player, not a cancelled combat owner");
        GameObject recoveryPlayer = recoveryController.CurrentPlayer;
        Debug.Log("CHARACTER_SESSION_STARTUP_CANCEL_PASS");

        int previousSessionId = 0;
        foreach (var id in new[] { PlayerCharacterId.Ninja, PlayerCharacterId.Sword, PlayerCharacterId.Wizard })
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Player/" + id + "Player.prefab");
            Require(prefab != null && prefab.GetComponent<PlayerCharacter>().CharacterId == id,
                "Direct-binding test prefab is missing or has the wrong identity: " + id);
            UnityEngine.Events.UnityAction<Scene, LoadSceneMode> bindPrefab = (scene, mode) =>
            {
                if (scene.name != "InGame") return;
                typeof(SpawnManager).GetField("playerPrefab", BindingFlags.Instance | BindingFlags.NonPublic)
                    .SetValue(SpawnManager.Instance, prefab);
            };
            SceneManager.sceneLoaded += bindPrefab;
            SceneManager.sceneLoaded += DisableLateSubscribers;
            try { yield return SceneManager.LoadSceneAsync("InGame"); }
            finally
            {
                SceneManager.sceneLoaded -= bindPrefab;
                SceneManager.sceneLoaded -= DisableLateSubscribers;
            }
            float deadline = Time.realtimeSinceStartup + 45f;
            while (SpawnManager.Instance == null || SpawnManager.Instance.SpawnedPlayer == null)
            {
                Require(Time.realtimeSinceStartup < deadline, "Player spawn timed out: " + id);
                yield return null;
            }

            var manager = SpawnManager.Instance;
            var player = manager.SpawnedPlayer;
            var session = GameManager.Instance;
            Require((GameObject)Field(manager, "playerPrefab") == prefab
                && player.name == prefab.name + "(Clone)",
                "Spawned player must be the directly assigned prefab");
            Require(!SceneManager.GetSceneByName("CharacterTest").isLoaded,
                "InGame load retained cancellation recovery fixture");
            Require(recoveryPlayer == null && recoveryController == null,
                "InGame load retained isolated recovery player/controller");
            Require(RunStats.Kills == 0, "Fresh InGame did not reset previous kill count");
            RunStats.AddKill();
            int sessionId = session.SessionId;
            Require(sessionId > previousSessionId && session.CurrentPlayer == player,
                "Session ownership must not depend on enabled augment/quest subscribers");
            session.EndCombatSession(previousSessionId);
            Require(session.BelongsToSession(sessionId), "Old session token ended current session");
            previousSessionId = sessionId;

            var augments = AugmentManager.Instance;
            var quests = QuestManager.Instance;
            Require(!augments.enabled && !quests.enabled, "Late subscribers were not disabled before spawn");
            augments.enabled = true;
            quests.enabled = true;
            Require((PlayerStats)Field(augments, "playerStats") == player.GetComponent<PlayerStats>(),
                "Late augment enable failed to bind snapshot");
            Require((GameObject)Field(quests, "player") == player,
                "Late quest enable failed to bind snapshot");
            typeof(QuestManager).GetMethod("BeginCountdown", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(quests, new object[] { 30f });
            float countdown = (float)Field(quests, "countdown");
            quests.enabled = false;
            quests.enabled = true;
            Require(quests.State == QuestState.Countdown && (float)Field(quests, "countdown") == countdown,
                "Same player reenable reset quest progress");

            var life = Resources.Load<SkillAugmentSO>("Augments/Data/BasicAttackLifeSteal_Data");
            AugmentApplyStatus? acquired = null;
            yield return augments.ApplyAugmentRoutine(life, sessionId, 0, (status, error) => acquired = status);
            Require(acquired == AugmentApplyStatus.Applied && player.GetComponent<PlayerAugments>().HasSkill(life),
                "Live session failed to load and apply augment");
            session.PauseGame();
            UIManager.Instance.RequestAugmentChoice();
            int tickets = (int)Field(UIManager.Instance, "pendingTickets");
            // 중복 프리팹: SessionId·Audio 유지
            AudioManager originalAudio = AudioManager.Instance;
            var dup = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Manager/Managers.prefab"));
            GameManager dupGm = dup.GetComponent<GameManager>();
            Require(dupGm != session && dupGm.SessionId == 0, "Duplicate GameManager minted a session");
            Require(AudioManager.Instance == originalAudio
                && !ReferenceEquals(AudioManager.Instance, dup.GetComponent<AudioManager>()),
                "Duplicate AudioManager replaced Instance");
            yield return null;
            Require(GameManager.Instance == session && AugmentManager.Instance == augments
                && SpawnManager.Instance == manager, "Duplicate managers replaced authoritative instances");
            Require(session.BelongsToSession(sessionId) && player.GetComponent<PlayerAugments>().HasSkill(life)
                && (int)Field(UIManager.Instance, "pendingTickets") == tickets,
                "Rejected duplicate cleared live session, augments or UI");
            UIManager.Instance.ClearSession(true);
            Require(player.GetComponent<PlayerCharacter>().CharacterId == id, "Wrong character spawned");
            Require(SceneManager.GetSceneByName("Map").isLoaded, "Player spawned before map loaded");
            Require(GameObject.FindGameObjectsWithTag("Player").Length == 1, "Duplicate players");
            Require(GameObject.Find("IngameManager") == null && GameObject.Find("SpawnPoint") == null,
                "Obsolete scene spawning objects remain");
            var hud = Object.FindFirstObjectByType<CombatHudUI>();
            hud.enabled = false;
            hud.enabled = true;
            Require(hud != null && (PlayerSkillController)Field(hud, "skills") == player.GetComponent<PlayerSkillController>(),
                "Skill HUD is not bound to spawned player");
            int healthBars = 0;
            foreach (var bar in Object.FindObjectsByType<PlayerHealthBarUI>(FindObjectsSortMode.None))
            {
                if (!(bool)Field(bar, "bindSpawnedPlayer")) continue;
                healthBars++;
                Require((PlayerHealth)Field(bar, "playerHealth") == player.GetComponent<PlayerHealth>(), "Health HUD not bound");
            }
            Require(healthBars > 0, "No player HUD health bar found");
            var ending = Object.FindFirstObjectByType<GameEndHandler>();
            Require(ending != null && (PlayerStateManager)Field(ending, "stateManager") == player.GetComponent<PlayerStateManager>(),
                "Death handler not bound");
            var spawn = typeof(SpawnManager).GetMethod("SpawnPlayer", BindingFlags.NonPublic | BindingFlags.Instance);
            spawn.Invoke(manager, null);
            Require(manager.SpawnedPlayer == player && GameObject.FindGameObjectsWithTag("Player").Length == 1,
                "Repeated spawn created another player");

            // 로딩을 기다리는 요청은 소유 세션 종료 후 적용할 수 없다.
            var fire = Resources.Load<SkillAugmentSO>("Augments/Data/FireAura_Data");
            AugmentApplyStatus? applyStatus = null;
            IEnumerator pending = augments.ApplyAugmentRoutine(fire, sessionId, 0,
                (status, error) => applyStatus = status);
            Require(pending.MoveNext(), "Expected asynchronous skill acquisition");
            IEnumerator loading = (IEnumerator)pending.Current;
            Require(loading.MoveNext(), "Expected resource request");
            session.EndCombatSession(sessionId);
            yield return loading.Current;
            while (loading.MoveNext()) yield return loading.Current;
            while (pending.MoveNext()) yield return pending.Current;
            Require(applyStatus == AugmentApplyStatus.Stale
                && player.GetComponent<PlayerAugments>().OwnedSkills.Count == 0,
                "Pending request survived session end");
            // 세션 종료 후 퀘스트 정지
            Require(quests.State == QuestState.Stopped
                && Field(quests, "activeEventMonster") == null,
                "Session end left quest running");
            // 진행 중 로드 콜백: 캐시 재오염 금지
            var loader = AugmentResourceLoader.Instance;
            var cache = (System.Collections.IDictionary)Field(loader, "prefabCache");
            Require(!cache.Contains(fire.SkillResourcePath),
                "Stale FireAura callback filled prefab cache");
            session.EndCombatSession(sessionId);
            Require(session.SessionId == 0, "Session end must be idempotent");
            float releaseDeadline = Time.realtimeSinceStartup + 30f;
            while (loader.IsReleasing)
            {
                Require(Time.realtimeSinceStartup < releaseDeadline, "Resource drain timed out");
                yield return null;
            }
            yield return loader.WaitForRelease();
            loader.InitializeMetadata();
            bool secondInitThrew = false;
            try { loader.InitializeMetadata(); }
            catch (InvalidOperationException) { secondInitThrew = true; }
            Require(secondInitThrew, "Second InitializeMetadata must fail while ready");
            loader.ReleaseResources();
            yield return loader.WaitForRelease();
            int inGameHandle = SceneManager.GetSceneByName("InGame").handle;
            int mapHandle = SceneManager.GetSceneByName("Map").handle;
            int activeHandle = SceneManager.GetActiveScene().handle;
            int deathLoads = 0;
            UnityEngine.Events.UnityAction<Scene, LoadSceneMode> recordDeathLoad = (scene, mode) => deathLoads++;
            SceneManager.sceneLoaded += recordDeathLoad;
            try
            {
                player.GetComponent<PlayerStateManager>().ChangeState(PlayerState.Dead);
                Require((bool)Field(ending, "ended") && GameResult.Data != null,
                    "Death did not capture result");
                Require(session.SessionId == 0 && session.CurrentPlayer == null,
                    "Death reopened ended session");
                RequireStableScenes(inGameHandle, mapHandle, activeHandle, deathLoads);
            }
            finally { SceneManager.sceneLoaded -= recordDeathLoad; }
            Debug.Log("CHARACTER_SPAWN_PASS: " + id
                + " session ownership, late binding, duplicate managers, stale request, resource drain, UI and same-scene death");
        }

        // 외부에서 플레이어가 제거되어도 그 플레이어를 소유한 세션만 종료한다.
        yield return SceneManager.LoadSceneAsync("InGame");
        float spawnDeadline = Time.realtimeSinceStartup + 45f;
        while (GameManager.Instance == null || GameManager.Instance.CurrentPlayer == null)
        {
            Require(Time.realtimeSinceStartup < spawnDeadline, "Removal test spawn timed out");
            yield return null;
        }
        var owner = GameManager.Instance;
        int ownedId = owner.SessionId;
        var unrelated = new GameObject("UnrelatedPlayerAugments");
        unrelated.AddComponent<PlayerAugments>();
        Object.Destroy(unrelated);
        yield return null;
        Require(owner.BelongsToSession(ownedId), "Unrelated player removal ended session");
        Object.Destroy(owner.CurrentPlayer);
        yield return null;
        Require(owner.SessionId == 0 && owner.CurrentPlayer == null, "Owned player removal did not end session");
        Debug.Log("CHARACTER_SESSION_REMOVAL_PASS");

        yield return SceneManager.LoadSceneAsync("InGame");
        spawnDeadline = Time.realtimeSinceStartup + 45f;
        while (GameManager.Instance == null || GameManager.Instance.CurrentPlayer == null)
        {
            Require(Time.realtimeSinceStartup < spawnDeadline, "Map unload test spawn timed out");
            yield return null;
        }
        owner = GameManager.Instance;
        yield return SceneManager.UnloadSceneAsync("Map");
        Require(owner.SessionId == 0, "Map unload did not end session");
        Debug.Log("CHARACTER_SESSION_MAP_UNLOAD_PASS");

        yield return SceneManager.LoadSceneAsync("InGame");
        spawnDeadline = Time.realtimeSinceStartup + 45f;
        while (GameManager.Instance == null || GameManager.Instance.CurrentPlayer == null)
        {
            Require(Time.realtimeSinceStartup < spawnDeadline, "Death test spawn timed out");
            yield return null;
        }
        owner = GameManager.Instance;
        GameObject dyingPlayer = owner.CurrentPlayer;
        Require(RunStats.Kills == 0, "Fresh death-test InGame did not reset kill count");
        var deathHandler = Object.FindFirstObjectByType<GameEndHandler>();
        Require(deathHandler != null && !(bool)Field(deathHandler, "ended"),
            "Live death handler missing or already ended");
        int liveSessionId = owner.SessionId;
        Require(liveSessionId > 0 && owner.BelongsToSession(liveSessionId), "Death test requires live session");
        int deathInGameHandle = SceneManager.GetSceneByName("InGame").handle;
        int deathMapHandle = SceneManager.GetSceneByName("Map").handle;
        int deathActiveHandle = SceneManager.GetActiveScene().handle;
        int loadedAfterDeath = 0;
        UnityEngine.Events.UnityAction<Scene, LoadSceneMode> recordLoad = (scene, mode) => loadedAfterDeath++;
        SceneManager.sceneLoaded += recordLoad;
        try
        {
            RunStats.AddKill();
            var stats = dyingPlayer.GetComponent<PlayerStats>();
            var expected = new ResultData
            {
                isVictory = false,
                survivalTime = Time.time - (float)Field(deathHandler, "combatStartTime"),
                monsterKills = RunStats.Kills,
                finalLevel = dyingPlayer.GetComponent<PlayerLevelSystem>().level,
                attackDamage = stats.AttackDamage,
                hp = 0f,
                maxHp = stats.HP,
                speed = stats.MoveSpeed,
                attackSpeed = stats.AttackSpeed,
                defense = stats.Defense
            };
            var state = dyingPlayer.GetComponent<PlayerStateManager>();
            state.ChangeState(PlayerState.Dead);
            Require(owner.SessionId == 0 && owner.CurrentPlayer == null
                && !owner.BelongsToSession(liveSessionId) && (bool)Field(deathHandler, "ended"),
                "Death must synchronously close its live owning session");
            ResultData captured = GameResult.Data;
            RequireResult(captured, expected);
            state.ChangeState(PlayerState.Dead);
            typeof(GameEndHandler).GetMethod("HandleDead", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(deathHandler, null);
            Require(ReferenceEquals(GameResult.Data, captured), "Duplicate death replaced captured result");
            float observeUntil = Time.realtimeSinceStartup + 2f;
            float deathDeadline = Time.realtimeSinceStartup + 30f;
            var deathLoader = AugmentResourceLoader.Instance;
            while (Time.realtimeSinceStartup < observeUntil || deathLoader.IsReleasing)
            {
                Require(Time.realtimeSinceStartup < deathDeadline, "Death observation/resource drain timed out");
                RequireStableScenes(deathInGameHandle, deathMapHandle, deathActiveHandle, loadedAfterDeath);
                Require(owner.SessionId == 0 && owner.CurrentPlayer == null
                    && SpawnManager.Instance.SpawnedPlayer == dyingPlayer,
                    "Death created a replacement player or new session");
                yield return null;
            }
            yield return deathLoader.WaitForRelease();
            Require(!deathLoader.IsReleasing, "Death resources did not settle");
            RequireStableScenes(deathInGameHandle, deathMapHandle, deathActiveHandle, loadedAfterDeath);
            Require(owner.SessionId == 0 && owner.CurrentPlayer == null
                && SpawnManager.Instance.SpawnedPlayer == dyingPlayer,
                "Post-death session/player ownership changed");
            Require(ReferenceEquals(GameResult.Data, captured), "Post-death result was replaced");
            RequireResult(GameResult.Data, expected);
            Require(Time.timeScale == 1f && Cursor.visible && Cursor.lockState == CursorLockMode.None,
                "Death cleanup did not restore time scale and cursor");
        }
        finally { SceneManager.sceneLoaded -= recordLoad; }
        Debug.Log("CHARACTER_SESSION_DEATH_PASS");
    }

    private static void RequireStableScenes(int inGameHandle, int mapHandle, int activeHandle, int loads)
    {
        Require(loads == 0 && SceneManager.GetSceneByName("InGame").isLoaded
            && SceneManager.GetSceneByName("InGame").handle == inGameHandle
            && SceneManager.GetSceneByName("Map").isLoaded
            && SceneManager.GetSceneByName("Map").handle == mapHandle
            && SceneManager.GetActiveScene().handle == activeHandle,
            "Death changed loaded or active scenes");
    }

    private static void RequireResult(ResultData actual, ResultData expected)
    {
        Require(actual != null && actual.isVictory == expected.isVictory
            && actual.survivalTime == expected.survivalTime && actual.monsterKills == expected.monsterKills
            && actual.finalLevel == expected.finalLevel && actual.attackDamage == expected.attackDamage
            && actual.hp == expected.hp && actual.maxHp == expected.maxHp && actual.speed == expected.speed
            && actual.attackSpeed == expected.attackSpeed && actual.defense == expected.defense,
            "Death result does not match pre-release stats");
    }
}

public sealed class CharacterSpawnValidationRunner : MonoBehaviour
{
    private IEnumerator Start()
    {
        var routine = CharacterSpawnValidation.Validate();
        while (true)
        {
            object current;
            try
            {
                if (!routine.MoveNext()) break;
                current = routine.Current;
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorApplication.Exit(1);
                yield break;
            }
            yield return current;
        }
        Debug.Log("CHARACTER_SPAWN_VALIDATION_PASS");
        EditorApplication.Exit(0);
    }
}
