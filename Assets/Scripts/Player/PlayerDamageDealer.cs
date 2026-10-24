using System;
using UnityEngine;

public enum PlayerDamageType
{
    BasicAttack,
    Skill
}

// 플레이어가 적에게 준 실제 피해량과 출처를 한 곳에서 전달한다.
public class PlayerDamageDealer : MonoBehaviour
{
    public event Action<int, PlayerDamageType> OnDamageDealt;
    public event Action<EnemyHealth, Vector3, int, PlayerDamageType> OnTargetDamaged;

    public int DealDamage(EnemyHealth enemy, int damage, PlayerDamageType damageType,
        Vector3? attackSourcePosition = null)
    {
        if (!NetworkHealth.CanWrite(gameObject) || enemy == null ||
            !NetworkHealth.CanWrite(enemy.gameObject) || enemy.IsDead || damage <= 0)
        {
            return 0;
        }

        int previousHealth = enemy.CurrentHealth;
        enemy.RecordDamageSource(this);
        Vector3 hitPosition = enemy.transform.position;
        IDirectionalDamageable directionalTarget = enemy.GetComponent<IDirectionalDamageable>();

        if (directionalTarget != null)
        {
            directionalTarget.TakeDamage(damage, attackSourcePosition ?? transform.position);
        }
        else
        {
            enemy.TakeDamage(damage);
        }

        // 약점 배율과 남은 체력을 반영한 실제 피해만 흡혈과 궁극기 게이지에 전달
        int actualDamage = Mathf.Max(0, previousHealth - enemy.CurrentHealth);

        if (actualDamage > 0)
        {
            OnDamageDealt?.Invoke(actualDamage, damageType);
            OnTargetDamaged?.Invoke(enemy, hitPosition, actualDamage, damageType);
        }

        return actualDamage;
    }
}
