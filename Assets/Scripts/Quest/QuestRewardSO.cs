using UnityEngine;

// 공유 설정 전용 · 지급 상태는 퀘스트 매니저 소유
public abstract class QuestRewardSO : ScriptableObject
{
    [Min(1)] public int amount = 1;

    public bool IsValid => amount > 0;
    public abstract string Description { get; }

    public bool CanApply(GameObject player)
    {
        if (player == null || !IsValid) return false;
        var health = player.GetComponent<PlayerHealth>();
        return (health == null || !health.IsDead) && CanApplyTo(player);
    }

    public QuestRewardResult Apply(GameObject player)
    {
        var result = new QuestRewardResult { reward = this };
        // 지급 직전 대상 재검증
        if (!CanApply(player)) return result;
        result.grantedAmount = Grant(player);
        result.applied = true;
        return result;
    }

    public virtual string DescribeGranted(float grantedAmount) => Description;
    protected abstract bool CanApplyTo(GameObject player);
    protected abstract float Grant(GameObject player);
}
