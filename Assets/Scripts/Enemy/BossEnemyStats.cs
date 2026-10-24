using UnityEngine;

[CreateAssetMenu(fileName = "New Boss Enemy Stats", menuName = "Game/Boss Enemy Stats")]
// 일반 적 능력치에 강공격 설정을 추가한 보스 전용 데이터
public class BossEnemyStats : EnemyStats
{
    [Header("Strong Attack")]
    [SerializeField] private int strongAttackDamage = 24;
    [SerializeField] private float strongAttackRadius = 6.5f;
    [SerializeField, Range(0f, 1f)] private float strongAttackChance = 0.35f;
    [SerializeField] private float strongAttackCooldown = 2.8f;
    [SerializeField] private float strongAttackMinInterval = 5f;
    [SerializeField] private float strongAttackFallbackHitDelay = 2.15f;

    [Header("Boss Control")]
    [SerializeField, Range(1f, 90f)] private float attackFacingAngle = 25f;

    [Header("Rear Weak Point")]
    [SerializeField, Range(1f, 5f)] private float rearWeakPointDamageMultiplier = 2f;
    [SerializeField, Range(1f, 180f)] private float rearWeakPointAngle = 100f;

    public int StrongAttackDamage => strongAttackDamage;
    public float StrongAttackRadius => strongAttackRadius;
    public float StrongAttackChance => strongAttackChance;
    public float StrongAttackCooldown => strongAttackCooldown;
    public float StrongAttackMinInterval => strongAttackMinInterval;
    public float StrongAttackFallbackHitDelay => strongAttackFallbackHitDelay;
    public float AttackFacingAngle => attackFacingAngle;
    public float RearWeakPointDamageMultiplier => rearWeakPointDamageMultiplier;
    public float RearWeakPointAngle => rearWeakPointAngle;

    protected override void OnValidate()
    {
        base.OnValidate();
        strongAttackDamage = Mathf.Max(1, strongAttackDamage);
        strongAttackRadius = Mathf.Max(0.1f, strongAttackRadius);
        strongAttackChance = Mathf.Clamp01(strongAttackChance);
        strongAttackCooldown = Mathf.Max(0.1f, strongAttackCooldown);
        strongAttackMinInterval = Mathf.Max(0f, strongAttackMinInterval);
        strongAttackFallbackHitDelay = Mathf.Max(0f, strongAttackFallbackHitDelay);
        attackFacingAngle = Mathf.Clamp(attackFacingAngle, 1f, 90f);
        rearWeakPointDamageMultiplier = Mathf.Max(1f, rearWeakPointDamageMultiplier);
        rearWeakPointAngle = Mathf.Clamp(rearWeakPointAngle, 1f, 180f);
    }
}
