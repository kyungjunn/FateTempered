using UnityEngine;

// 자식 모델의 타격 이벤트를 부모 적의 공격 판정으로 전달
[RequireComponent(typeof(Animator))]
public sealed class EnemyAnimationEventRelay : MonoBehaviour
{
    [SerializeField] private EnemyAttack enemyAttack;

    private void Awake()
    {
        if (enemyAttack == null)
        {
            enemyAttack = GetComponentInParent<EnemyAttack>();
        }
    }

    public void OnAttackHit()
    {
        enemyAttack?.OnAttackHit();
    }
}
