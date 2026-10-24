using UnityEngine;

[CreateAssetMenu(fileName = "HealReward", menuName = "Game/Quest Rewards/Heal")]
public sealed class HealRewardSO : QuestRewardSO
{
    public override string Description => $"체력 {amount} 회복";
    protected override bool CanApplyTo(GameObject player) => player.GetComponent<PlayerHealth>() != null;
    // 최대 체력 제한 · 실제 회복량 반환
    protected override float Grant(GameObject player) => player.GetComponent<PlayerHealth>().Heal(amount);
    public override string DescribeGranted(float grantedAmount) => $"체력 {grantedAmount:0.#} 회복";
}
