using System;
using UnityEngine;

// 플레이어 현재 체력, 피격, 회복을 관리
public class PlayerHealth : LivingEntity, MatchFlow.IMember
{
    [SerializeField] private PlayerStats stats;
    [SerializeField] private int maxHealth = 100;
    [SerializeField] private bool disableControlsOnDeath;

    // 복제본 최대 체력: 로컬 능력치 대신 서버 스냅샷 우선
    private bool hasReplicatedHealthSnapshot;
    private int replicatedMaxHealth;
    private float invulnerableUntil = float.NegativeInfinity;
    private float upperBodyLayerWeight = 1f;

    public int MaxHealth => hasReplicatedHealthSnapshot
        ? replicatedMaxHealth
        : stats != null ? Mathf.RoundToInt(stats.HP) : maxHealth;
    public int CurrentHealth { get; private set; }
    public bool IsDead { get; private set; }
    // 부활 직후 무적. 피해 판정은 서버/오프라인만 하므로 클라이언트에는 복제하지 않는다.
    public bool IsInvulnerable => Time.time < invulnerableUntil;
    public Action onHealthChanged;

    private PlayerStateManager stateManager;
    private Animator animator;
    private Rigidbody rb;

    private void Awake()
    {
        if (stats == null)
        {
            stats = GetComponent<PlayerStats>();
        }

        stateManager = GetComponent<PlayerStateManager>();
        if (stateManager == null)
        {
            // 같은 오브젝트에 없으면 씬 전체에서 찾음
            stateManager = FindFirstObjectByType<PlayerStateManager>();
        }

        animator = GetComponentInChildren<Animator>();
        rb = GetComponent<Rigidbody>();
        if (animator != null && animator.layerCount > 1)
        {
            upperBodyLayerWeight = animator.GetLayerWeight(1);
        }

        CurrentHealth = MaxHealth;
    }

    private void OnEnable()
    {
        EnemyTargetRegistry.Register(this);
        if (stats != null)
        {
            stats.onStatsChanged += HandleStatsChanged;
        }
    }

    private void OnDisable()
    {
        EnemyTargetRegistry.Unregister(this);
        if (stats != null)
        {
            stats.onStatsChanged -= HandleStatsChanged;
        }
    }

    // 서버·오프라인 전용 피해 확정 및 사망 판정
    public override void TakeDamage(int damage)
    {
        if (!NetworkAuthority.CanSimulate(gameObject) || IsDead || IsInvulnerable || damage <= 0)
        {
            return;
        }

        // 방어력을 적용하되 최소 1 피해는 보장
        int finalDamage = Mathf.Max(1, damage - Defense);
        CurrentHealth = Mathf.Max(CurrentHealth - finalDamage, 0);
        onHealthChanged?.Invoke();

        if (CurrentHealth == 0)
        {
            Die();
        }
    }

    // 서버·오프라인 전용 회복량 확정
    public int Heal(int amount)
    {
        if (!NetworkAuthority.CanSimulate(gameObject) || IsDead || amount <= 0 || CurrentHealth >= MaxHealth)
        {
            return 0;
        }

        int previousHealth = CurrentHealth;
        CurrentHealth = Mathf.Min(CurrentHealth + amount, MaxHealth);
        onHealthChanged?.Invoke();
        return CurrentHealth - previousHealth;
    }

    // 클라이언트 복제본 체력 적용 및 사망·부활 연출 전환
    public void ApplyReplicatedHealth(int currentHealth, int maxHealth)
    {
        if (NetworkAuthority.CanSimulate(gameObject))
        {
            return;
        }

        bool wasDead = IsDead;
        hasReplicatedHealthSnapshot = true;
        replicatedMaxHealth = Mathf.Max(0, maxHealth);
        CurrentHealth = Mathf.Clamp(currentHealth, 0, replicatedMaxHealth);
        IsDead = CurrentHealth == 0;
        onHealthChanged?.Invoke();

        if (!wasDead && IsDead)
        {
            ApplyDeathPresentation();
        }
        else if (wasDead && !IsDead)
        {
            ApplyRevivePresentation();
        }
    }

    // 쓰러진 자리에서 부활 (서버/오프라인만). 체력은 최대 체력 비율, 최소 1.
    public bool Revive(float healthRatio, float invulnerableSeconds)
    {
        if (!NetworkAuthority.CanSimulate(gameObject) || !IsDead)
        {
            return false;
        }

        IsDead = false;
        CurrentHealth = Mathf.Clamp(Mathf.RoundToInt(MaxHealth * Mathf.Clamp01(healthRatio)), 1, MaxHealth);
        invulnerableUntil = Time.time + Mathf.Max(0f, invulnerableSeconds);
        ApplyRevivePresentation();
        onHealthChanged?.Invoke();
        return true;
    }

    private int Defense => stats != null ? Mathf.RoundToInt(stats.Defense) : 0;

    // 서버·오프라인 능력치 변경에 따른 체력 보정
    private void HandleStatsChanged()
    {
        if (!NetworkAuthority.CanSimulate(gameObject))
        {
            return;
        }

        int newMaxHealth = MaxHealth;
        CurrentHealth = Mathf.Clamp(CurrentHealth, 0, newMaxHealth);
        onHealthChanged?.Invoke();

        if (CurrentHealth == 0)
        {
            Die();
        }
    }

    private void Die()
    {
        if (IsDead)
        {
            return;
        }

        IsDead = true;

        Debug.Log($"[Health] Die 호출, stateManager 있음: {stateManager != null}");
        ApplyDeathPresentation();
    }

    // 권한 객체·복제본 공통 사망 연출 및 조작 차단
    private void ApplyDeathPresentation()
    {
        stateManager?.ChangeState(PlayerState.Dead);

        if (rb != null && !rb.isKinematic)
        {
            rb.linearVelocity = new Vector3(0f, rb.linearVelocity.y, 0f);
        }

        if (animator != null)
        {
            if (animator.layerCount > 1)
            {
                animator.SetLayerWeight(1, 0f);
            }

            animator.SetTrigger("Die");
        }

        if (!disableControlsOnDeath)
        {
            return;
        }

        if (TryGetComponent(out PlayerController controller))
        {
            controller.enabled = false;
        }

        if (TryGetComponent(out PlayerMovement movement))
        {
            movement.enabled = false;
        }
    }

    // 권한 객체·복제본 공통 부활 연출 및 조작 복원
    private void ApplyRevivePresentation()
    {
        stateManager?.Revive();

        if (animator != null)
        {
            // Die 상태는 종단 상태라 전이로 빠져나올 수 없다. 기본 상태로 되돌린다.
            animator.ResetTrigger("Die");
            animator.Rebind();
            animator.Update(0f);
            if (animator.layerCount > 1)
            {
                animator.SetLayerWeight(1, upperBodyLayerWeight);
            }
        }

        if (!disableControlsOnDeath)
        {
            return;
        }

        if (TryGetComponent(out PlayerController controller))
        {
            controller.enabled = true;
        }

        if (TryGetComponent(out PlayerMovement movement))
        {
            movement.enabled = true;
        }
    }
}
