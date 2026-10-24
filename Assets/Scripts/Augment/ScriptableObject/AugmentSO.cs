using UnityEngine;

// 증강 카드 공통 정보. 스탯 증강과 스킬 증강이 상속한다.
public abstract class AugmentSO : ScriptableObject
{
    [Header("Info")] // 기본 정보
    public string augmentName;
    [TextArea]
    public string description;

    public Sprite icon;

    [Header("Rarity")] // 희귀도
    public RarityType rarity;

    [Range(1, 100)] // 확률
    public int weight = 50; // 높을 수록 잘 나옴.

    // 카드 제목. 스탯은 "Damage +5", 스킬은 이름 그대로
    public abstract string GetDisplayName();

    // 현재 플레이어가 이 증강을 획득할 수 있는지 판정한다.
    // 공용 증강은 기본적으로 모든 플레이어에게 적용된다.
    public virtual bool IsAvailableFor(GameObject player)
    {
        return true;
    }
}
