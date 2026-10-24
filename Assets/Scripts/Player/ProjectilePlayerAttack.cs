using UnityEngine;

[RequireComponent(typeof(PlayerDamageDealer))]
// 원거리 기본 공격: 발사 간격, 증강 투사체, 피해 전달.
public class ProjectilePlayerAttack : MonoBehaviour, IPlayerBasicAttack
{
    [SerializeField] private PlayerStats stats;
    [SerializeField] private DamageProjectile projectilePrefab;
    [SerializeField] private Transform projectileOrigin;
    [SerializeField] private float speed = 14f;
    [SerializeField] private float maxDistance = 14f;
    [SerializeField] private float damageMultiplier = 1f;
    [SerializeField] private float attackCooldown = 0.5f;

    private PlayerDamageDealer damageDealer;
    private PlayerAugments playerAugments;
    private WizardCombat wizardCombat;
    private float nextAttackTime;

    private float AttackDamage => stats != null ? stats.AttackDamage : 10f;
    // 공격 속도가 있으면 역수로 발사 간격 계산.
    private float AttackCooldown => stats != null && stats.AttackSpeed > 0f ? 1f / stats.AttackSpeed : attackCooldown;

    private void Awake()
    {
        if (stats == null)
        {
            stats = GetComponent<PlayerStats>();
        }

        damageDealer = GetComponent<PlayerDamageDealer>();
        playerAugments = GetComponent<PlayerAugments>();
        wizardCombat = GetComponent<WizardCombat>();
    }

    public bool TryAttack()
    {
        if (NetworkPlayerCombat.TryRouteAttack(this))
        {
            return false;
        }

        // 필수 참조와 쿨타임 검사: 실패 시 발사하지 않음.
        if (projectilePrefab == null || projectileOrigin == null || damageDealer == null)
        {
            return false;
        }

        if (Time.time < nextAttackTime)
        {
            return false;
        }

        if (wizardCombat == null)
            wizardCombat = GetComponent<WizardCombat>();

        bool empoweredWizardAttack = wizardCombat != null && wizardCombat.IsOverdriveActive;
        float overdriveDamage = wizardCombat != null ? wizardCombat.BasicDamageMultiplier : 1f;
        float visualScale = wizardCombat != null ? wizardCombat.BasicVisualScale : 1f;
        int additionalPierces = wizardCombat != null ? wizardCombat.BasicAdditionalPierces : 0;
        int damage = Mathf.Max(1, Mathf.RoundToInt(AttackDamage * damageMultiplier * overdriveDamage));
        // 증강값으로 추가 발사 수와 전체 산탄 각도 산출.
        int extraProjectileCount = 0;
        float spreadDegrees = 0f;

        if (playerAugments != null)
        {
            extraProjectileCount = Mathf.Max(0, Mathf.RoundToInt(
                playerAugments.GetCombatAugmentValue(PlayerCombatAugmentEffect.BasicProjectileCount)));
            spreadDegrees = Mathf.Max(0f, playerAugments.GetCombatAugmentSpread(
                PlayerCombatAugmentEffect.BasicProjectileCount));
        }

        int projectileCount = 1 + extraProjectileCount;
        for (int i = 0; i < projectileCount; i++)
        {
            // 투사체를 산탄 각도 안에 균등 배치. 한 발이면 정면.
            float yaw = projectileCount > 1
                ? Mathf.Lerp(-spreadDegrees * 0.5f, spreadDegrees * 0.5f,
                    i / (float)(projectileCount - 1))
                : 0f;
            Quaternion yawRotation = Quaternion.AngleAxis(yaw, projectileOrigin.up);

            // 발사 방향과 투사체 프리팹의 자체 회전을 함께 적용.
            Quaternion projectileRotation = yawRotation * projectileOrigin.rotation * projectilePrefab.transform.localRotation;
            Vector3 direction = yawRotation * projectileOrigin.forward;

            DamageProjectile projectile = Instantiate(
                projectilePrefab,
                projectileOrigin.position,
                projectileRotation);
            if (wizardCombat != null)
            {
                WizardProjectileVisual visual = projectile.gameObject.AddComponent<WizardProjectileVisual>();
                visual.Configure(empoweredWizardAttack);
            }

            // 피해 주체, 피해 유형, 속도, 사거리, 진행 방향 전달.
            projectile.Initialize(
                damageDealer,
                PlayerDamageType.BasicAttack,
                damage,
                speed,
                maxDistance,
                direction,
                visualScale: visualScale,
                additionalPierces: additionalPierces,
                empoweredWizardAttack: empoweredWizardAttack);
        }

        nextAttackTime = Time.time + AttackCooldown;
        // 성공적으로 발사한 뒤에만 다음 발사 가능 시각 갱신.
        return true;
    }

    private void OnValidate()
    {
        speed = Mathf.Max(0.1f, speed);
        maxDistance = Mathf.Max(0.1f, maxDistance);
        damageMultiplier = Mathf.Max(0.1f, damageMultiplier);
        attackCooldown = Mathf.Max(0.1f, attackCooldown);
    }
}
