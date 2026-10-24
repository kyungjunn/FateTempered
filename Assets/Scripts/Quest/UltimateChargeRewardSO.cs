using UnityEngine;

[CreateAssetMenu(fileName = "UltimateChargeReward", menuName = "Game/Quest Rewards/Ultimate Charge")]
public sealed class UltimateChargeRewardSO : QuestRewardSO
{
    public override string Description => $"궁극기 게이지 {amount} 충전";
    protected override bool CanApplyTo(GameObject player) => player.GetComponent<PlayerUltimateGauge>() != null;
    protected override float Grant(GameObject player)
    {
        var gauge = player.GetComponent<PlayerUltimateGauge>();
        // 최대 게이지 제한 · 실제 충전량 계산
        float previous = gauge.Current;
        gauge.Add(amount);
        return gauge.Current - previous;
    }
    public override string DescribeGranted(float grantedAmount) => $"궁극기 게이지 {grantedAmount:0.#} 충전";
}
