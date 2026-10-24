using TMPro;
using UnityEngine;
using UnityEngine.UI;

// 증강 슬롯 프리팹의 루트에 붙이는 스크립트
public class AugmentSlotUI : MonoBehaviour
{
    [SerializeField] private Image frameImage;        // 테두리 (등급별 색상)
    [SerializeField] private Image iconImage;         // 증강 아이콘
    [SerializeField] private TMP_Text valueText;      // 아이콘 아래쪽 "+20"
    [SerializeField] private TMP_Text nameText;       // 슬롯 아래 이름
    [SerializeField] private GameObject legendaryStar; // 전설 등급일 때 켜지는 별

    [Header("등급별 테두리 색")]
    [SerializeField] private Color normalFrameColor = new Color(0.55f, 0.35f, 0.25f);
    [SerializeField] private Color legendaryFrameColor = new Color(1f, 0.78f, 0.2f);

    public void Setup(AugmentData data)
    {
        iconImage.sprite = data.icon;
        valueText.text = data.valueText;
        nameText.text = data.displayName;

        bool isLegendary = data.rarity == AugmentRarity.Legendary;
        frameImage.color = isLegendary ? legendaryFrameColor : normalFrameColor;
        if (legendaryStar != null) legendaryStar.SetActive(isLegendary);
    }
}
