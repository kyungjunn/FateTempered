using TMPro;
using UnityEngine;
using UnityEngine.UI;

// 증강 이름 및 중첩 표시 · 클릭·호버 동작 없음
public sealed class AugmentInventorySlotUI : MonoBehaviour
{
    [SerializeField] private Image icon;
    [SerializeField] private TextMeshProUGUI nameText;
    [SerializeField] private TextMeshProUGUI levelText;
    public SkillAugmentSO Augment { get; private set; }

    public void Configure(Image icon, TextMeshProUGUI nameText, TextMeshProUGUI levelText)
    {
        this.icon = icon;
        this.nameText = nameText;
        this.levelText = levelText;
    }

    public void SetAugment(SkillAugmentSO augment, int stacks = 0)
    {
        Augment = augment;
        icon.sprite = augment != null ? augment.icon : null;
        icon.enabled = icon.sprite != null;
        icon.raycastTarget = false;
        nameText.text = augment != null ? augment.augmentName : string.Empty;
        // 무제한 또는 복수 중첩 증강만 레벨 표시
        bool stackable = augment != null && (augment.maxStacks == 0 || augment.maxStacks > 1);
        levelText.text = stackable ? $"Lv.{stacks}" : string.Empty;
        levelText.gameObject.SetActive(stackable);
    }
}
