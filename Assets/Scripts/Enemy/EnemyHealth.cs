using System;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;

// 적의 체력, 사망 처리, 경험치 지급을 관리
public class EnemyHealth : MonoBehaviour, IDamageable
{
    // 퀘스트 처치 알림
    public static event Action<EnemyHealth> OnEnemyKilled;

    [SerializeField] private EnemyStats stats;
    [SerializeField] private int maxHealth = 20;
    [SerializeField] private int expReward = 1000;
    [SerializeField] private float destroyDelay = 2f;

    private bool hasReplicatedHealthSnapshot;
    private int replicatedMaxHealth;

    public int MaxHealth => hasReplicatedHealthSnapshot
        ? replicatedMaxHealth
        : stats != null ? stats.MaxHealth : maxHealth;
    public int ExpReward => stats != null ? stats.ExpReward : expReward;
    public int CurrentHealth { get; private set; }
    public bool IsDead { get; private set; }
    public float HealthPercent => MaxHealth > 0 ? (float)CurrentHealth / MaxHealth : 0f;

    public event Action<int, int> OnHealthChanged;

    private EnemyAnimator enemyAnimator;
    private EnemyHitFeedback hitFeedback;
    private bool expAwarded;
    private bool deathHandled;
    private PlayerDamageDealer lastDamageSource;

    public void RecordDamageSource(PlayerDamageDealer source)
    {
        if (NetworkAuthority.CanSimulate(gameObject)) lastDamageSource = source;
    }

    private void Awake()
    {
        enemyAnimator = GetComponent<EnemyAnimator>();
        hitFeedback = GetComponent<EnemyHitFeedback>();

        if (hitFeedback == null)
        {
            hitFeedback = gameObject.AddComponent<EnemyHitFeedback>();
        }

        CurrentHealth = MaxHealth;
    }

    // 플레이어 공격 등 외부 피해를 받을 때 호출
    public void TakeDamage(int damage)
    {
        if (!NetworkAuthority.CanSimulate(gameObject) || IsDead || damage <= 0)
        {
            return;
        }

        CurrentHealth = Mathf.Max(CurrentHealth - damage, 0);
        OnHealthChanged?.Invoke(CurrentHealth, MaxHealth);
        hitFeedback?.Play();

        if (AudioManager.Instance != null)
        {
            AudioManager.Instance.PlayEnemyHit();
        }

        if (CurrentHealth == 0)
        {
            Die();
            return;
        }

        enemyAnimator?.PlayHit();
    }

    public void ApplyReplicatedHealth(int currentHealth, int maxHealth)
    {
        if (NetworkAuthority.CanSimulate(gameObject))
        {
            return;
        }

        bool wasDead = IsDead;
        bool hit = hasReplicatedHealthSnapshot && currentHealth < CurrentHealth;
        hasReplicatedHealthSnapshot = true;
        replicatedMaxHealth = Mathf.Max(0, maxHealth);
        CurrentHealth = Mathf.Clamp(currentHealth, 0, replicatedMaxHealth);
        IsDead = CurrentHealth == 0;
        OnHealthChanged?.Invoke(CurrentHealth, MaxHealth);

        if (!wasDead && IsDead)
        {
            ApplyDeathPresentation();
        }
        else if (hit && !IsDead)
        {
            hitFeedback?.Play();
            enemyAnimator?.PlayHit();
            AudioManager.Instance?.PlayEnemyHit();
        }
    }

    // 사망 처리는 한 번만 실행해서 경험치 중복 지급을 막음
    private void Die()
    {
        if (deathHandled)
        {
            return;
        }

        deathHandled = true;
        IsDead = true;
        RunStats.AddKill();   // ← 추가: 엔딩 화면의 Monster Kills 집계
        AwardExpOnce();
        OnEnemyKilled?.Invoke(this);
        ApplyDeathPresentation();
        ScheduleDestruction();
    }

    private void ApplyDeathPresentation()
    {
        enemyAnimator?.PlayDeath();

        if (AudioManager.Instance != null)
        {
            AudioManager.Instance.PlayEnemyDeath();
        }

        if (TryGetComponent(out NavMeshAgent agent))
        {
            agent.enabled = false;
        }

        foreach (Collider enemyCollider in GetComponentsInChildren<Collider>())
        {
            enemyCollider.enabled = false;
        }
    }

    private void ScheduleDestruction()
    {
        NetworkObject networkObject = GetComponent<NetworkObject>();
        if (networkObject != null && networkObject.IsSpawned)
        {
            Invoke(nameof(DespawnNetworkObject), destroyDelay);
            return;
        }

        Destroy(gameObject, destroyDelay);
    }

    private void DespawnNetworkObject()
    {
        NetworkObject networkObject = GetComponent<NetworkObject>();
        if (networkObject != null && networkObject.IsSpawned &&
            networkObject.NetworkManager != null && networkObject.NetworkManager.IsServer)
        {
            networkObject.Despawn(true);
        }
    }

    // 처치 보상 경험치를 플레이어 레벨 시스템에 1회 지급
    private void AwardExpOnce()
    {
        if (expAwarded || ExpReward <= 0)
        {
            return;
        }

        expAwarded = true;

        var networkObject = GetComponent<NetworkObject>();
        GameObject playerObject = networkObject != null && networkObject.IsSpawned
            ? lastDamageSource != null ? lastDamageSource.gameObject : null
            : GameObject.FindGameObjectWithTag("Player");

        if (playerObject == null)
        {
            return;
        }

        if (playerObject.TryGetComponent(out PlayerLevelSystem levelSystem))
        {
            levelSystem.AddExp(ExpReward);
        }
    }

    private void OnValidate()
    {
        maxHealth = Mathf.Max(1, maxHealth);
        expReward = Mathf.Max(0, expReward);
        destroyDelay = Mathf.Max(0f, destroyDelay);
    }
}
