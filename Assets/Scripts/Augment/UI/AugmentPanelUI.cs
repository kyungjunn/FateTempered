using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

// 증강 선택 패널.
public class AugmentPanelUI : MonoBehaviour
{
    // 패널 진행 상태
    private enum PanelState
    {
        Closed,
        Ready,
        Loading,
        Failure,
        Exhausted
    }

    // 선택 방지 시간
    [SerializeField] private float selectionDelay = 0.35f;
    // 상태 안내
    [SerializeField] private TextMeshProUGUI statusText;
    [SerializeField] private TMP_FontAsset statusFont;
    // 선택 건너뛰기
    [SerializeField] private Button skipButton;

    // 증강 선택지
    public List<AugmentOptionUI> optionUIs;

    // 현재 선택 정보
    private PanelState state = PanelState.Closed;
    private Coroutine enableSelectionRoutine;
    private Coroutine applyRoutine;
    private int ticketId;
    private int sessionId;
    private bool consumed;

    public bool IsBusy => state == PanelState.Loading;

    private void Awake()
    {
        EnsureStatusControls();
        if (skipButton != null)
        {
            skipButton.onClick.AddListener(SkipCurrentTicket);
        }
    }

    // 선택지 초기화
    public void Setup(List<AugmentSO> augments, int session, int ticket)
    {
        StopRoutines();
        sessionId = session;
        ticketId = ticket;
        consumed = false;
        state = PanelState.Ready;
        SetStatus(string.Empty);
        SetSkipVisible(false);
        ShowOptions(augments);
        enableSelectionRoutine = StartCoroutine(EnableSelectionAfterDelay());
    }

    // 선택지 소진 표시
    public void ShowExhausted(string message, int session, int ticket)
    {
        StopRoutines();
        sessionId = session;
        ticketId = ticket;
        consumed = false;
        state = PanelState.Exhausted;
        ShowOptions(null);
        SetStatus(message);
        SetSkipVisible(true);
        SetAllInteractable(false);
    }

    // 증강 선택
    public void Select(AugmentSO augment)
    {
        if (state != PanelState.Ready || augment == null || consumed)
        {
            return;
        }

        state = PanelState.Loading;
        SetAllInteractable(false);
        SetSkipVisible(false);
        SetStatus("스킬을 준비하는 중...");

        if (AudioManager.Instance != null)
        {
            AudioManager.Instance.PlayLevelUp();
        }

        applyRoutine = StartCoroutine(ApplySelected(augment));
    }

    // 선택 증강 적용
    private IEnumerator ApplySelected(AugmentSO augment)
    {
        AugmentApplyStatus status = AugmentApplyStatus.Failed;
        string error = "적용에 실패했습니다.";
        int expectedSession = sessionId;
        int expectedTicket = ticketId;

        yield return AugmentManager.Instance.ApplyAugmentRoutine(augment, expectedSession, expectedTicket, (result, message) =>
        {
            status = result;
            error = message;
        });

        if (sessionId != expectedSession || ticketId != expectedTicket)
        {
            yield break;
        }

        if (status == AugmentApplyStatus.Applied || status == AugmentApplyStatus.Stacked)
        {
            consumed = true;
            UIManager.Instance?.CompleteCurrentTicket();
            yield break;
        }

        if (status == AugmentApplyStatus.Stale)
        {
            yield break;
        }

        List<AugmentSO> keep = new List<AugmentSO>();
        if (status != AugmentApplyStatus.Failed)
        {
            CollectVisible(keep);
        }
        else
        {
            CollectVisibleExcept(keep, augment);
        }

        List<AugmentSO> next = AugmentManager.Instance.GetRandomAugments(keep);
        if (next.Count == 0)
        {
            ShowExhausted("선택할 증강이 없습니다. 이번 선택을 건너뛰려면 확인하세요.", sessionId, ticketId);
            yield break;
        }

        state = PanelState.Failure;
        SetStatus(error);
        ShowOptions(next);
        enableSelectionRoutine = StartCoroutine(EnableSelectionAfterDelay());
    }

    // 현재 선택 건너뛰기
    public void SkipCurrentTicket()
    {
        if (consumed || state != PanelState.Exhausted)
        {
            return;
        }

        consumed = true;
        UIManager.Instance?.CompleteCurrentTicket();
    }

    // 패널 상태 무효화
    public void Invalidate()
    {
        StopRoutines();
        consumed = true;
        state = PanelState.Closed;
        ShowOptions(null);
        SetStatus(string.Empty);
        SetSkipVisible(false);
    }

    // 지연 후 입력 허용
    private IEnumerator EnableSelectionAfterDelay()
    {
        yield return new WaitForSecondsRealtime(selectionDelay);
        enableSelectionRoutine = null;

        if (state == PanelState.Loading || consumed)
        {
            yield break;
        }

        state = PanelState.Ready;
        SetAllInteractable(true);
    }

    // 선택지 표시
    private void ShowOptions(List<AugmentSO> augments)
    {
        for (int i = 0; i < optionUIs.Count; i++)
        {
            optionUIs[i].SetInteractable(false);
            optionUIs[i].gameObject.SetActive(false);
            optionUIs[i].Clear();
        }

        if (augments == null)
        {
            return;
        }

        for (int i = 0; i < augments.Count && i < optionUIs.Count; i++)
        {
            optionUIs[i].gameObject.SetActive(true);
            optionUIs[i].Setup(augments[i]);
            optionUIs[i].SetInteractable(false);
        }
    }

    private void CollectVisible(List<AugmentSO> results)
    {
        for (int i = 0; i < optionUIs.Count; i++)
        {
            if (optionUIs[i].gameObject.activeSelf && optionUIs[i].CurrentAugment != null)
            {
                results.Add(optionUIs[i].CurrentAugment);
            }
        }
    }

    private void CollectVisibleExcept(List<AugmentSO> results, AugmentSO excluded)
    {
        for (int i = 0; i < optionUIs.Count; i++)
        {
            AugmentSO current = optionUIs[i].CurrentAugment;
            if (optionUIs[i].gameObject.activeSelf && current != null && current != excluded)
            {
                results.Add(current);
            }
        }
    }

    private void SetAllInteractable(bool interactable)
    {
        for (int i = 0; i < optionUIs.Count; i++)
        {
            if (optionUIs[i].gameObject.activeSelf)
            {
                optionUIs[i].SetInteractable(interactable);
            }
        }
    }

    private void SetStatus(string message)
    {
        if (statusText != null)
        {
            statusText.text = message;
            statusText.gameObject.SetActive(!string.IsNullOrEmpty(message));
        }
    }

    private void SetSkipVisible(bool visible)
    {
        if (skipButton != null)
        {
            skipButton.gameObject.SetActive(visible);
        }
    }

    // 실행 중 코루틴 정리
    private void StopRoutines()
    {
        if (enableSelectionRoutine != null)
        {
            StopCoroutine(enableSelectionRoutine);
            enableSelectionRoutine = null;
        }

        if (applyRoutine != null)
        {
            StopCoroutine(applyRoutine);
            applyRoutine = null;
        }
    }

    private void OnDisable()
    {
        StopRoutines();
    }

    private void OnValidate()
    {
        selectionDelay = Mathf.Max(0f, selectionDelay);
    }

    // 상태 UI 확보
    private void EnsureStatusControls()
    {
        if (statusText == null)
        {
            TextMeshProUGUI existing = GetComponentInChildren<TextMeshProUGUI>(true);
            if (existing != null && existing.name == "StatusText")
            {
                statusText = existing;
            }
            else
            {
                GameObject textObject = new GameObject("StatusText", typeof(RectTransform));
                textObject.transform.SetParent(transform, false);
                RectTransform rect = textObject.GetComponent<RectTransform>();
                rect.anchorMin = new Vector2(0.1f, 0.08f);
                rect.anchorMax = new Vector2(0.9f, 0.18f);
                rect.offsetMin = Vector2.zero;
                rect.offsetMax = Vector2.zero;
                statusText = textObject.AddComponent<TextMeshProUGUI>();
                statusText.alignment = TextAlignmentOptions.Center;
                statusText.fontSize = 24;
                statusText.color = Color.white;
            }
        }

        if (skipButton == null)
        {
            Button existingButton = GetComponentInChildren<Button>(true);
            if (existingButton != null && existingButton.name == "SkipButton")
            {
                skipButton = existingButton;
            }
            else
            {
                GameObject buttonObject = new GameObject("SkipButton", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button));
                buttonObject.transform.SetParent(transform, false);
                RectTransform rect = buttonObject.GetComponent<RectTransform>();
                rect.anchorMin = new Vector2(0.35f, 0.02f);
                rect.anchorMax = new Vector2(0.65f, 0.08f);
                rect.offsetMin = Vector2.zero;
                rect.offsetMax = Vector2.zero;
                skipButton = buttonObject.GetComponent<Button>();
                Image image = buttonObject.GetComponent<Image>();
                image.color = new Color(0.2f, 0.2f, 0.2f, 0.9f);
                GameObject labelObject = new GameObject("Text", typeof(RectTransform));
                labelObject.transform.SetParent(buttonObject.transform, false);
                TextMeshProUGUI label = labelObject.AddComponent<TextMeshProUGUI>();
                label.text = "이번 선택 건너뛰기";
                label.alignment = TextAlignmentOptions.Center;
                label.fontSize = 20;
                RectTransform labelRect = label.GetComponent<RectTransform>();
                labelRect.anchorMin = Vector2.zero;
                labelRect.anchorMax = Vector2.one;
                labelRect.offsetMin = Vector2.zero;
                labelRect.offsetMax = Vector2.zero;
            }
        }

        if (statusFont != null)
        {
            statusText.font = statusFont;
            TextMeshProUGUI skipLabel = skipButton.GetComponentInChildren<TextMeshProUGUI>(true);
            if (skipLabel != null)
            {
                skipLabel.font = statusFont;
            }
        }

        SetSkipVisible(false);
        SetStatus(string.Empty);
    }
}
