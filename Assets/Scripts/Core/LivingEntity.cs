using UnityEngine;

// 피해를 받을 수 있는 생명체 공통 기반 클래스
public abstract class LivingEntity : MonoBehaviour, IDamageable
{
    // 자식 클래스에서 실제 피해 처리 로직을 구현
    public virtual void TakeDamage(int damage)
    {

    }
}
