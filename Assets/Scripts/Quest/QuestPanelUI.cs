using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// QuestManager 이벤트를 받아 현재 퀘스트 HUD와 성공/실패 토스트를 표시한다.
public class QuestPanelUI : MonoBehaviour
{
    [Header("Quest Card")]
    public CanvasGroup questCardGroup;
    public TextMeshProUGUI questNameText;
    public TextMeshProUGUI descriptionText;
    public TextMeshProUGUI progressText;
    public TextMeshProUGUI timerText;
    public CanvasGroup penaltyGroup;
    public TextMeshProUGUI penaltyText;
    public Image timerFillImage;

    [Header("Result Toast")]
    public CanvasGroup resultToastGroup;
    public Image resultBackgroundImage;
    public TextMeshProUGUI resultTitleText;
    public TextMeshProUGUI resultMessageText;

    [Header("Colors")]
    public Color successColor = new Color(0.08f, 0.28f, 0.16f, 0.92f);
    public Color failureColor = new Color(0.33f, 0.07f, 0.11f, 0.94f);

    [SerializeField] private float resultDuration = 2.5f;

    private QuestManager questManager;
    private Coroutine hideResultRoutine;

    private void Awake()
    {
        HideQuestCard();
        HideResultToast();
    }

    private void OnEnable()
    {
        TryBindQuestManager();
    }

    private void OnDisable()
    {
        UnbindQuestManager();

        if (hideResultRoutine != null)
        {
            StopCoroutine(hideResultRoutine);
            hideResultRoutine = null;
        }
    }

    private void Update()
    {
        if (questManager == null)
        {
            TryBindQuestManager();
        }
        else if (questManager.State == QuestState.Stopped)
        {
            HideQuestCard();
        }
    }

    private void TryBindQuestManager()
    {
        if (questManager != null || QuestManager.Instance == null)
        {
            return;
        }

        questManager = QuestManager.Instance;
        questManager.onQuestStarted += HandleQuestStarted;
        questManager.onQuestProgressChanged += HandleQuestProgressChanged;
        questManager.onQuestTimeChanged += UpdateTime;
        questManager.onQuestSucceeded += HandleQuestSucceeded;
        questManager.onQuestFailed += HandleQuestFailed;

        RefreshFromManager();
    }

    private void UnbindQuestManager()
    {
        if (questManager == null)
        {
            return;
        }

        questManager.onQuestStarted -= HandleQuestStarted;
        questManager.onQuestProgressChanged -= HandleQuestProgressChanged;
        questManager.onQuestTimeChanged -= UpdateTime;
        questManager.onQuestSucceeded -= HandleQuestSucceeded;
        questManager.onQuestFailed -= HandleQuestFailed;
        questManager = null;
    }

    private void RefreshFromManager()
    {
        if (questManager != null && questManager.State == QuestState.Active && questManager.CurrentQuest != null)
        {
            HandleQuestStarted(questManager.CurrentQuest);
            HandleQuestProgressChanged(questManager.CurrentProgress, questManager.CurrentQuest.targetAmount);
            UpdateTime(questManager.RemainingTime, questManager.CurrentQuest.timeLimit);
            return;
        }

        HideQuestCard();
    }

    private void HandleQuestStarted(QuestSO quest)
    {
        if (quest == null)
        {
            HideQuestCard();
            return;
        }

        SetCanvasGroupVisible(questCardGroup, true);
        SetCanvasGroupVisible(penaltyGroup, true);

        if (questNameText != null)
        {
            questNameText.text = quest.questName;
        }

        if (descriptionText != null)
        {
            descriptionText.text = $"{quest.description}\n성공 시: {questManager.CurrentReward.Description}";
        }

        if (penaltyText != null)
        {
            penaltyText.text = FormatPenalty(quest);
        }

        HandleQuestProgressChanged(0, quest.targetAmount);
        UpdateTime(quest.timeLimit, quest.timeLimit);
    }

    private void HandleQuestProgressChanged(int current, int target)
    {
        if (progressText != null)
        {
            progressText.text = string.Format("{0} / {1}", current, target);
        }
    }

    private void UpdateTime(float remaining, float total)
    {
        if (timerText != null)
        {
            timerText.text = FormatTime(remaining);
        }

        if (timerFillImage != null)
        {
            timerFillImage.fillAmount = total > 0f ? Mathf.Clamp01(remaining / total) : 0f;
        }
    }

    private void HandleQuestSucceeded(QuestSO quest, QuestRewardResult result)
    {
        HideQuestCard();
        ShowResultToast(true, "퀘스트 성공", result.Description);
    }

    private void HandleQuestFailed(QuestSO quest, QuestPenaltyResult result)
    {
        HideQuestCard();
        ShowResultToast(false, "페널티 발생", FormatPenaltyResult(result));
    }

    private void ShowResultToast(bool success, string title, string message)
    {
        if (resultBackgroundImage != null)
        {
            resultBackgroundImage.color = success ? successColor : failureColor;
        }

        if (resultTitleText != null)
        {
            resultTitleText.text = title;
        }

        if (resultMessageText != null)
        {
            resultMessageText.text = message;
        }

        SetCanvasGroupVisible(resultToastGroup, true);

        if (hideResultRoutine != null)
        {
            StopCoroutine(hideResultRoutine);
        }

        hideResultRoutine = StartCoroutine(HideResultAfterDelay());
    }

    private IEnumerator HideResultAfterDelay()
    {
        yield return new WaitForSecondsRealtime(resultDuration);
        HideResultToast();
        hideResultRoutine = null;
    }

    private void HideQuestCard()
    {
        SetCanvasGroupVisible(questCardGroup, false);
        SetCanvasGroupVisible(penaltyGroup, false);
    }

    private void HideResultToast()
    {
        SetCanvasGroupVisible(resultToastGroup, false);
    }

    private static void SetCanvasGroupVisible(CanvasGroup group, bool visible)
    {
        if (group == null)
        {
            return;
        }

        group.alpha = visible ? 1f : 0f;
        group.interactable = false;
        group.blocksRaycasts = false;
    }

    private static string FormatPenalty(QuestSO quest)
    {
        if (quest.penaltyType == QuestPenaltyType.SkillRemove)
        {
            return "실패: 증강 스킬 중첩 -1";
        }

        int minPercent = Mathf.RoundToInt(quest.statReduceMin * 100f);
        int maxPercent = Mathf.RoundToInt(quest.statReduceMax * 100f);
        return string.Format("실패 시: 증강 스탯 {0}~{1}% 감소", minPercent, maxPercent);
    }

    private static string FormatPenaltyResult(QuestPenaltyResult result)
    {
        if (result.removedSkill != null)
        {
            return result.remainingSkillStacks > 0
                ? $"{result.removedSkill.GetDisplayName()} 중첩 -1 (남은 중첩 {result.remainingSkillStacks})"
                : $"증강 스킬 제거: {result.removedSkill.GetDisplayName()}";
        }

        if (result.reducedStat.HasValue)
        {
            StatReduceResult stat = result.reducedStat.Value;
            return string.Format("{0} -{1:0.#}", FormatStatType(stat.type), stat.amount);
        }

        return "잃을 증강이 없어 페널티 없음";
    }

    private static string FormatStatType(AugmentType type)
    {
        switch (type)
        {
            case AugmentType.AttackDamage:
                return "공격력";
            case AugmentType.HP:
                return "체력";
            case AugmentType.MoveSpeed:
                return "이동속도";
            case AugmentType.AttackSpeed:
                return "공격속도";
            case AugmentType.Defense:
                return "방어력";
            default:
                return type.ToString();
        }
    }

    private static string FormatTime(float seconds)
    {
        int clampedSeconds = Mathf.Max(0, Mathf.CeilToInt(seconds));
        int minutes = clampedSeconds / 60;
        int remainingSeconds = clampedSeconds % 60;
        return string.Format("{0:00}:{1:00}", minutes, remainingSeconds);
    }

    private void OnValidate()
    {
        resultDuration = Mathf.Max(0f, resultDuration);
    }
}
