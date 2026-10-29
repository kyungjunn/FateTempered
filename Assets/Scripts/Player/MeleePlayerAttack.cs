using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// 전사 평타: 투사체/검기 대신 실제 검 끝에 붙인 TrailRenderer를 짧게 켠다.
// 모션과 맞는 시점에 전방 타격 판정을 처리하고 실제 접촉 위치에 피격 연출을 요청한다.
[RequireComponent(typeof(PlayerDamageDealer))]
public class MeleePlayerAttack : MonoBehaviour, IPlayerBasicAttack
{
    [Header("Attack Area")]
    [SerializeField] private PlayerStats stats;
    [SerializeField] private Transform attackOrigin;
    [SerializeField, Min(0.1f)] private float attackRange = 2.2f;
    [SerializeField, Min(0.1f)] private float nearWidth = 1f;
    [SerializeField, Min(0.1f)] private float farWidth = 3f;
    [SerializeField, Range(1f, 360f)] private float attackAngle = 90f;
    [SerializeField] private LayerMask enemyLayers = ~0;
    [SerializeField, Min(0)] private int maxTargets;
    [SerializeField, Min(0.1f)] private float areaDamageMultiplier = 1f;

    [Header("Timing (AttackSpeed = 1)")]
    [SerializeField, Min(0.1f)] private float attackCooldown = 0.55f;
    [Tooltip("평타 입력 후 실제 타격까지 시간. 1차 추천: 0.19 ~ 0.23초")]
    [SerializeField, Min(0f)] private float hitDelay = 0.20f;

    [Header("Sword-only Trail (NO projectile)")]
    [Tooltip("검 끝의 자식 오브젝트에 붙인 TrailRenderer를 연결한다.")]
    [SerializeField] private TrailRenderer bladeTrail;
    [Tooltip("모션 시작 후 TrailRenderer를 켜는 시간")]
    [SerializeField, Min(0f)] private float trailStartDelay = 0.08f;
    [Tooltip("모션 시작 후 TrailRenderer를 끄는 시간")]
    [SerializeField, Min(0f)] private float trailEndDelay = 0.27f;

    [Header("Debug Gizmos")]
    [SerializeField] private Color attackGizmoColor = new Color(1f, 0.2f, 0.1f, 0.75f);
    [SerializeField, Min(4)] private int attackGizmoSegments = 24;

    private readonly List<EnemyHealth> attackTargets = new List<EnemyHealth>();
    private readonly HashSet<EnemyHealth> uniqueTargets = new HashSet<EnemyHealth>();
    private PlayerDamageDealer damageDealer;
    private PlayerStateManager stateManager;
    private SwordCombatFeedback swordFeedback;
    private Coroutine attackRoutine;
    private float nextAttackTime;

    private float AttackDamage => stats != null ? stats.AttackDamage : 10f;
    private float AttackSpeed => stats != null ? Mathf.Max(0.1f, stats.AttackSpeed) : 1f;

    
    private void Awake()
    {
        if (stats == null)
            stats = GetComponent<PlayerStats>();

        damageDealer = GetComponent<PlayerDamageDealer>();
        stateManager = GetComponent<PlayerStateManager>();
        swordFeedback = GetComponent<SwordCombatFeedback>();

        // BladeTrailPoint 자동 탐색
        if (bladeTrail == null)
        {
            TrailRenderer[] trails =
                GetComponentsInChildren<TrailRenderer>(true);

            foreach (TrailRenderer trail in trails)
            {
                if (trail.gameObject.name == "BladeTrailPoint")
                {
                    bladeTrail = trail;
                    break;
                }
            }
        }

        DisableTrail(true);
    }


    public bool TryAttack()
    {
        if (NetworkPlayerCombat.TryRouteAttack(this))
        {
            return false;
        }

        if (damageDealer == null) damageDealer = GetComponent<PlayerDamageDealer>();
        if (damageDealer == null || !isActiveAndEnabled || Time.time < nextAttackTime ||
            (stateManager != null && stateManager.CurrentState == PlayerState.Dead))
            return false;

        float inverseSpeed = 1f / AttackSpeed;
        nextAttackTime = Time.time + attackCooldown * inverseSpeed;

        // 공격속도가 매우 빠른 빌드에서 이전 잔상이 남지 않도록 초기화.
        StartAttackSequence(inverseSpeed, true);
        return true;
    }

    // 승인된 공격 RPC의 비서버 복제본 전용.
    // 칼날 궤적 타이밍만 재현, 명중 검사·피해 없음.
    public void PlayAcceptedAttackVisual()
    {
        // 서버·오프라인은 실제 공격 경로 사용.
        if (!isActiveAndEnabled || NetworkAuthority.CanSimulate(gameObject))
        {
            return;
        }

        StartAttackSequence(1f / AttackSpeed, false);
    }

    private void StartAttackSequence(float inverseSpeed, bool applyDamage)
    {
        if (attackRoutine != null)
        {
            StopCoroutine(attackRoutine);
            attackRoutine = null;
        }
        DisableTrail(true);

        attackRoutine = StartCoroutine(AttackSequence(inverseSpeed, applyDamage));
    }

    private IEnumerator AttackSequence(float inverseSpeed, bool applyDamage)
    {
        float trailStart = trailStartDelay * inverseSpeed;
        float trailEnd = Mathf.Max(trailStart, trailEndDelay * inverseSpeed);
        float hitTime = hitDelay * inverseSpeed;
        float totalTime = Mathf.Max(trailEnd, hitTime);
        float elapsed = 0f;
        bool trailStarted = false;
        bool trailStopped = false;
        bool damageApplied = !applyDamage;

        // 최소 1프레임 대기: Animator가 Attack Trigger를 수신하도록 한다.
        yield return null;

        while (elapsed < totalTime || !damageApplied || !trailStopped)
        {
            if (!isActiveAndEnabled ||
                (stateManager != null && stateManager.CurrentState == PlayerState.Dead))
                break;

            if (Time.timeScale <= 0f ||
                (GameManager.Instance != null && GameManager.Instance.IsPaused))
            {
                yield return null;
                continue;
            }

            elapsed += Time.deltaTime;

            if (!trailStarted && elapsed >= trailStart)
            {
                trailStarted = true;
                EnableTrail();
            }

            if (!damageApplied && elapsed >= hitTime)
            {
                damageApplied = true;
                if (applyDamage)
                {
                    ApplyDamage();
                }
            }

            if (!trailStopped && elapsed >= trailEnd)
            {
                trailStopped = true;
                DisableTrail(false); // 남은 궤적은 Trail Time만큼 자연스럽게 사라진다.
            }

            yield return null;
        }

        DisableTrail(false);
        attackRoutine = null;
    }

    private void ApplyDamage()
    {
        // 서버·오프라인만 근접 피해 확정.
        if (!NetworkAuthority.CanSimulate(gameObject)) return;

        NetworkPlayerCombat networkCombat = GetComponent<NetworkPlayerCombat>();
        int damage = Mathf.Max(1, Mathf.RoundToInt(AttackDamage * areaDamageMultiplier));
        int targetCount = FindAttackTargets(attackTargets);

        for (int i = 0; i < targetCount; i++)
        {
            EnemyHealth enemy = attackTargets[i];
            if (enemy == null || enemy.IsDead) continue;

            // 적이 죽으면서 Collider가 비활성화되기 전에 접촉 위치를 계산한다.
            Vector3 contactPoint = GetContactPoint(enemy);
            int dealt = damageDealer.DealDamage(
                enemy, damage, PlayerDamageType.BasicAttack, transform.position);

            if (dealt > 0 && swordFeedback != null)
            {
                if (networkCombat != null && networkCombat.IsSpawned)
                    networkCombat.PresentMeleeHit(contactPoint);
                else
                    swordFeedback.PlayMeleeImpact(contactPoint);
            }
        }
    }

    private Vector3 GetContactPoint(EnemyHealth enemy)
    {
        Vector3 attackProbe = transform.position + Vector3.up * 0.9f;
        float nearestSq = float.PositiveInfinity;
        Vector3 nearest = enemy.transform.position + Vector3.up * 0.8f;

        foreach (Collider target in enemy.GetComponentsInChildren<Collider>())
        {
            if (target == null || !target.enabled || target.isTrigger) continue;
            Vector3 point = target.ClosestPoint(attackProbe);
            float d = (point - attackProbe).sqrMagnitude;
            if (d < nearestSq)
            {
                nearestSq = d;
                nearest = point;
            }
        }

        if (!float.IsPositiveInfinity(nearestSq)) return nearest;
        Renderer visual = enemy.GetComponentInChildren<Renderer>();
        return visual != null ? visual.bounds.center : nearest;
    }

    private void EnableTrail()
    {
        if (bladeTrail == null) return;
        bladeTrail.Clear();
        bladeTrail.emitting = true;
    }

    private void DisableTrail(bool clear)
    {
        if (bladeTrail == null) return;
        bladeTrail.emitting = false;
        if (clear) bladeTrail.Clear();
    }

    private void OnDisable()
    {
        if (attackRoutine != null)
        {
            StopCoroutine(attackRoutine);
            attackRoutine = null;
        }
        DisableTrail(true);
    }

    private int FindAttackTargets(List<EnemyHealth> results)
    {
        results.Clear();
        uniqueTargets.Clear();
        Transform origin = attackOrigin != null ? attackOrigin : transform;
        Collider[] hits = Physics.OverlapSphere(
            origin.position, attackRange, enemyLayers, QueryTriggerInteraction.Ignore);

        foreach (Collider hit in hits)
        {
            EnemyHealth enemy = hit.GetComponentInParent<EnemyHealth>();
            if (enemy == null || enemy.IsDead || !uniqueTargets.Add(enemy)) continue;

            Vector3 local = origin.InverseTransformPoint(enemy.transform.position);
            float forwardDistance = local.z;
            if (forwardDistance < 0f || forwardDistance > attackRange) continue;

            float width = Mathf.Lerp(nearWidth, farWidth, forwardDistance / attackRange);
            if (Mathf.Abs(local.x) > width * 0.5f) continue;

            results.Add(enemy);
            if (maxTargets > 0 && results.Count >= maxTargets) break;
        }
        return results.Count;
    }

    private void OnValidate()
    {
        attackRange = Mathf.Max(0.1f, attackRange);
        nearWidth = Mathf.Max(0.1f, nearWidth);
        farWidth = Mathf.Max(0.1f, farWidth);
        attackAngle = Mathf.Clamp(attackAngle, 1f, 360f);
        attackCooldown = Mathf.Max(0.1f, attackCooldown);
        hitDelay = Mathf.Max(0f, hitDelay);
        trailStartDelay = Mathf.Max(0f, trailStartDelay);
        trailEndDelay = Mathf.Max(trailStartDelay, trailEndDelay);
        maxTargets = Mathf.Max(0, maxTargets);
        areaDamageMultiplier = Mathf.Max(0.1f, areaDamageMultiplier);
        attackGizmoSegments = Mathf.Max(4, attackGizmoSegments);
    }

    private void OnDrawGizmosSelected()
    {
        Transform origin = attackOrigin != null ? attackOrigin : transform;
        if (origin == null) return;

        Vector3 forward = origin.forward;
        forward.y = 0f;
        if (forward.sqrMagnitude < 0.001f) forward = transform.forward;
        forward.y = 0f;
        forward.Normalize();
        Vector3 pos = origin.position;
        Color oldColor = Gizmos.color;
        Gizmos.color = attackGizmoColor;
        Vector3 left = Quaternion.Euler(0f, -attackAngle * 0.5f, 0f) * forward;
        Vector3 right = Quaternion.Euler(0f, attackAngle * 0.5f, 0f) * forward;
        Gizmos.DrawLine(pos, pos + left * attackRange);
        Gizmos.DrawLine(pos, pos + right * attackRange);
        Vector3 prev = pos + left * attackRange;
        for (int i = 1; i <= attackGizmoSegments; i++)
        {
            float angle = -attackAngle * 0.5f + attackAngle * i / attackGizmoSegments;
            Vector3 next = pos + (Quaternion.Euler(0f, angle, 0f) * forward) * attackRange;
            Gizmos.DrawLine(prev, next);
            prev = next;
        }
        Gizmos.color = oldColor;
    }
}
