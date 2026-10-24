// 조준 방향으로 발사체를 생성하는 고유 스킬.
using UnityEngine;

[CreateAssetMenu(menuName = "Player Skills/Projectile Skill")]
public sealed class ProjectileSkillSO : PlayerSkillSO
{
    // 발사체 프리팹
    [SerializeField] private DamageProjectile projectilePrefab;
    // 이동 속도
    [SerializeField, Min(0.1f)] private float speed = 12f;
    // 최대 사거리
    [SerializeField, Min(0.1f)] private float maxDistance = 15f;
    // 공격력 계수
    [SerializeField, Min(0.1f)] private float damageMultiplier = 1f;
    [SerializeField, Min(0.1f)] private float visualScale = 1f;
    [SerializeField, Min(0f)] private float explosionRadius;

    // 발사체 생성
    public override bool Cast(PlayerSkillController owner, PlayerDamageType damageType)
    {
        if (owner == null || projectilePrefab == null || owner.SkillOrigin == null || owner.DamageDealer == null ||
            !NetworkHealth.CanWrite(owner.gameObject))
            return false;

        DamageProjectile projectile = Instantiate(
            projectilePrefab,
            owner.SkillOrigin.position,
            owner.SkillOrigin.rotation);

        float attackDamage = owner.Stats != null ? owner.Stats.AttackDamage : 10f;
        int damage = Mathf.Max(1, Mathf.RoundToInt(attackDamage * damageMultiplier));
        projectile.Initialize(owner.DamageDealer, damageType, damage, speed, maxDistance,
            owner.SkillOrigin.forward, explosionRadius, visualScale);
        return true;
    }
}
