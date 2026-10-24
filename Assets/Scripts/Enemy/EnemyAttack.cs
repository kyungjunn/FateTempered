using System.Collections;
using UnityEngine;

// 적의 공격 범위, 쿨다운, 실제 피해 적용 타이밍을 관리
public class EnemyAttack : MonoBehaviour
{
    [SerializeField] private EnemyStats stats;
    [SerializeField] private int attackDamage = 10;
    [SerializeField] private float attackRange = 1.8f;
    [SerializeField] private float attackCooldown = 1.2f;
    [SerializeField] private float fallbackHitDelay = 0.45f;
    [SerializeField] private bool useFallbackHitDelay = true;

    private EnemyAnimator enemyAnimator;
    private Transform pendingTarget;
    private Coroutine fallbackHitRoutine;
    private float nextAttackTime;
    private bool pendingDamageApplied;

    public virtual float AttackRange => stats != null ? stats.AttackRange : attackRange;
    public bool IsAttackCycleActive => Time.time < nextAttackTime;
    public virtual bool LockFacingDuringAttack => false;
    public virtual bool ShouldFaceTarget => true;
    protected EnemyStats Stats => stats;
    protected EnemyAnimator EnemyAnimator => enemyAnimator;
    protected bool IsAttackReady => Time.time >= nextAttackTime;
    protected virtual int CurrentAttackDamage => stats != null ? stats.AttackDamage : attackDamage;
    protected virtual float CurrentAttackCooldown => stats != null ? stats.AttackCooldown : attackCooldown;
    protected virtual float CurrentFallbackHitDelay => fallbackHitDelay;

    protected virtual void Awake()
    {
        enemyAnimator = GetComponent<EnemyAnimator>();
    }

    // 공격 가능 상태일 때 공격 애니메이션을 재생하고 피해 적용을 예약
    public virtual bool TryAttack(Transform target)
    {
        if (target == null || Time.time < nextAttackTime)
        {
            return false;
        }

        if (!IsTargetInRange(target))
        {
            return false;
        }

        PrepareAttack(target);
        nextAttackTime = Time.time + CurrentAttackCooldown;
        pendingTarget = target;
        pendingDamageApplied = false;

        if (fallbackHitRoutine != null)
        {
            StopCoroutine(fallbackHitRoutine);
        }

        PlayAttackAnimation();

        if (useFallbackHitDelay)
        {
            fallbackHitRoutine = StartCoroutine(ApplyDamageAfterFallbackDelay(target));
        }

        return true;
    }

    // 공격 애니메이션 이벤트에서 호출되는 실제 타격 지점
    public void ApplyAttackDamageFromAnimationEvent()
    {
        ApplyPendingAttackDamage();
    }

    // 다른 공격 클립에서 OnAttackHit 이름을 쓰는 경우를 위한 별칭
    public void OnAttackHit()
    {
        ApplyPendingAttackDamage();
    }

    // 애니메이션 이벤트가 없는 경우에도 일정 시간 뒤 피해가 들어가도록 하는 보조 처리
    private IEnumerator ApplyDamageAfterFallbackDelay(Transform expectedTarget)
    {
        yield return new WaitForSeconds(CurrentFallbackHitDelay);

        if (pendingTarget == expectedTarget)
        {
            ApplyPendingAttackDamage();
        }

        fallbackHitRoutine = null;
    }

    private void ApplyPendingAttackDamage()
    {
        if (pendingDamageApplied || pendingTarget == null)
        {
            return;
        }

        if (TryGetComponent(out EnemyHealth enemyHealth) && enemyHealth.IsDead)
        {
            ClearPendingAttack();
            return;
        }

        if (!IsTargetInRangeForDamage(pendingTarget))
        {
            ClearPendingAttack();
            return;
        }

        DealDamage(pendingTarget);

        ClearPendingAttack();
    }

    private void ClearPendingAttack()
    {
        pendingDamageApplied = true;
        pendingTarget = null;
    }

    // 보스처럼 공격 종류를 선택해야 하는 파생 클래스에서 공격 직전 상태를 준비
    protected virtual void PrepareAttack(Transform target)
    {
    }

    protected virtual void PlayAttackAnimation()
    {
        enemyAnimator?.PlayAttack();
    }

    // 플레이어 루트 또는 자식 오브젝트에 붙은 IDamageable을 찾아 피해 적용
    protected virtual void DealDamage(Transform target)
    {
        IDamageable damageable = FindDamageable(target);
        damageable?.TakeDamage(CurrentAttackDamage);
    }

    protected bool IsTargetInRange(Transform target)
    {
        float currentAttackRange = AttackRange;
        return (target.position - transform.position).sqrMagnitude <= currentAttackRange * currentAttackRange;
    }

    // 범위 공격처럼 시작 거리와 실제 판정 거리가 다른 적이 타격 검사를 확장할 수 있음
    protected virtual bool IsTargetInRangeForDamage(Transform target)
    {
        return IsTargetInRange(target);
    }

    protected IDamageable FindDamageable(Transform target)
    {
        IDamageable damageable = target.GetComponentInParent<IDamageable>();

        if (damageable != null)
        {
            return damageable;
        }

        return target.GetComponentInChildren<IDamageable>();
    }

    protected virtual void OnValidate()
    {
        attackDamage = Mathf.Max(1, attackDamage);
        attackRange = Mathf.Max(0.1f, attackRange);
        attackCooldown = Mathf.Max(0.1f, attackCooldown);
        fallbackHitDelay = Mathf.Max(0f, fallbackHitDelay);
    }
}
