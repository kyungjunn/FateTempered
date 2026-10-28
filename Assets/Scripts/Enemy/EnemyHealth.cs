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
    private float healthMultiplier = 1f;

    public int MaxHealth => hasReplicatedHealthSnapshot
        ? replicatedMaxHealth
        : Mathf.Max(1, Mathf.RoundToInt((stats != null ? stats.MaxHealth : maxHealth) * healthMultiplier));
    public int ExpReward => stats != null ? stats.ExpReward : expReward;
    public int CurrentHealth { get; private set; }
    public bool IsDead { get; private set; }
    public float HealthPercent => MaxHealth > 0 ? (float)CurrentHealth / MaxHealth : 0f;

    public event Action<int, int> OnHealthChanged;

    private EnemyAnimator enemyAnimator;
    private EnemyHitFeedback hitFeedback;
    private bool expAwarded;
    private bool deathHandled;
    private EnemyAggro aggro;

    public void RecordDamageSource(PlayerDamageDealer source)
    {
        if (NetworkAuthority.CanSimulate(gameObject) && aggro != null) aggro.RecordHit(source);
    }

    // 협동 난이도. 서버가 Spawn 전에 한 번 적용한다.
    public void ApplyHealthMultiplier(float multiplier)
    {
        if (!NetworkAuthority.CanSimulate(gameObject) || IsDead) return;
        healthMultiplier = Mathf.Max(0.1f, multiplier);
        CurrentHealth = MaxHealth;
        OnHealthChanged?.Invoke(CurrentHealth, MaxHealth);
    }

    private void Awake()
    {
        enemyAnimator = GetComponent<EnemyAnimator>();
        hitFeedback = GetComponent<EnemyHitFeedback>();
        aggro = GetComponent<EnemyAggro>();
        if (aggro == null) aggro = gameObject.AddComponent<EnemyAggro>();

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
        RunStats.AddKill();
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

    // 처치 보상 경험치를 1회 지급. 네트워크는 피해를 준 플레이어 전원이 전액을 받는다.
    private void AwardExpOnce()
    {
        if (expAwarded || ExpReward <= 0)
        {
            return;
        }

        expAwarded = true;

        var networkObject = GetComponent<NetworkObject>();
        if (networkObject != null && networkObject.IsSpawned)
        {
            foreach (PlayerDamageDealer contributor in aggro.Contributors)
            {
                if (contributor == null) continue;
                if (contributor.TryGetComponent(out PlayerHealth health) && health.IsDead) continue;
                if (contributor.TryGetComponent(out PlayerLevelSystem contributorLevel))
                    contributorLevel.AddExp(ExpReward);
            }
            return;
        }

        GameObject playerObject = GameObject.FindGameObjectWithTag("Player");
        if (playerObject != null && playerObject.TryGetComponent(out PlayerLevelSystem levelSystem))
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
