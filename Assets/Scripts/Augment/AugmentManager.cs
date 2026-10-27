using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

// 적용 결과
public enum AugmentApplyStatus
{
    Applied,
    Stacked,
    Failed,
    Stale,
    AlreadyMax,
    Invalid
}

public struct StatReduceResult
{
    // 감소 스탯
    public AugmentType type;
    // 감소 수치
    public float amount;
}

// 증강 추첨·적용. 세션 종료 없음.
public class AugmentManager : MonoBehaviour
{
    public static AugmentManager Instance;

    // 플레이어 구성 요소
    private PlayerStats playerStats;
    private PlayerAugments playerAugments;
    private readonly HashSet<AugmentSO> failedDefinitions = new HashSet<AugmentSO>(); // 로드 실패 카드
    private int boundSessionId; // 연결 세션

    [Header("Settings")]
    // 선택지 개수
    public int optionCount = 3;

    private void Awake()
    {
        // 중복 거부
        if (Instance != null && Instance != this)
        {
            enabled = false;
            Destroy(gameObject);
            return;
        }
        Instance = this;
    }

    private void OnEnable()
    {
        if (Instance != this) return;
        GameManager.OnLocalPlayerChanged += HandlePlayerSpawned;
        // 이벤트 + 현재 스냅샷
        HandlePlayerSpawned(GameManager.Instance != null ? GameManager.Instance.CurrentPlayer : null);
    }

    private void OnDisable()
    {
        GameManager.OnLocalPlayerChanged -= HandlePlayerSpawned;
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    // 세션 로컬 상태만 해제
    public void ClearSession()
    {
        playerStats = null;
        playerAugments = null;
        failedDefinitions.Clear();
        boundSessionId = 0;
    }

    // 플레이어 스냅샷 연결
    private void HandlePlayerSpawned(GameObject playerObject)
    {
        if (NetworkGameSession.IsNetworkSessionActive)
        {
            ClearSession();
            return;
        }
        int sessionId = GameManager.Instance != null ? GameManager.Instance.SessionId : 0;
        if (boundSessionId != sessionId)
        {
            failedDefinitions.Clear();
            boundSessionId = sessionId;
        }
        playerStats = playerObject != null ? playerObject.GetComponent<PlayerStats>() : null;
        playerAugments = playerObject != null ? playerObject.GetComponent<PlayerAugments>() : null;
    }

    // 증강 선택지 추첨
    public List<AugmentSO> GetRandomAugments()
    {
        return GetRandomAugments(null);
    }

    // 기존 선택지 유지 추첨
    public List<AugmentSO> GetRandomAugments(IReadOnlyCollection<AugmentSO> keepVisible)
    {
        List<AugmentSO> result = new List<AugmentSO>();
        if (NetworkGameSession.IsNetworkSessionActive) return result;
        AugmentResourceLoader host = AugmentResourceLoader.Instance;
        GameManager session = GameManager.Instance;
        if (host == null || session == null || !session.IsActiveSession || playerStats == null)
        {
            return result;
        }

        List<AugmentSO> pool = new List<AugmentSO>();
        foreach (AugmentSO augment in host.Catalog)
        {
            if (augment == null || failedDefinitions.Contains(augment) ||
                !augment.IsAvailableFor(playerStats.gameObject)) continue;
            if (augment is SkillAugmentSO skill && playerAugments != null && playerAugments.IsAtMaxStacks(skill))
                continue;
            pool.Add(augment);
        }
        if (keepVisible != null)
        {
            foreach (AugmentSO visible in keepVisible)
            {
                if (visible != null && !result.Contains(visible) && pool.Contains(visible))
                {
                    result.Add(visible);
                    pool.Remove(visible);
                }
            }
        }

        while (result.Count < optionCount && pool.Count > 0)
        {
            AugmentSO selected = GetWeightedRandom(pool);
            result.Add(selected);
            pool.Remove(selected);
        }

        return result;
    }

    // 가중치 추첨
    private AugmentSO GetWeightedRandom(List<AugmentSO> pool)
    {
        int totalWeight = pool.Sum(x => x.weight);
        if (totalWeight <= 0)
        {
            return pool[0];
        }

        int random = Random.Range(0, totalWeight);
        int current = 0;

        foreach (AugmentSO augment in pool)
        {
            current += augment.weight;
            if (random < current)
            {
                return augment;
            }
        }

        return pool[0];
    }

    // 증강 적용
    public IEnumerator ApplyAugmentRoutine(AugmentSO augment, int sessionId, int ticketId, System.Action<AugmentApplyStatus, string> completed)
    {
        if (NetworkGameSession.IsNetworkSessionActive)
        {
            completed?.Invoke(AugmentApplyStatus.Invalid, "증강은 네트워크 전투에서 아직 지원하지 않습니다.");
            yield break;
        }
        // 세션·플레이어 불일치 → Stale
        AugmentResourceLoader host = AugmentResourceLoader.Instance;
        GameManager session = GameManager.Instance;
        PlayerAugments targetAugments = playerAugments;
        if (host == null || session == null || !session.BelongsToSession(sessionId) ||
            playerStats == null || session.CurrentPlayer != playerStats.gameObject)
        {
            completed?.Invoke(AugmentApplyStatus.Stale, null);
            yield break;
        }

        if (augment == null || !augment.IsAvailableFor(playerStats != null ? playerStats.gameObject : null))
        {
            completed?.Invoke(AugmentApplyStatus.Invalid, "현재 캐릭터에게 적용할 수 없는 증강입니다.");
            yield break;
        }

        switch (augment)
        {
            // 스탯 증강 적용
            case StatAugmentSO stat:
                if (playerStats == null)
                {
                    completed?.Invoke(AugmentApplyStatus.Failed, "PlayerStats 바인딩 안됨.");
                    yield break;
                }

                playerStats.ApplyStat(stat.type, stat.value);
                completed?.Invoke(AugmentApplyStatus.Applied, null);
                yield break;

            // 스킬 증강 적용
            case SkillAugmentSO skill:
                if (playerAugments == null)
                {
                    completed?.Invoke(AugmentApplyStatus.Failed, "PlayerAugments 바인딩 안됨.");
                    yield break;
                }

                // 최대 중첩 상태라면 => AlreadyMax
                if (playerAugments.IsAtMaxStacks(skill))
                {
                    completed?.Invoke(AugmentApplyStatus.AlreadyMax, null);
                    yield break;
                }

                // 이미 보유중인 스킬이라면 => 스택 증가
                if (playerAugments.HasSkill(skill))
                {
                    SkillAcquireStatus stacked = playerAugments.TryAcquireLoadedSkill(skill, null);
                    completed?.Invoke(stacked == SkillAcquireStatus.Stacked ? AugmentApplyStatus.Stacked : AugmentApplyStatus.AlreadyMax, null);
                    yield break;
                }

                string path = skill.SkillResourcePath;
                GameObject prefab = null;
                string error = null;
                // 처음 획득하는 스킬이면 프리팹 비동기 로딩
                yield return host.LoadSkillPrefab(path, (loaded, loadError) =>
                {
                    prefab = loaded;
                    error = loadError;
                });

                // 로딩 후 세션과 플레이어 다시 확인
                if (session == null || !session.BelongsToSession(sessionId) ||
                    targetAugments == null || playerAugments != targetAugments ||
                    session.CurrentPlayer != targetAugments.gameObject)
                {
                    completed?.Invoke(AugmentApplyStatus.Stale, null);
                    yield break;
                }

                // 로드 실패시 증강 목록에서 제거
                if (prefab == null)
                {
                    failedDefinitions.Add(skill);
                    completed?.Invoke(AugmentApplyStatus.Failed, error ?? $"스킬 로딩 실패: {skill.augmentName}");
                    yield break;
                }

                // 정상적 로드됐으면 플레이어 자식으로 생성
                SkillAcquireStatus status = playerAugments.TryAcquireLoadedSkill(skill, prefab);
                if (status == SkillAcquireStatus.Invalid)
                {
                    failedDefinitions.Add(skill);
                    completed?.Invoke(AugmentApplyStatus.Failed, $"스킬 적용 실패: {skill.augmentName}");
                    yield break;
                }

                if (status == SkillAcquireStatus.AlreadyMax)
                {
                    completed?.Invoke(AugmentApplyStatus.AlreadyMax, null);
                    yield break;
                }

                // AugmentApplyStatus로 결과 반환
                completed?.Invoke(status == SkillAcquireStatus.Stacked ? AugmentApplyStatus.Stacked : AugmentApplyStatus.Applied, null);
                yield break;

            default:
                completed?.Invoke(AugmentApplyStatus.Invalid, "지원하지 않는 증강입니다.");
                yield break;
        }
    }

    // 무작위 스킬 제거
    public SkillAugmentSO RemoveRandomSkill()
    {
        if (NetworkGameSession.IsNetworkSessionActive || playerAugments == null)
        {
            return null;
        }

        return playerAugments.TryRemoveRandomSkill(out SkillAugmentSO removed) ? removed : null;
    }

    // 무작위 스탯 감소
    public StatReduceResult? ReduceRandomStat(float minRatio, float maxRatio)
    {
        if (NetworkGameSession.IsNetworkSessionActive || playerStats == null)
        {
            return null;
        }

        IReadOnlyList<AugmentType> candidates = playerStats.GetAugmentedStatTypes();
        if (candidates.Count == 0)
        {
            return null;
        }

        AugmentType type = candidates[Random.Range(0, candidates.Count)];
        float amount = playerStats.ReduceStatByRatio(type, Random.Range(minRatio, maxRatio));
        return new StatReduceResult { type = type, amount = amount };
    }

    [ContextMenu("Test/Remove Random Skill")]
    private void TestRemoveRandomSkill()
    {
        SkillAugmentSO removed = RemoveRandomSkill();
        Debug.Log(removed != null ? $"[Penalty] 스킬 박탈: {removed.augmentName}" : "[Penalty] 박탈할 스킬 없음");
    }

    [ContextMenu("Test/Reduce Random Stat 25~50%")]
    private void TestReduceRandomStat()
    {
        StatReduceResult? result = ReduceRandomStat(0.25f, 0.5f);
        Debug.Log(result.HasValue ? $"[Penalty] 스탯 감소: {result.Value.type} -{result.Value.amount}" : "[Penalty] 깎을 스탯 없음");
    }
}
