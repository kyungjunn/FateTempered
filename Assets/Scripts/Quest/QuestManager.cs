using System;
using System.Collections.Generic;
using UnityEngine;

// 퀘스트 진행 상태
public enum QuestState
{
    WaitingForAugment,
    Countdown,
    Active,
    Stopped
}

// 페널티 실행 결과
public struct QuestPenaltyResult
{
    public SkillAugmentSO removedSkill;
    public int remainingSkillStacks;
    public StatReduceResult? reducedStat;

    public bool HasPenalty => removedSkill != null || reducedStat.HasValue;
}

// 퀘스트 진행. 세션 종료 없음.
public class QuestManager : MonoBehaviour
{
    public static QuestManager Instance { get; private set; }

    // 퀘스트 후보
    [Header("Quests")]
    [SerializeField] private List<QuestSO> quests = new List<QuestSO>();

    // 발생 간격
    [Header("Timing")]
    [SerializeField] private float firstQuestDelay = 12f;
    [SerializeField] private float questCooldown = 45f;

    public QuestState State { get; private set; } = QuestState.WaitingForAugment;
    public QuestSO CurrentQuest { get; private set; }
    public int CurrentProgress { get; private set; }
    public float RemainingTime { get; private set; }
    public QuestRewardSO CurrentReward { get; private set; }

    // UI 연동 이벤트
    public event Action<QuestSO> onQuestStarted;
    public event Action<int, int> onQuestProgressChanged;
    public event Action<float, float> onQuestTimeChanged;
    public event Action<QuestSO, QuestRewardResult> onQuestSucceeded;
    public event Action<QuestSO, QuestPenaltyResult> onQuestFailed;

    private PlayerStats playerStats;
    private PlayerAugments playerAugments;
    private GameObject player;
    private PlayerHealth playerHealth;
    private float countdown;
    private EnemyHealth activeEventMonster;
    private GameObject activeEventBeacon;

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

    private void OnEnable()
    {
        if (Instance != this) return;

        GameManager.OnLocalPlayerChanged += HandlePlayerSpawned;
        EnemyHealth.OnEnemyKilled += HandleEnemyKilled;
        // 이벤트 + 현재 스냅샷
        HandlePlayerSpawned(GameManager.Instance != null ? GameManager.Instance.CurrentPlayer : null);
    }

    private void OnDisable()
    {
        GameManager.OnLocalPlayerChanged -= HandlePlayerSpawned;
        EnemyHealth.OnEnemyKilled -= HandleEnemyKilled;
    }

    // 세션 종료: 진행 정지, 플레이어 참조 유지
    public void ClearSession()
    {
        State = QuestState.Stopped;
        countdown = 0f;
        ClearEventMonster();
        ClearCurrentQuest();
    }

    private static bool IsCombatSessionActive()
    {
        return !NetworkGameSession.IsNetworkSessionActive
            && GameManager.Instance != null && GameManager.Instance.IsActiveSession;
    }

    // 비활성 세션 → Stopped
    private bool HaltIfSessionInactive()
    {
        if (IsCombatSessionActive()) return false;
        if (State != QuestState.Stopped) ClearSession();
        return true;
    }

    private void Update()
    {
        if (HaltIfSessionInactive()) return;
        if (player == null || (playerHealth != null && playerHealth.IsDead))
        {
            if (State != QuestState.Stopped)
            {
                State = QuestState.Stopped;
                ClearEventMonster();
                ClearCurrentQuest();
            }
            return;
        }

        // 상태별 시간 처리
        switch (State)
        {
            case QuestState.WaitingForAugment:
                if (HasAnyAugment())
                {
                    BeginCountdown(firstQuestDelay);
                }
                break;

            case QuestState.Countdown:
                countdown -= Time.deltaTime;

                if (countdown <= 0f)
                {
                    StartRandomQuest();
                }
                break;

            case QuestState.Active:
                RemainingTime = Mathf.Max(0f, RemainingTime - Time.deltaTime);
                onQuestTimeChanged?.Invoke(RemainingTime, CurrentQuest.timeLimit);

                if (RemainingTime <= 0f)
                {
                    FailCurrentQuest();
                }
                break;
        }
    }

    // 같은 생존 플레이어: 진행 유지
    private void HandlePlayerSpawned(GameObject playerObject)
    {
        if (NetworkGameSession.IsNetworkSessionActive)
        {
            ClearSession();
            return;
        }
        if (playerObject != null && playerObject == player
            && (State != QuestState.Stopped || (playerHealth != null && playerHealth.IsDead)))
        {
            return;
        }

        ClearEventMonster();
        // 플레이어 바인딩
        player = playerObject;
        playerHealth = playerObject != null ? playerObject.GetComponent<PlayerHealth>() : null;
        playerStats = playerObject != null ? playerObject.GetComponent<PlayerStats>() : null;
        playerAugments = playerObject != null ? playerObject.GetComponent<PlayerAugments>() : null;
        CurrentQuest = null;
        CurrentProgress = 0;
        RemainingTime = 0f;
        countdown = 0f;
        State = playerObject != null && IsCombatSessionActive()
            ? QuestState.WaitingForAugment : QuestState.Stopped;
        CurrentReward = default;
    }

    private bool HasAnyAugment()
    {
        // 첫 퀘스트 조건
        bool hasStatAugment = playerStats != null && playerStats.GetAugmentedStatTypes().Count > 0;
        bool hasSkillAugment = playerAugments != null && playerAugments.OwnedSkills.Count > 0;
        return hasStatAugment || hasSkillAugment;
    }

    private void BeginCountdown(float duration)
    {
        countdown = Mathf.Max(0f, duration);
        State = QuestState.Countdown;
    }

    private void StartRandomQuest()
    {
        if (HaltIfSessionInactive()) return;
        // 랜덤 퀘스트 시작
        RemoveInvalidQuests();

        if (quests.Count == 0)
        {
            Debug.LogWarning("QuestManager에 유효한 QuestSO가 없습니다.");
            State = QuestState.Stopped;
            return;
        }

        CurrentQuest = quests[UnityEngine.Random.Range(0, quests.Count)];
        if (CurrentQuest.rewardPool == null
            || !CurrentQuest.rewardPool.TrySelect(
                UnityEngine.Random.value,
                selected => selected.CanApply(player),
                out QuestRewardSO reward))
        {
            Debug.LogWarning($"[Quest] 보상 설정 또는 지급 대상이 유효하지 않습니다: {CurrentQuest.questName}");
            ClearCurrentQuest();
            BeginCountdown(questCooldown);
            return;
        }
        // 시작 시 보상 확정 · 성공 시 재추첨 없음
        CurrentReward = reward;
        if (CurrentQuest.objectiveType == QuestObjectiveType.HuntEventMonster)
        {
            GameObject monster = null;
            if (SpawnManager.Instance == null || CurrentQuest.eventMonsterPrefab == null
                || !SpawnManager.Instance.TrySpawnQuestEnemy(CurrentQuest.eventMonsterPrefab, out monster)
                || !monster.TryGetComponent(out activeEventMonster))
            {
                if (monster != null) Destroy(monster);
                Debug.LogWarning("이벤트 몬스터를 스폰할 수 없어 퀘스트를 다시 대기합니다.");
                ClearCurrentQuest();
                BeginCountdown(5f);
                return;
            }

            if (CurrentQuest.eventMonsterBeaconPrefab != null)
            {
                activeEventBeacon = Instantiate(CurrentQuest.eventMonsterBeaconPrefab, monster.transform);
                activeEventBeacon.transform.localPosition = Vector3.zero;
                activeEventBeacon.transform.localRotation = Quaternion.identity;
            }
        }
        CurrentProgress = 0;
        RemainingTime = CurrentQuest.timeLimit;
        State = QuestState.Active;

        Debug.Log($"[Quest] 시작: {CurrentQuest.questName} ({CurrentQuest.targetAmount}, {CurrentQuest.timeLimit:0.#}초)");
        onQuestStarted?.Invoke(CurrentQuest);
        onQuestProgressChanged?.Invoke(CurrentProgress, CurrentQuest.targetAmount);
        onQuestTimeChanged?.Invoke(RemainingTime, CurrentQuest.timeLimit);
    }

    private void HandleEnemyKilled(EnemyHealth enemy)
    {
        // 처치 진행도
        if (!CanResolveQuest())
        {
            return;
        }

        if (CurrentQuest.objectiveType == QuestObjectiveType.HuntEventMonster && enemy != activeEventMonster)
        {
            return;
        }

        if (CurrentQuest.objectiveType != QuestObjectiveType.KillEnemies
            && CurrentQuest.objectiveType != QuestObjectiveType.HuntEventMonster) return;

        CurrentProgress = Mathf.Min(CurrentProgress + 1, CurrentQuest.targetAmount);
        Debug.Log($"[Quest] 진행: {CurrentQuest.questName} {CurrentProgress}/{CurrentQuest.targetAmount}");
        onQuestProgressChanged?.Invoke(CurrentProgress, CurrentQuest.targetAmount);

        if (CurrentProgress >= CurrentQuest.targetAmount)
        {
            SucceedCurrentQuest();
        }
    }

    private void SucceedCurrentQuest()
    {
        if (!CanResolveQuest()) return;
        QuestSO completedQuest = CurrentQuest;
        QuestRewardSO reward = CurrentReward;
        // 지급 전 상태 종료 · 재진입 중복 지급 차단
        BeginCountdown(questCooldown);
        ClearCurrentQuest();
        QuestRewardResult result = reward.Apply(player);
        if (!result.applied) Debug.LogError($"[Quest] 보상 지급 실패: {completedQuest.questName}");
        Debug.Log($"[Quest] 성공: {completedQuest.questName}, {result.Description}");
        onQuestSucceeded?.Invoke(completedQuest, result);
    }

    private void FailCurrentQuest()
    {
        if (!CanResolveQuest()) return;
        QuestSO failedQuest = CurrentQuest;
        BeginCountdown(questCooldown);
        ClearEventMonster();
        ClearCurrentQuest();
        QuestPenaltyResult penaltyResult = ApplyPenalty(failedQuest);
        LogFailure(failedQuest, penaltyResult);
        onQuestFailed?.Invoke(failedQuest, penaltyResult);
    }

    private bool CanResolveQuest()
    {
        return IsCombatSessionActive()
            && State == QuestState.Active && CurrentQuest != null && player != null
            && (playerHealth == null || !playerHealth.IsDead);
    }

    private QuestPenaltyResult ApplyPenalty(QuestSO quest)
    {
        if (!IsCombatSessionActive()) return default;
        // 설정된 능력만 손실 · 다른 종류로 전가 없음
        QuestPenaltyResult result = new QuestPenaltyResult();

        if (quest == null || AugmentManager.Instance == null)
        {
            return result;
        }

        if (quest.penaltyType == QuestPenaltyType.SkillRemove)
        {
            result.removedSkill = AugmentManager.Instance.RemoveRandomSkill();
            result.remainingSkillStacks = playerAugments != null
                ? playerAugments.GetSkillStackCount(result.removedSkill)
                : 0;
            return result;
        }

        if (quest.penaltyType == QuestPenaltyType.StatReduce)
        {
            result.reducedStat = AugmentManager.Instance.ReduceRandomStat(
                quest.statReduceMin,
                quest.statReduceMax);
        }

        return result;
    }

    private void LogFailure(QuestSO quest, QuestPenaltyResult result)
    {
        if (result.removedSkill != null)
        {
            Debug.Log($"[Quest] 실패: {quest.questName}, 증강 중첩 감소: {result.removedSkill.augmentName}, 남은 중첩: {result.remainingSkillStacks}");
            return;
        }

        if (result.reducedStat.HasValue)
        {
            StatReduceResult stat = result.reducedStat.Value;
            Debug.Log($"[Quest] 실패: {quest.questName}, 스탯 감소: {stat.type} -{stat.amount:0.##}");
            return;
        }

        Debug.Log($"[Quest] 실패: {quest.questName}, 페널티 없음");
    }

    private void ClearCurrentQuest()
    {
        ClearEventBeacon();
        activeEventMonster = null;
        CurrentQuest = null;
        CurrentProgress = 0;
        RemainingTime = 0f;
        CurrentReward = default;
    }

    private void ClearEventMonster()
    {
        ClearEventBeacon();
        if (activeEventMonster != null) Destroy(activeEventMonster.gameObject);
        activeEventMonster = null;
    }

    private void ClearEventBeacon()
    {
        if (activeEventBeacon != null) Destroy(activeEventBeacon);
        activeEventBeacon = null;
    }

    private void RemoveInvalidQuests()
    {
        quests.RemoveAll(quest => quest == null);
    }

    private void OnValidate()
    {
        firstQuestDelay = Mathf.Max(0f, firstQuestDelay);
        questCooldown = Mathf.Max(0f, questCooldown);
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }
}
