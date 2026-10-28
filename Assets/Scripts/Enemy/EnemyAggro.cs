using System.Collections.Generic;
using UnityEngine;

// 피해 기여자와 타겟 선택. 마지막으로 때린 플레이어를 우선한다. 시뮬레이션 인스턴스에서만 기록된다.
public sealed class EnemyAggro : MonoBehaviour
{
    [SerializeField] private float aggroHoldTime = 6f;
    [SerializeField] private float leashRangeMultiplier = 1.5f;
    [SerializeField] private float minimumTargetHoldTime = 0.5f;

    private readonly List<PlayerDamageDealer> contributors = new List<PlayerDamageDealer>(2);
    private PlayerDamageDealer lastAttacker;
    private float lastHitTime = float.NegativeInfinity;
    private PlayerHealth currentTarget;
    private float targetLockedUntil;

    public IReadOnlyList<PlayerDamageDealer> Contributors => contributors;
    public PlayerDamageDealer LastAttacker => lastAttacker;

    public void RecordHit(PlayerDamageDealer source)
    {
        if (source == null) return;
        if (!contributors.Contains(source)) contributors.Add(source);
        lastAttacker = source;
        lastHitTime = Time.time;
    }

    public Transform SelectTarget(Vector3 from, float detectionRange)
    {
        PlayerHealth next = PickTarget(from, detectionRange);
        if (next != currentTarget)
        {
            // 너무 잦은 전환 방지. 현재 타겟이 무효가 되면 즉시 교체.
            bool currentValid = EnemyTargetRegistry.IsAlive(currentTarget) &&
                (currentTarget.transform.position - from).sqrMagnitude <= Sqr(detectionRange * leashRangeMultiplier);
            if (currentValid && Time.time < targetLockedUntil) return currentTarget.transform;
            currentTarget = next;
            targetLockedUntil = Time.time + minimumTargetHoldTime;
        }
        return currentTarget != null ? currentTarget.transform : null;
    }

    private PlayerHealth PickTarget(Vector3 from, float detectionRange)
    {
        if (lastAttacker != null && Time.time - lastHitTime <= aggroHoldTime &&
            lastAttacker.TryGetComponent(out PlayerHealth attacker) && EnemyTargetRegistry.IsAlive(attacker) &&
            (attacker.transform.position - from).sqrMagnitude <= Sqr(detectionRange * leashRangeMultiplier))
        {
            return attacker;
        }
        return EnemyTargetRegistry.FindClosest(from, detectionRange);
    }

    private static float Sqr(float value) => value * value;

    private void OnValidate()
    {
        aggroHoldTime = Mathf.Max(0f, aggroHoldTime);
        leashRangeMultiplier = Mathf.Max(1f, leashRangeMultiplier);
        minimumTargetHoldTime = Mathf.Max(0f, minimumTargetHoldTime);
    }
}
