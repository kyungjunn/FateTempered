using UnityEngine;

[CreateAssetMenu(fileName = "AugmentChoiceReward", menuName = "Game/Quest Rewards/Augment Choice")]
public sealed class AugmentChoiceRewardSO : QuestRewardSO
{
    public override string Description => $"증강 선택 {amount}회";
    // 활성 세션·현재 플레이어
    protected override bool CanApplyTo(GameObject player) => UIManager.Instance != null
        && UIManager.Instance.augmentPanelUI != null
        && AugmentManager.Instance != null
        && GameManager.Instance != null
        && GameManager.Instance.IsActiveSession
        && GameManager.Instance.CurrentPlayer == player;
    protected override float Grant(GameObject player)
    {
        for (int i = 0; i < amount; i++)
            UIManager.Instance.RequestAugmentChoice();
        return amount;
    }
}
