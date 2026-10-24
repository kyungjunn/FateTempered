using System;
using System.Collections.Generic;
using UnityEngine;

[Serializable]
public struct QuestRewardEntry
{
    public QuestRewardSO reward;
    [Min(1)] public int weight;
}

[CreateAssetMenu(fileName = "QuestRewardPool", menuName = "Game/Quest Reward Pool")]
public sealed class QuestRewardPoolSO : ScriptableObject
{
    public List<QuestRewardEntry> entries = new List<QuestRewardEntry>();

    // 단일 후보 확정 · 복수 후보 가중치 추첨
    public bool TrySelect(float sample, out QuestRewardSO reward)
    {
        return TrySelect(sample, null, out reward);
    }

    public bool TrySelect(float sample, Func<QuestRewardSO, bool> isEligible, out QuestRewardSO reward)
    {
        reward = default;
        if (entries == null || float.IsNaN(sample) || sample < 0f || sample > 1f)
            return false;

        // 유효한 지급 후보의 가중치 합산
        double totalWeight = 0;
        foreach (QuestRewardEntry entry in entries)
        {
            if (IsSelectable(entry, isEligible))
                totalWeight += entry.weight;
        }
        if (totalWeight <= 0) return false;

        double remaining = sample * totalWeight;
        foreach (QuestRewardEntry entry in entries)
        {
            if (!IsSelectable(entry, isEligible)) continue;
            reward = entry.reward;
            remaining -= entry.weight;
            if (remaining < 0) return true;
        }
        // 난수 상한 1 포함 · 마지막 유효 후보 선택
        return true;
    }

    private static bool IsSelectable(QuestRewardEntry entry, Func<QuestRewardSO, bool> isEligible)
    {
        return entry.weight > 0 && entry.reward != null && entry.reward.IsValid
            && (isEligible == null || isEligible(entry.reward));
    }
}
