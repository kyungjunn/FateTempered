using UnityEngine;

// 지정된 피해 종류로 준 실제 피해의 일정 비율만큼 체력을 회복한다.
public class LifeStealSkill : AugmentSkill
{
    // 피해 분류
    [SerializeField] private PlayerDamageType damageType;
    // 기본 흡혈률
    [SerializeField, Min(0f)] private float baseLifeStealRatio = 0.03f;
    // 중첩 증가율
    [SerializeField, Min(0f)] private float ratioPerAdditionalStack = 0.02f;

    private PlayerDamageDealer damageDealer;
    private PlayerHealth playerHealth;
    // 소수 회복량
    private float pendingHealing;

    protected override void OnApply()
    {
        damageDealer = Player.GetComponent<PlayerDamageDealer>();
        playerHealth = Player.GetComponent<PlayerHealth>();

        if (damageDealer == null)
        {
            damageDealer = Player.AddComponent<PlayerDamageDealer>();
        }

        damageDealer.OnDamageDealt += HandleDamageDealt;
    }

    protected override void OnRemove()
    {
        if (damageDealer != null)
        {
            damageDealer.OnDamageDealt -= HandleDamageDealt;
            damageDealer = null;
        }
    }

    // 실제 피해 수신
    private void HandleDamageDealt(int damage, PlayerDamageType dealtDamageType)
    {
        if (dealtDamageType != damageType || playerHealth == null)
        {
            return;
        }

        float ratio = baseLifeStealRatio + ratioPerAdditionalStack * (StackCount - 1);
        pendingHealing += damage * ratio;
        int healAmount = Mathf.FloorToInt(pendingHealing);

        if (healAmount > 0)
        {
            pendingHealing -= healAmount;
            playerHealth.Heal(healAmount);
        }
    }

    private void OnValidate()
    {
        baseLifeStealRatio = Mathf.Max(0f, baseLifeStealRatio);
        ratioPerAdditionalStack = Mathf.Max(0f, ratioPerAdditionalStack);
    }
}
