using UnityEngine;

// 보상 지급 결과
public struct QuestRewardResult
{
    public QuestRewardSO reward;
    public bool applied;
    public float grantedAmount;

    public string Description => applied && reward != null
        ? reward.DescribeGranted(grantedAmount)
        : "보상 지급 실패";
}
