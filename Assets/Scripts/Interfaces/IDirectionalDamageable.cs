using UnityEngine;

// 공격이 들어온 위치를 기준으로 방향성 피해를 처리하는 대상 인터페이스
public interface IDirectionalDamageable
{
    void TakeDamage(int damage, Vector3 attackSourcePosition);
}
