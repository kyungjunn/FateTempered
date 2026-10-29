using System;
using System.Collections.Generic;
using System.Globalization;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public sealed class PlayerResultUI : MonoBehaviour
{
    [Header("플레이어")]
    [SerializeField] private TMP_Text playerNameText;
    [SerializeField] private TMP_Text championText;
    [SerializeField] private TMP_Text clientIdText;
    [SerializeField] private Image portraitImage;
    [SerializeField] private Sprite swordPortrait;
    [SerializeField] private Sprite ninjaPortrait;
    [SerializeField] private Sprite wizardPortrait;

    [Header("전투 기록")]
    [SerializeField] private TMP_Text damageDealtText;
    [SerializeField] private TMP_Text killsText;
    [SerializeField] private TMP_Text levelText;
    [SerializeField] private TMP_Text aliveTimeText;

    [Header("최종 능력치")]
    [SerializeField] private TMP_Text attackDamageText;
    [SerializeField] private TMP_Text maxHpText;
    [SerializeField] private TMP_Text defenseText;
    [SerializeField] private TMP_Text moveSpeedText;
    [SerializeField] private TMP_Text attackSpeedText;

    [Header("증강 목록")]
    [SerializeField] private Transform augmentListContent;
    [SerializeField] private GameObject augmentRowTemplate;
    [SerializeField] private Image augmentRowIcon;
    [SerializeField] private TMP_Text augmentRowText;
    [SerializeField] private TMP_Text noAugmentsText;

    private sealed class AugmentRowView
    {
        public GameObject Root;
        public Image Icon;
        public TMP_Text Text;
    }

    private static AugmentSO[] augmentMetadata;
    private readonly List<AugmentRowView> augmentRows = new List<AugmentRowView>();

    private void Awake()
    {
        if (augmentRowTemplate != null)
            augmentRowTemplate.SetActive(false);
    }

    public void SetVisible(bool visible)
    {
        if (gameObject.activeSelf != visible)
            gameObject.SetActive(visible);
    }

    public void Display(PlayerResult result)
    {
        SetVisible(true);
        SetText(playerNameText, "이름: " + (string.IsNullOrEmpty(result.DisplayName.ToString())
            ? "이름 없음"
            : result.DisplayName.ToString()));
        SetText(championText, "캐릭터: " + GetChampionName(result.Champion));
        SetText(clientIdText, "플레이어 ID: " + result.ClientId.ToString(CultureInfo.InvariantCulture));
        SetPortrait(result.Champion);

        SetText(damageDealtText, "누적 피해량: " + result.DamageDealt.ToString("N0", CultureInfo.InvariantCulture));
        SetText(killsText, "처치: " + result.Kills.ToString(CultureInfo.InvariantCulture));
        SetText(levelText, "최종 레벨: " + result.Level.ToString(CultureInfo.InvariantCulture));
        SetText(aliveTimeText, "생존 시간: " + FormatDuration(result.AliveSeconds));

        SetText(attackDamageText, "공격력: " + FormatNumber(result.AttackDamage));
        SetText(maxHpText, "최대 체력: " + FormatNumber(result.MaxHp));
        SetText(defenseText, "방어력: " + FormatNumber(result.Defense));
        SetText(moveSpeedText, "이동 속도: " + FormatNumber(result.MoveSpeed));
        SetText(attackSpeedText, "공격 속도: " + FormatNumber(result.AttackSpeed));

        RenderAugments(result.Augments);
    }

    private void SetPortrait(PlayerCharacterId champion)
    {
        if (portraitImage == null)
            return;

        Sprite sprite = null;
        switch (champion)
        {
            case PlayerCharacterId.Sword:
                sprite = swordPortrait;
                break;
            case PlayerCharacterId.Ninja:
                sprite = ninjaPortrait;
                break;
            case PlayerCharacterId.Wizard:
                sprite = wizardPortrait;
                break;
        }

        portraitImage.preserveAspect = true;
        portraitImage.sprite = sprite;
        portraitImage.enabled = sprite != null;
    }

    private void RenderAugments(AugmentResult[] results)
    {
        int renderedCount = 0;
        if (results != null)
        {
            for (int i = 0; i < results.Length; i++)
            {
                AugmentResult result = results[i];
                string displayName;
                Sprite icon;
                if (result.Kind == AugmentResultKind.Skill)
                {
                    AugmentSO metadata = FindSkillAugment(result.SkillId.ToString());
                    displayName = metadata != null ? metadata.GetDisplayName() : result.SkillId.ToString();
                    if (string.IsNullOrEmpty(displayName) && metadata != null)
                        displayName = metadata.augmentName;
                    displayName = (string.IsNullOrEmpty(displayName) ? "스킬 증강" : displayName) +
                                  " ×" + result.Stacks.ToString(CultureInfo.InvariantCulture);
                    icon = metadata != null ? metadata.icon : null;
                }
                else if (result.Kind == AugmentResultKind.Stat)
                {
                    displayName = GetStatName(result.StatType) + " " + FormatSigned(result.Value);
                    AugmentSO metadata = FindStatAugment(result.StatType);
                    icon = metadata != null ? metadata.icon : null;
                }
                else
                {
                    displayName = "증강 " + FormatSigned(result.Value);
                    icon = null;
                }

                AugmentRowView row = GetOrCreateAugmentRow(renderedCount);
                if (row == null)
                    break;

                SetText(row.Text, displayName);
                if (row.Icon != null)
                {
                    row.Icon.sprite = icon;
                    row.Icon.preserveAspect = true;
                    row.Icon.enabled = icon != null;
                }
                row.Root.SetActive(true);
                renderedCount++;
            }
        }

        for (int i = renderedCount; i < augmentRows.Count; i++)
            augmentRows[i].Root.SetActive(false);

        if (noAugmentsText != null)
        {
            noAugmentsText.gameObject.SetActive(renderedCount == 0);
            noAugmentsText.text = "증강 없음";
        }
    }

    private AugmentRowView GetOrCreateAugmentRow(int index)
    {
        if (index < augmentRows.Count)
            return augmentRows[index];

        if (augmentRowTemplate == null || augmentListContent == null ||
            augmentRowIcon == null || augmentRowText == null)
        {
            return null;
        }

        Transform templateRoot = augmentRowTemplate.transform;
        GameObject rowObject = Instantiate(augmentRowTemplate, augmentListContent, false);
        rowObject.SetActive(false);
        Transform iconTransform = FindCloneChild(templateRoot, augmentRowIcon.transform, rowObject.transform);
        Transform textTransform = FindCloneChild(templateRoot, augmentRowText.transform, rowObject.transform);
        AugmentRowView row = new AugmentRowView
        {
            Root = rowObject,
            Icon = iconTransform != null ? iconTransform.GetComponent<Image>() : null,
            Text = textTransform != null ? textTransform.GetComponent<TMP_Text>() : null
        };
        augmentRows.Add(row);
        return row;
    }

    private static Transform FindCloneChild(Transform templateRoot, Transform templateChild, Transform cloneRoot)
    {
        if (templateRoot == null || templateChild == null || cloneRoot == null)
            return null;
        if (templateRoot == templateChild)
            return cloneRoot;

        string path = templateChild.name;
        Transform current = templateChild.parent;
        while (current != null && current != templateRoot)
        {
            path = current.name + "/" + path;
            current = current.parent;
        }
        return current == templateRoot ? cloneRoot.Find(path) : null;
    }

    private static AugmentSO FindSkillAugment(string skillId)
    {
        AugmentSO[] metadata = GetAugmentMetadata();
        for (int i = 0; i < metadata.Length; i++)
        {
            AugmentSO augment = metadata[i];
            if (augment != null && augment is SkillAugmentSO && augment.name == skillId)
                return augment;
        }
        return null;
    }

    private static AugmentSO FindStatAugment(AugmentType type)
    {
        AugmentSO[] metadata = GetAugmentMetadata();
        for (int i = 0; i < metadata.Length; i++)
        {
            if (metadata[i] is StatAugmentSO stat && stat.type == type)
                return stat;
        }
        return null;
    }

    private static AugmentSO[] GetAugmentMetadata()
    {
        if (augmentMetadata == null)
        {
            augmentMetadata = Resources.LoadAll<AugmentSO>("Augments/Data");
            Array.Sort(augmentMetadata, (left, right) => string.CompareOrdinal(
                left != null ? left.name : string.Empty,
                right != null ? right.name : string.Empty));
        }
        return augmentMetadata;
    }

    private static string GetChampionName(PlayerCharacterId champion)
    {
        switch (champion)
        {
            case PlayerCharacterId.Sword: return "검사";
            case PlayerCharacterId.Ninja: return "닌자";
            case PlayerCharacterId.Wizard: return "마법사";
            default: return "알 수 없음";
        }
    }

    private static string GetStatName(AugmentType type)
    {
        switch (type)
        {
            case AugmentType.AttackDamage: return "공격력";
            case AugmentType.HP: return "최대 체력";
            case AugmentType.MoveSpeed: return "이동 속도";
            case AugmentType.AttackSpeed: return "공격 속도";
            case AugmentType.Defense: return "방어력";
            default: return "능력치";
        }
    }

    private static string FormatNumber(float value)
    {
        return value.ToString("0.##", CultureInfo.InvariantCulture);
    }

    private static string FormatSigned(float value)
    {
        string formatted = value.ToString("0.##", CultureInfo.InvariantCulture);
        return value > 0f ? "+" + formatted : formatted;
    }

    private static string FormatDuration(float seconds)
    {
        long totalSeconds = Math.Max(0L, (long)Math.Floor(seconds));
        return (totalSeconds / 60L).ToString("00", CultureInfo.InvariantCulture) + ":" +
               (totalSeconds % 60L).ToString("00", CultureInfo.InvariantCulture);
    }

    private static void SetText(TMP_Text label, string value)
    {
        if (label != null)
            label.text = value;
    }
}
