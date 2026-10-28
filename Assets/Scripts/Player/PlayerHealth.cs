using System;
using UnityEngine;

// 플레이어 현재 체력, 피격, 회복을 관리
public class PlayerHealth : LivingEntity
{
    [SerializeField] private PlayerStats stats;
    [SerializeField] private int maxHealth = 100;
    [SerializeField] private bool disableControlsOnDeath;

    private bool hasReplicatedHealthSnapshot;
    private int replicatedMaxHealth;

    public int MaxHealth => hasReplicatedHealthSnapshot
        ? replicatedMaxHealth
        : stats != null ? Mathf.RoundToInt(stats.HP) : maxHealth;
    public int CurrentHealth { get; private set; }
    public bool IsDead { get; private set; }
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

    public override void TakeDamage(int damage)
    {
        if (!NetworkAuthority.CanSimulate(gameObject) || IsDead || damage <= 0)
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
    }

    private int Defense => stats != null ? Mathf.RoundToInt(stats.Defense) : 0;

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
}
