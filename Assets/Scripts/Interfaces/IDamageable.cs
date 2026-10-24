using UnityEngine;

// 데미지를 입을 수 있는 모든 대상 인터페이스
public interface IDamageable
{
    void TakeDamage(int damage);
}
