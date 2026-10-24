using UnityEngine;

[CreateAssetMenu(fileName = "New Enemy Stats", menuName = "Game/Enemy Stats")]
// 적 종류별 능력치를 저장하는 ScriptableObject
public class EnemyStats : ScriptableObject
{
    [Header("Info")]
    [SerializeField] private string enemyName = "Enemy";

    [Header("Health")]
    [SerializeField] private int maxHealth = 20;

    [Header("Reward")]
    [SerializeField] private int expReward = 1000;

    [Header("Movement")]
    [SerializeField] private float moveSpeed = 3.5f;
    [SerializeField] private float acceleration = 8f;
    [SerializeField] private float stoppingDistance = 1.5f;
    [SerializeField] private float rotationSpeed = 12f;

    [Header("Detection")]
    [SerializeField] private float detectionRange = 12f;
    [SerializeField] private float repathRate = 0.15f;

    [Header("Patrol")]
    [SerializeField] private float patrolRadius = 8f;
    [SerializeField] private float patrolWaitTime = 0.3f;
    [SerializeField] private float patrolPointTolerance = 0.7f;

    [Header("Attack")]
    [SerializeField] private int attackDamage = 10;
    [SerializeField] private float attackRange = 1.8f;
    [SerializeField] private float attackCooldown = 1.2f;

    public string EnemyName => enemyName;
    public int MaxHealth => maxHealth;
    public int ExpReward => expReward;
    public float MoveSpeed => moveSpeed;
    public float Acceleration => acceleration;
    public float StoppingDistance => stoppingDistance;
    public float RotationSpeed => rotationSpeed;
    public float DetectionRange => detectionRange;
    public float RepathRate => repathRate;
    public float PatrolRadius => patrolRadius;
    public float PatrolWaitTime => patrolWaitTime;
    public float PatrolPointTolerance => patrolPointTolerance;
    public int AttackDamage => attackDamage;
    public float AttackRange => attackRange;
    public float AttackCooldown => attackCooldown;

    protected virtual void OnValidate()
    {
        // 인스펙터에서 잘못된 값이 들어가도 런타임 계산이 깨지지 않도록 보정
        maxHealth = Mathf.Max(1, maxHealth);
        expReward = Mathf.Max(0, expReward);
        moveSpeed = Mathf.Max(0.1f, moveSpeed);
        acceleration = Mathf.Max(0.1f, acceleration);
        stoppingDistance = Mathf.Max(0f, stoppingDistance);
        rotationSpeed = Mathf.Max(0.1f, rotationSpeed);
        detectionRange = Mathf.Max(0.1f, detectionRange);
        repathRate = Mathf.Max(0.02f, repathRate);
        patrolRadius = Mathf.Max(0.1f, patrolRadius);
        patrolWaitTime = Mathf.Max(0f, patrolWaitTime);
        patrolPointTolerance = Mathf.Max(0.1f, patrolPointTolerance);
        attackDamage = Mathf.Max(1, attackDamage);
        attackRange = Mathf.Max(0.1f, attackRange);
        attackCooldown = Mathf.Max(0.1f, attackCooldown);
    }
}
