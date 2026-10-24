using UnityEngine;

// 선택권 큐·패널. 세션 종료 없음.
public class UIManager : MonoBehaviour
{
    public static UIManager Instance;

    [Header("Panels")]
    public AugmentPanelUI augmentPanelUI;
    public StatsPanelUI statsPanelUI;
    public LoadingPanelUI loadingPanelUI;

    private int pendingTickets;
    private bool isAugmentPanelOpen;
    private int nextTicketId = 1;
    private int currentTicketId;

    private void Awake()
    {
        if (Instance == null)
        {
            Instance = this;
        }
        else
        {
            // 중복 거부
            enabled = false;
            Destroy(gameObject);
        }
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    public void ShowLoadingUI(bool isShow)
    {
        if (loadingPanelUI == null)
        {
            return;
        }

        if (isShow)
        {
            loadingPanelUI.ResetLoadingUI();
            loadingPanelUI.gameObject.SetActive(true);
        }
        else
        {
            loadingPanelUI.gameObject.SetActive(false);
        }
    }

    public void RequestAugmentChoice()
    {
        // 비활성 세션: 티켓 거부
        if (GameManager.Instance == null || !GameManager.Instance.IsActiveSession) return;
        pendingTickets++;
        if (!isAugmentPanelOpen)
        {
            ShowNextTicket();
        }
    }

    public void CompleteCurrentTicket()
    {
        if (pendingTickets > 0)
        {
            pendingTickets--;
        }

        if (pendingTickets > 0)
        {
            ShowNextTicket();
            return;
        }

        ClosePanelAndResume();
    }

    // 선택권·패널 무효
    public void ClearSession(bool resume)
    {
        pendingTickets = 0;
        currentTicketId = 0;
        isAugmentPanelOpen = false;

        if (augmentPanelUI != null)
        {
            augmentPanelUI.Invalidate();
            augmentPanelUI.gameObject.SetActive(false);
        }

        if (resume && GameManager.Instance != null)
        {
            GameManager.Instance.ResumeGame();
        }
    }

    private void ShowNextTicket()
    {
        if (augmentPanelUI == null || AugmentManager.Instance == null)
        {
            ClosePanelAndResume();
            return;
        }

        isAugmentPanelOpen = true;
        currentTicketId = nextTicketId++;
        int sessionId = GameManager.Instance?.SessionId ?? 0;
        var options = AugmentManager.Instance.GetRandomAugments();
        augmentPanelUI.gameObject.SetActive(true);

        if (options.Count == 0)
        {
            augmentPanelUI.ShowExhausted("선택할 증강이 없습니다. 이번 선택을 건너뛰려면 확인하세요.", sessionId, currentTicketId);
        }
        else
        {
            augmentPanelUI.Setup(options, sessionId, currentTicketId);
        }

        if (GameManager.Instance != null)
        {
            GameManager.Instance.PauseGame();
        }
    }

    private void ClosePanelAndResume()
    {
        isAugmentPanelOpen = false;
        currentTicketId = 0;

        if (augmentPanelUI != null)
        {
            augmentPanelUI.Invalidate();
            augmentPanelUI.gameObject.SetActive(false);
        }

        if (GameManager.Instance != null)
        {
            GameManager.Instance.ResumeGame();
        }
    }
}
