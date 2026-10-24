using UnityEngine;

[RequireComponent(typeof(EnemyHealth))]
// 보스의 후방에서 들어온 플레이어 공격에 약점 피해를 적용
public class BossWeakPoint : MonoBehaviour, IDirectionalDamageable
{
    [SerializeField] private BossEnemyStats bossStats;
    [SerializeField] private EnemyHealth enemyHealth;
    [SerializeField, Range(1f, 5f)] private float rearDamageMultiplier = 2f;
    [SerializeField, Range(1f, 180f)] private float rearWeakPointAngle = 100f;

    private float RearDamageMultiplier => bossStats != null
        ? bossStats.RearWeakPointDamageMultiplier
        : rearDamageMultiplier;

    private float RearWeakPointAngle => bossStats != null
        ? bossStats.RearWeakPointAngle
        : rearWeakPointAngle;

    private void Awake()
    {
        if (enemyHealth == null)
        {
            enemyHealth = GetComponent<EnemyHealth>();
        }
    }

    public void TakeDamage(int damage, Vector3 attackSourcePosition)
    {
        if (enemyHealth == null || enemyHealth.IsDead || damage <= 0)
        {
            return;
        }

        int finalDamage = IsRearAttack(attackSourcePosition)
            ? Mathf.Max(1, Mathf.RoundToInt(damage * RearDamageMultiplier))
            : damage;

        enemyHealth.TakeDamage(finalDamage);
    }

    private bool IsRearAttack(Vector3 attackSourcePosition)
    {
        Vector3 directionToAttacker = attackSourcePosition - transform.position;
        directionToAttacker.y = 0f;

        Vector3 bossForward = transform.forward;
        bossForward.y = 0f;

        if (directionToAttacker.sqrMagnitude < 0.001f || bossForward.sqrMagnitude < 0.001f)
        {
            return false;
        }

        directionToAttacker.Normalize();
        bossForward.Normalize();

        float rearThreshold = Mathf.Cos(RearWeakPointAngle * 0.5f * Mathf.Deg2Rad);
        return Vector3.Dot(-bossForward, directionToAttacker) >= rearThreshold;
    }

    private void OnValidate()
    {
        rearDamageMultiplier = Mathf.Max(1f, rearDamageMultiplier);
        rearWeakPointAngle = Mathf.Clamp(rearWeakPointAngle, 1f, 180f);
    }

    private void OnDrawGizmosSelected()
    {
        Vector3 center = transform.position + Vector3.up * 0.15f;
        Vector3 rearDirection = -transform.forward;
        rearDirection.y = 0f;

        if (rearDirection.sqrMagnitude < 0.001f)
        {
            return;
        }

        rearDirection.Normalize();
        float halfAngle = RearWeakPointAngle * 0.5f;
        Vector3 leftEdge = Quaternion.Euler(0f, -halfAngle, 0f) * rearDirection;
        Vector3 rightEdge = Quaternion.Euler(0f, halfAngle, 0f) * rearDirection;

        Color previousColor = Gizmos.color;
        Gizmos.color = new Color(1f, 0.75f, 0.1f, 0.9f);
        Gizmos.DrawLine(center, center + leftEdge * 4f);
        Gizmos.DrawLine(center, center + rightEdge * 4f);
        Gizmos.color = previousColor;
    }
}
