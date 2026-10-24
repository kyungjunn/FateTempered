using TMPro;
using UnityEngine;
using UnityEngine.UI;

public sealed class SkillSlotUI : MonoBehaviour
{
    [SerializeField] private Image icon;
    [SerializeField] private Image progress;
    [SerializeField] private TextMeshProUGUI cooldownText;
    private static readonly Color Dim = new Color(0.22f, 0.27f, 0.33f, 1f);

    public void SetIcon(Sprite sprite)
    {
        icon.sprite = sprite;
        progress.sprite = sprite;
        icon.enabled = sprite != null;
        progress.enabled = sprite != null;
    }

    public void Refresh(float remaining, float duration, bool channeling, int charges, int maxCharges)
    {
        bool cooling = remaining > 0f;
        bool ready = !cooling && !channeling;
        icon.color = ready ? Color.white : Dim;
        progress.fillAmount = channeling ? 0f : duration > 0f ? Mathf.Clamp01(1f - remaining / duration) : 1f;
        cooldownText.gameObject.SetActive(cooling || maxCharges > 1);
        if (cooling && maxCharges > 1)
            cooldownText.SetText("{0}s\n{1}/{2}", Mathf.CeilToInt(remaining), charges, maxCharges);
        else if (cooling)
            cooldownText.SetText("{0}", Mathf.CeilToInt(remaining));
        else if (maxCharges > 1)
            cooldownText.SetText("{0}/{1}", charges, maxCharges);
    }
}
