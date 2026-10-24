using UnityEngine;
using TMPro;
using UnityEngine.UI;

public class AugmentOptionUI : MonoBehaviour
{
    [SerializeField] private Button button;

    public Image iconImage;
    public Image backgroundImage;

    public TextMeshProUGUI titleText;
    public TextMeshProUGUI descText;
    public TextMeshProUGUI rarityText;

    private AugmentSO currentAugment;
    private AugmentPanelUI panel;

    private void Awake()
    {
        CacheButton();
        panel = GetComponentInParent<AugmentPanelUI>(true);
    }

    public void Setup(AugmentSO augment)
    {
        currentAugment = augment;

        if (titleText != null)
        {
            titleText.text = augment != null ? augment.GetDisplayName() : string.Empty;
        }

        if (descText != null)
        {
            descText.text = augment != null ? augment.description : string.Empty;
        }

        if (rarityText != null)
        {
            rarityText.text = augment != null ? augment.rarity.ToString() : string.Empty;
        }

        if (iconImage != null)
        {
            iconImage.sprite = augment != null ? augment.icon : null;
        }

        if (augment != null)
        {
            SetRarityColor(augment.rarity);
        }
    }

    public AugmentSO CurrentAugment => currentAugment;

    public void Clear()
    {
        currentAugment = null;
        Setup(null);
    }

    public void SetInteractable(bool interactable)
    {
        CacheButton();

        if (button != null)
        {
            button.interactable = interactable;
        }
    }

    private void SetRarityColor(RarityType rarity)
    {
        if (backgroundImage == null)
        {
            return;
        }

        switch (rarity)
        {
            case RarityType.Common:
                backgroundImage.color = Color.white;
                break;
            case RarityType.Rare:
                backgroundImage.color = Color.blue;
                break;
            case RarityType.Epic:
                backgroundImage.color = new Color(0.6f, 0f, 1f);
                break;
            case RarityType.Legendary:
                backgroundImage.color = new Color(1f, 0.5f, 0f);
                break;
        }
    }

    public void OnClick()
    {
        CacheButton();

        if (button != null && !button.interactable)
        {
            return;
        }

        if (panel == null)
        {
            panel = GetComponentInParent<AugmentPanelUI>(true);
        }

        panel?.Select(currentAugment);
    }

    private void CacheButton()
    {
        if (button == null)
        {
            button = GetComponentInChildren<Button>(true);
        }
    }
}
