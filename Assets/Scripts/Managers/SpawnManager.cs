using System;
using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;

// 오프라인 플레이어·적 스폰 및 네트워크 플레이어 생성 준비.
public class SpawnManager : MonoBehaviour
{
    public static SpawnManager Instance;

    [Header("Prefabs")]
    [SerializeField] private GameObject playerPrefab;
    [SerializeField] private GameObject enemyPrefab;
    [SerializeField] private List<EnemySpawnEntry> enemySpawnEntries = new List<EnemySpawnEntry>();

    [Header("Initial Enemy Spawn")]
    [SerializeField] private bool spawnEnemiesOnStart = true;
    [SerializeField] private bool spawnAllEnemyPointsOnStart = true;
    [SerializeField] private int initialEnemyCount;

    [Header("Enemy Auto Spawn")]
    [SerializeField] private bool enableEnemyAutoSpawn = true;
    [SerializeField] private float enemySpawnInterval = 5f;
    [SerializeField] private int enemiesPerSpawn = 1;
    [SerializeField] private int maxAliveEnemies = 12;

    [Header("Dynamic Difficulty")]
    [SerializeField] private bool enableDynamicDifficulty = true;
    [SerializeField] private float difficultyStepDuration = 60f;
    [SerializeField] private float spawnIntervalDecreasePerStep = 0.4f;
    [SerializeField] private float minimumSpawnInterval = 2.5f;
    [SerializeField] private int extraEnemyEverySteps = 3;
    [SerializeField] private int maxEnemiesIncreasePerStep = 2;
    [SerializeField] private int maximumAliveEnemies = 24;

    [Header("Spawn Distance")]
    [SerializeField] private float minSpawnDistanceFromPlayer = 8f;
    [SerializeField] private float maxSpawnDistanceFromPlayer = 60f;
    [SerializeField] private int spawnPointPickAttempts = 12;

    private readonly List<GameObject> activeEnemies = new List<GameObject>();
    private readonly List<Transform> enemySpawnPoints = new List<Transform>();
    private readonly Dictionary<GameObject, GameObject> activeEnemyPrefabs = new Dictionary<GameObject, GameObject>();

    private GameObject spawnedPlayer;
    private PlayerLevelSystem playerLevelSystem;
    private Coroutine enemySpawnRoutine;
    private float spawnStartTime;
    private bool sessionEnded;

    public int AliveEnemyCount
    {
        get
        {
            if (CanRunOfflineSpawning) RemoveInactiveEnemies();
            return activeEnemies.Count;
        }
    }

    private bool CanRunOfflineSpawning => !sessionEnded && !NetworkGameSession.IsNetworkSessionActive;

    private void Awake()
    {
        if (Instance == null)
        {
            Instance = this;
        }
        else
        {
            // 중복 거부
            enabled = false;
            Destroy(gameObject);
        }
    }

    private void OnDestroy()
    {
        if (Instance != this) return;
        Instance = null;
        // 소유 플레이어 소실
        GameManager.Instance?.NotifyPlayerUnavailable(spawnedPlayer);
        spawnedPlayer = null;
    }

    private void OnDisable()
    {
        StopEnemyAutoSpawn();
        UnbindPlayerLevelSystem();
    }

    public void EndSession()
    {
        if (sessionEnded) return;
        sessionEnded = true;
        StopEnemyAutoSpawn();
        UnbindPlayerLevelSystem();
        ResetEnemyUnlockSpawns();
        enemySpawnPoints.Clear();
    }

    public void SpawnAll()
    {
        if (!CanRunOfflineSpawning) return;
        // 맵 씬 로드가 끝난 뒤 플레이어와 적 스폰을 한 번에 초기화
        spawnStartTime = Time.time;
        ResetEnemyUnlockSpawns();
        if (!SpawnPlayer() || !CanRunOfflineSpawning) return;
        CacheEnemySpawnPoints();
        SpawnInitialEnemies();
        if (!CanRunOfflineSpawning) return;
        StartEnemyAutoSpawn();
        TrySpawnUnlockEnemies();
    }

    private bool SpawnPlayer()
    {
        if (!CanRunOfflineSpawning) return false;
        if (spawnedPlayer != null) return true;

        if (playerPrefab == null || !playerPrefab.TryGetComponent(out PlayerCharacter character)
            || !Enum.IsDefined(typeof(PlayerCharacterId), character.CharacterId))
        {
            Debug.LogError("Player Prefab must be assigned and have a valid PlayerCharacter.", this);
            return false;
        }

        GameObject[] spawnPoints = GameObject.FindGameObjectsWithTag("PlayerSpawnPoint");

        if (spawnPoints == null || spawnPoints.Length == 0)
        {
            Debug.LogError("PlayerSpawnPoint tag could not be found.");
            return false;
        }

        int randomIndex = UnityEngine.Random.Range(0, spawnPoints.Length);
        Transform selectedSpawn = spawnPoints[randomIndex].transform;

        spawnedPlayer = Instantiate(playerPrefab, selectedSpawn.position, selectedSpawn.rotation);
        BindPlayerLevelSystem(spawnedPlayer);
        if (GameManager.Instance == null || !GameManager.Instance.RegisterLocalPlayer(spawnedPlayer))
        {
            Debug.LogError("The offline player could not be registered with GameManager.", this);
            UnbindPlayerLevelSystem();
            Destroy(spawnedPlayer);
            spawnedPlayer = null;
            return false;
        }
        return true;
    }

    public bool TryGetNetworkSpawnPoints(Scene map, out Transform[] points)
    {
        points = Array.Empty<Transform>();
        if (!NetworkGameSession.IsNetworkSessionActive || sessionEnded ||
            !map.IsValid() || !map.isLoaded || map.name != "Map")
        {
            Debug.LogError("Network spawn points require an active network session and a loaded Map scene.", this);
            return false;
        }

        List<Transform> found = new List<Transform>(2);
        foreach (GameObject root in map.GetRootGameObjects())
        {
            foreach (Transform candidate in root.GetComponentsInChildren<Transform>(true))
            {
                if (candidate.CompareTag("PlayerSpawnPoint"))
                    found.Add(candidate);
            }
        }

        found.Sort(CompareSceneHierarchy);
        if (found.Count != 2)
        {
            Debug.LogError($"Map must contain exactly two PlayerSpawnPoint transforms; found {found.Count}.", this);
            return false;
        }

        points = found.ToArray();
        return true;
    }

    public GameObject SpawnNetworkPlayer(GameObject prefab, Vector3 position, Quaternion rotation, Scene scene)
    {
        NetworkManager networkManager = NetworkManager.Singleton;
        if (!NetworkGameSession.IsNetworkSessionActive || sessionEnded || networkManager == null ||
            !networkManager.IsListening || !networkManager.IsServer)
        {
            Debug.LogError("Network player spawning is server-only and requires an active network session.", this);
            return null;
        }
        if (!scene.IsValid() || !scene.isLoaded || scene.name != "Map")
        {
            Debug.LogError("Network players can only be spawned into a loaded Map scene.", this);
            return null;
        }
        if (prefab == null || prefab.scene.IsValid() ||
            !prefab.TryGetComponent(out NetworkObject networkPrefab) || networkPrefab.IsSpawned ||
            !prefab.TryGetComponent(out PlayerCharacter character) ||
            !Enum.IsDefined(typeof(PlayerCharacterId), character.CharacterId) ||
            !networkManager.NetworkConfig.Prefabs.Contains(prefab) ||
            !IsFinite(position) || !IsFinite(rotation) || Quaternion.Dot(rotation, rotation) < 0.0001f)
        {
            Debug.LogError("Network player prefab must be registered with NGO and have a valid NetworkObject and PlayerCharacter; pose must be finite.", this);
            return null;
        }

        GameObject player = null;
        try
        {
            player = Instantiate(prefab, position, rotation);
            SceneManager.MoveGameObjectToScene(player, scene);
            return player;
        }
        catch (Exception exception)
        {
            Debug.LogError("Failed to instantiate or move the network player into Map: " + exception.Message, this);
            if (player != null) Destroy(player);
            return null;
        }
    }

    private static bool IsFinite(Vector3 value) => IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);

    private static bool IsFinite(Quaternion value) => IsFinite(value.x) && IsFinite(value.y) &&
        IsFinite(value.z) && IsFinite(value.w);

    private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

    private static int CompareSceneHierarchy(Transform left, Transform right)
    {
        List<int> leftIndices = GetHierarchyIndices(left);
        List<int> rightIndices = GetHierarchyIndices(right);
        int sharedDepth = Mathf.Min(leftIndices.Count, rightIndices.Count);
        for (int i = 0; i < sharedDepth; i++)
        {
            int comparison = leftIndices[i].CompareTo(rightIndices[i]);
            if (comparison != 0) return comparison;
        }
        return leftIndices.Count.CompareTo(rightIndices.Count);
    }

    private static List<int> GetHierarchyIndices(Transform target)
    {
        List<int> indices = new List<int>();
        for (Transform current = target; current != null; current = current.parent)
            indices.Add(current.GetSiblingIndex());
        indices.Reverse();
        return indices;
    }

    private void CacheEnemySpawnPoints()
    {
        if (!CanRunOfflineSpawning) return;
        enemySpawnPoints.Clear();

        // Map 씬에 배치된 EnemySpawnPoint 태그 오브젝트를 캐싱
        GameObject[] points = GameObject.FindGameObjectsWithTag("EnemySpawnPoint");

        if (points == null)
        {
            return;
        }

        foreach (GameObject point in points)
        {
            if (point != null)
            {
                enemySpawnPoints.Add(point.transform);
            }
        }
    }

    // 일반 스폰 목록과 별도로 퀘스트 몬스터를 NavMesh 위에 배치
    public bool TrySpawnQuestEnemy(GameObject prefab, out GameObject enemy)
    {
        enemy = null;
        if (!CanRunOfflineSpawning || prefab == null) return false;
        if (enemySpawnPoints.Count == 0) CacheEnemySpawnPoints();
        if (enemySpawnPoints.Count == 0) return false;

        int start = UnityEngine.Random.Range(0, enemySpawnPoints.Count);
        for (int pass = 0; pass < 2; pass++)
        {
            for (int i = 0; i < enemySpawnPoints.Count; i++)
            {
                Transform point = enemySpawnPoints[(start + i) % enemySpawnPoints.Count];
                if (point == null || (pass == 0 && !IsValidEnemySpawnPoint(point))) continue;
                if (!NavMesh.SamplePosition(point.position, out NavMeshHit hit, 2f, NavMesh.AllAreas)) continue;
                enemy = SpawnEnemyAt(point, prefab, hit.position);
                return enemy != null;
            }
        }
        return false;
    }

    private void SpawnInitialEnemies()
    {
        if (!CanRunOfflineSpawning || !spawnEnemiesOnStart || !HasAnyEnemyPrefab() || enemySpawnPoints.Count == 0)
        {
            return;
        }

        if (spawnAllEnemyPointsOnStart)
        {
            foreach (Transform spawnPoint in enemySpawnPoints)
            {
                if (AliveEnemyCount >= GetCurrentMaxAliveEnemies())
                {
                    break;
                }

                SpawnEnemyAt(spawnPoint, PickEnemyPrefab(true));
            }

            return;
        }

        for (int i = 0; i < initialEnemyCount; i++)
        {
            if (AliveEnemyCount >= GetCurrentMaxAliveEnemies())
            {
                break;
            }

            Transform spawnPoint = PickEnemySpawnPoint();

            if (spawnPoint == null)
            {
                break;
            }

            SpawnEnemyAt(spawnPoint, PickEnemyPrefab(true));
        }
    }

    private void StartEnemyAutoSpawn()
    {
        if (!CanRunOfflineSpawning || !enableEnemyAutoSpawn || !HasAnyEnemyPrefab() || enemySpawnPoints.Count == 0)
        {
            return;
        }

        StopEnemyAutoSpawn();
        enemySpawnRoutine = StartCoroutine(EnemySpawnRoutine());
    }

    private void StopEnemyAutoSpawn()
    {
        if (enemySpawnRoutine == null)
        {
            return;
        }

        StopCoroutine(enemySpawnRoutine);
        enemySpawnRoutine = null;
    }

    private IEnumerator EnemySpawnRoutine()
    {
        while (CanRunOfflineSpawning)
        {
            yield return new WaitForSeconds(GetCurrentSpawnInterval());
            if (!CanRunOfflineSpawning) yield break;

            if (GameManager.Instance != null && GameManager.Instance.IsPaused)
            {
                continue;
            }

            // 생존 수 제한으로 미뤄진 레벨 해금 스폰을 일반 스폰보다 먼저 재시도
            TrySpawnUnlockEnemies();

            for (int i = 0; i < GetCurrentEnemiesPerSpawn(); i++)
            {
                if (!TrySpawnRandomEnemy())
                {
                    break;
                }
            }
        }
    }

    private bool TrySpawnRandomEnemy()
    {
        if (!CanRunOfflineSpawning) return false;
        RemoveInactiveEnemies();

        if (!HasAnyEnemyPrefab() || enemySpawnPoints.Count == 0 || activeEnemies.Count >= GetCurrentMaxAliveEnemies())
        {
            return false;
        }

        Transform spawnPoint = PickEnemySpawnPoint();

        if (spawnPoint == null)
        {
            return false;
        }

        SpawnEnemyAt(spawnPoint, PickEnemyPrefab(false));
        return true;
    }

    private Transform PickEnemySpawnPoint()
    {
        if (enemySpawnPoints.Count == 0)
        {
            return null;
        }

        for (int i = 0; i < spawnPointPickAttempts; i++)
        {
            Transform candidate = enemySpawnPoints[UnityEngine.Random.Range(0, enemySpawnPoints.Count)];

            // 플레이어와 너무 가깝거나 너무 먼 스폰 지점은 피함
            if (IsValidEnemySpawnPoint(candidate))
            {
                return candidate;
            }
        }

        return FindFallbackEnemySpawnPoint();
    }

    private Transform FindFallbackEnemySpawnPoint()
    {
        if (spawnedPlayer == null)
        {
            return enemySpawnPoints[UnityEngine.Random.Range(0, enemySpawnPoints.Count)];
        }

        Transform fallback = null;
        float farthestDistance = float.MinValue;

        foreach (Transform spawnPoint in enemySpawnPoints)
        {
            if (spawnPoint == null)
            {
                continue;
            }

            float sqrDistance = (spawnPoint.position - spawnedPlayer.transform.position).sqrMagnitude;

            if (sqrDistance > farthestDistance)
            {
                farthestDistance = sqrDistance;
                fallback = spawnPoint;
            }
        }

        return fallback;
    }

    private bool IsValidEnemySpawnPoint(Transform spawnPoint)
    {
        if (spawnPoint == null || spawnedPlayer == null)
        {
            return spawnPoint != null;
        }

        float sqrDistance = (spawnPoint.position - spawnedPlayer.transform.position).sqrMagnitude;
        float minSqrDistance = minSpawnDistanceFromPlayer * minSpawnDistanceFromPlayer;

        if (sqrDistance < minSqrDistance)
        {
            return false;
        }

        if (maxSpawnDistanceFromPlayer <= 0f)
        {
            return true;
        }

        float maxSqrDistance = maxSpawnDistanceFromPlayer * maxSpawnDistanceFromPlayer;
        return sqrDistance <= maxSqrDistance;
    }

    private GameObject SpawnEnemyAt(Transform spawnPoint, GameObject prefab, Vector3? position = null)
    {
        if (!CanRunOfflineSpawning || spawnPoint == null || prefab == null)
        {
            return null;
        }

        GameObject enemy = Instantiate(prefab, position ?? spawnPoint.position, spawnPoint.rotation);
        activeEnemies.Add(enemy);
        activeEnemyPrefabs[enemy] = prefab;
        return enemy;
    }

    private void BindPlayerLevelSystem(GameObject playerObject)
    {
        UnbindPlayerLevelSystem();

        if (playerObject != null && playerObject.TryGetComponent(out playerLevelSystem))
        {
            playerLevelSystem.onLevelUp += HandlePlayerLevelUp;
        }
    }

    private void UnbindPlayerLevelSystem()
    {
        if (playerLevelSystem != null)
        {
            playerLevelSystem.onLevelUp -= HandlePlayerLevelUp;
            playerLevelSystem = null;
        }
    }

    private void HandlePlayerLevelUp()
    {
        if (!CanRunOfflineSpawning) return;
        // 특정 레벨에 도달했을 때 Giant 같은 해금 적을 즉시 한 번 스폰
        TrySpawnUnlockEnemies();
    }

    private void TrySpawnUnlockEnemies()
    {
        if (!CanRunOfflineSpawning || enemySpawnEntries == null)
        {
            return;
        }

        if (enemySpawnPoints.Count == 0)
        {
            Debug.LogWarning("Enemy spawn points not found. Check objects tagged EnemySpawnPoint.");
            return;
        }

        RemoveInactiveEnemies();
        int playerLevel = GetPlayerLevel();

        foreach (EnemySpawnEntry entry in enemySpawnEntries)
        {
            if (!CanRunOfflineSpawning) return;
            if (!CanForceSpawnUnlockEnemy(entry, playerLevel))
            {
                continue;
            }

            Transform spawnPoint = PickEnemySpawnPoint();

            if (spawnPoint == null)
            {
                return;
            }

            GameObject spawnedEnemy = SpawnEnemyAt(spawnPoint, entry.Prefab);
            if (spawnedEnemy == null || !CanRunOfflineSpawning) return;
            entry.MarkUnlockSpawned();
            Debug.Log($"Unlocked enemy spawned: {entry.Prefab.name} at player level {playerLevel}");
        }
    }

    private bool CanForceSpawnUnlockEnemy(EnemySpawnEntry entry, int playerLevel)
    {
        if (entry == null || !entry.ForceSpawnOnUnlock || entry.UnlockSpawned || entry.Prefab == null)
        {
            return false;
        }

        if (activeEnemies.Count >= GetCurrentMaxAliveEnemies() || playerLevel < entry.MinPlayerLevel)
        {
            return false;
        }

        return entry.MaxAliveCount <= 0 || CountAliveEnemiesForPrefab(entry.Prefab) < entry.MaxAliveCount;
    }

    private GameObject PickEnemyPrefab(bool isInitialSpawn)
    {
        if (enemySpawnEntries == null || enemySpawnEntries.Count == 0)
        {
            return enemyPrefab;
        }

        int playerLevel = GetPlayerLevel();
        int totalWeight = 0;

        // 현재 플레이어 레벨에 맞는 적만 가중치 후보로 사용
        foreach (EnemySpawnEntry entry in enemySpawnEntries)
        {
            if (IsEligibleEnemyEntry(entry, playerLevel, isInitialSpawn))
            {
                totalWeight += entry.GetSpawnWeight(playerLevel);
            }
        }

        if (totalWeight <= 0)
        {
            return enemyPrefab;
        }

        int randomWeight = UnityEngine.Random.Range(0, totalWeight);

        foreach (EnemySpawnEntry entry in enemySpawnEntries)
        {
            if (!IsEligibleEnemyEntry(entry, playerLevel, isInitialSpawn))
            {
                continue;
            }

            int currentWeight = entry.GetSpawnWeight(playerLevel);

            if (randomWeight < currentWeight)
            {
                return entry.Prefab;
            }

            randomWeight -= currentWeight;
        }

        return enemyPrefab;
    }

    private bool IsEligibleEnemyEntry(EnemySpawnEntry entry, int playerLevel, bool isInitialSpawn)
    {
        if (entry == null || entry.Prefab == null || entry.GetSpawnWeight(playerLevel) <= 0)
        {
            return false;
        }

        if (isInitialSpawn && !entry.AllowInitialSpawn)
        {
            return false;
        }

        if (playerLevel < entry.MinPlayerLevel)
        {
            return false;
        }

        return entry.MaxAliveCount <= 0 || CountAliveEnemiesForPrefab(entry.Prefab) < entry.MaxAliveCount;
    }

    private int GetPlayerLevel()
    {
        if (playerLevelSystem != null)
        {
            return playerLevelSystem.level;
        }

        if (spawnedPlayer != null && spawnedPlayer.TryGetComponent(out PlayerLevelSystem levelSystem))
        {
            return levelSystem.level;
        }

        return 1;
    }

    // 시간은 스폰 빈도와 수에만 반영하고, 적 종류의 해금은 기존 레벨 조건을 사용
    private int GetDifficultyStep()
    {
        if (!enableDynamicDifficulty || difficultyStepDuration <= 0f)
        {
            return 0;
        }

        return Mathf.FloorToInt(Mathf.Max(0f, Time.time - spawnStartTime) / difficultyStepDuration);
    }

    private float GetCurrentSpawnInterval()
    {
        if (!enableDynamicDifficulty)
        {
            return enemySpawnInterval;
        }

        float interval = enemySpawnInterval - GetDifficultyStep() * spawnIntervalDecreasePerStep;
        return Mathf.Max(minimumSpawnInterval, interval);
    }

    private int GetCurrentEnemiesPerSpawn()
    {
        if (!enableDynamicDifficulty || extraEnemyEverySteps <= 0)
        {
            return enemiesPerSpawn;
        }

        return enemiesPerSpawn + GetDifficultyStep() / extraEnemyEverySteps;
    }

    private int GetCurrentMaxAliveEnemies()
    {
        if (!enableDynamicDifficulty)
        {
            return maxAliveEnemies;
        }

        int currentMaximum = maxAliveEnemies + GetDifficultyStep() * maxEnemiesIncreasePerStep;
        return Mathf.Min(maximumAliveEnemies, currentMaximum);
    }

    private bool HasAnyEnemyPrefab()
    {
        if (enemyPrefab != null)
        {
            return true;
        }

        if (enemySpawnEntries == null)
        {
            return false;
        }

        foreach (EnemySpawnEntry entry in enemySpawnEntries)
        {
            if (entry != null && entry.Prefab != null)
            {
                return true;
            }
        }

        return false;
    }

    private int CountAliveEnemiesForPrefab(GameObject prefab)
    {
        if (prefab == null)
        {
            return 0;
        }

        int count = 0;

        foreach (GameObject enemyPrefabValue in activeEnemyPrefabs.Values)
        {
            if (enemyPrefabValue == prefab)
            {
                count++;
            }
        }

        return count;
    }

    private void RemoveInactiveEnemies()
    {
        // Destroy 되었거나 사망 처리된 적은 생존 카운트에서 제거
        for (int i = activeEnemies.Count - 1; i >= 0; i--)
        {
            GameObject enemy = activeEnemies[i];

            if (enemy == null)
            {
                RemoveTrackedEnemyAt(i, enemy);
                continue;
            }

            if (enemy.TryGetComponent(out EnemyHealth enemyHealth) && enemyHealth.IsDead)
            {
                RemoveTrackedEnemyAt(i, enemy);
            }
        }
    }

    private void RemoveTrackedEnemyAt(int index, GameObject enemy)
    {
        if (!ReferenceEquals(enemy, null))
        {
            activeEnemyPrefabs.Remove(enemy);
        }

        activeEnemies.RemoveAt(index);
    }

    private void OnValidate()
    {
        initialEnemyCount = Mathf.Max(0, initialEnemyCount);
        enemySpawnInterval = Mathf.Max(0.1f, enemySpawnInterval);
        enemiesPerSpawn = Mathf.Max(1, enemiesPerSpawn);
        maxAliveEnemies = Mathf.Max(1, maxAliveEnemies);
        difficultyStepDuration = Mathf.Max(1f, difficultyStepDuration);
        spawnIntervalDecreasePerStep = Mathf.Max(0f, spawnIntervalDecreasePerStep);
        minimumSpawnInterval = Mathf.Clamp(minimumSpawnInterval, 0.1f, enemySpawnInterval);
        extraEnemyEverySteps = Mathf.Max(1, extraEnemyEverySteps);
        maxEnemiesIncreasePerStep = Mathf.Max(0, maxEnemiesIncreasePerStep);
        maximumAliveEnemies = Mathf.Max(maxAliveEnemies, maximumAliveEnemies);
        minSpawnDistanceFromPlayer = Mathf.Max(0f, minSpawnDistanceFromPlayer);
        maxSpawnDistanceFromPlayer = Mathf.Max(0f, maxSpawnDistanceFromPlayer);
        spawnPointPickAttempts = Mathf.Max(1, spawnPointPickAttempts);

        if (enemySpawnEntries == null)
        {
            return;
        }

        foreach (EnemySpawnEntry entry in enemySpawnEntries)
        {
            if (entry != null)
            {
                entry.Validate();
            }
        }
    }

    private void ResetEnemyUnlockSpawns()
    {
        if (enemySpawnEntries == null)
        {
            return;
        }

        foreach (EnemySpawnEntry entry in enemySpawnEntries)
        {
            if (entry != null)
            {
                entry.ResetUnlockSpawned();
            }
        }
    }

    [Serializable]
    private class EnemySpawnEntry
    {
        [SerializeField] private GameObject prefab;
        [SerializeField] private int minPlayerLevel = 1;
        [SerializeField] private int spawnWeight = 1;
        [SerializeField] private bool allowInitialSpawn = true;
        [SerializeField] private int maxAliveCount;
        [SerializeField] private int additionalWeightPerLevel;
        [SerializeField] private int maxSpawnWeight;
        [SerializeField] private bool forceSpawnOnUnlock;

        [NonSerialized] private bool unlockSpawned;

        public GameObject Prefab => prefab;
        public int MinPlayerLevel => minPlayerLevel;
        public bool AllowInitialSpawn => allowInitialSpawn;
        public int MaxAliveCount => maxAliveCount;
        public bool ForceSpawnOnUnlock => forceSpawnOnUnlock;
        public bool UnlockSpawned => unlockSpawned;

        public int GetSpawnWeight(int playerLevel)
        {
            int levelBonus = Mathf.Max(0, playerLevel - minPlayerLevel) * additionalWeightPerLevel;
            int currentWeight = spawnWeight + levelBonus;

            if (maxSpawnWeight > 0)
            {
                currentWeight = Mathf.Min(currentWeight, maxSpawnWeight);
            }

            return Mathf.Max(0, currentWeight);
        }

        public void Validate()
        {
            minPlayerLevel = Mathf.Max(1, minPlayerLevel);
            spawnWeight = Mathf.Max(0, spawnWeight);
            maxAliveCount = Mathf.Max(0, maxAliveCount);
            additionalWeightPerLevel = Mathf.Max(0, additionalWeightPerLevel);
            maxSpawnWeight = Mathf.Max(0, maxSpawnWeight);
        }

        public void MarkUnlockSpawned()
        {
            unlockSpawned = true;
        }

        public void ResetUnlockSpawned()
        {
            unlockSpawned = false;
        }
    }
}
