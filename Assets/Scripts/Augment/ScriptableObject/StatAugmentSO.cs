using UnityEngine;

// 스탯 증강. 클래스명은 기존 에셋의 스크립트 GUID에 물려 있으므로 유지할 것.
[CreateAssetMenu(fileName = "New Augment",
                 menuName = "Game/Augment")]
public class StatAugmentSO : AugmentSO
{
    [Header("Stat")] // 스탯
    public AugmentType type;
    public float value;

    public override string GetDisplayName() => $"{augmentName} +{value}";
}
