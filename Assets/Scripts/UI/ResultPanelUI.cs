using System.Globalization;
using TMPro;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.UI;

public sealed class ResultPanelUI : MonoBehaviour
{
    [Header("결과 패널")]
    [SerializeField] private GameObject resultPanel;
    [SerializeField] private TMP_Text resultStatusText;
    [SerializeField] private TMP_Text matchDurationText;
    [SerializeField] private PlayerResultUI[] playerColumns = new PlayerResultUI[2];

    [Header("네트워크 버튼")]
    [SerializeField] private Button returnToLobbyButton;
    [SerializeField] private Button leaveNetworkButton;

    [Header("오프라인 버튼")]
    [SerializeField] private Button restartOfflineButton;
    [SerializeField] private Button returnOfflineToMenuButton;

    // 결과 상태·로비 복귀 권한 조회 대상
    private NetworkGameSession session;
    private bool resultVisible;

    private void Awake()
    {
        SetResultPanelActive(false);
    }

    private void OnEnable()
    {
        GameResult.Changed += HandleResultChanged;
        RegisterButtons();
        EnsureSessionBinding();
        Render();
    }

    private void OnDisable()
    {
        GameResult.Changed -= HandleResultChanged;
        UnregisterButtons();
        UnbindSession();
    }

    private void Update()
    {
        // 영속 캔버스 선활성화 대비·세션 생성 후 연결
        EnsureSessionBinding();
    }

    private void RegisterButtons()
    {
        AddListener(returnToLobbyButton, HandleReturnToLobby);
        AddListener(leaveNetworkButton, HandleLeaveNetwork);
        AddListener(restartOfflineButton, HandleRestartOffline);
        AddListener(returnOfflineToMenuButton, HandleReturnOfflineToMenu);
    }

    private void UnregisterButtons()
    {
        RemoveListener(returnToLobbyButton, HandleReturnToLobby);
        RemoveListener(leaveNetworkButton, HandleLeaveNetwork);
        RemoveListener(restartOfflineButton, HandleRestartOffline);
        RemoveListener(returnOfflineToMenuButton, HandleReturnOfflineToMenu);
    }

    // 세션 교체 감지·상태 변경 구독 갱신
    private void EnsureSessionBinding()
    {
        NetworkGameSession currentSession = NetworkGameSession.Instance;
        if (ReferenceEquals(session, currentSession))
            return;

        UnbindSession();
        session = currentSession;
        if (session != null)
            session.Changed += HandleSessionChanged;
        Render();
    }

    // 이전 네트워크 세션 구독 해제
    private void UnbindSession()
    {
        if (!ReferenceEquals(session, null))
            session.Changed -= HandleSessionChanged;
        session = null;
    }

    private void HandleResultChanged()
    {
        Render();
    }

    // 연결된 세션의 결과·복귀 상태 반영
    private void HandleSessionChanged(NetworkGameSession changedSession)
    {
        if (changedSession == session)
            Render();
    }

    private void Render()
    {
        MatchResult? result = GameResult.Data;
        bool networkMatch = IsNetworkMatch();
        // 네트워크 종료 상태 우선·결과 미수신 대기 화면 허용
        bool networkEnded = networkMatch && session != null && session.State == NetworkGameSessionState.Ended;
        bool visible = networkMatch ? networkEnded : result.HasValue;
        SetResultPanelActive(visible);
        if (!visible)
        {
            RenderPlayers(null);
            SetActions(false, false, false, false);
            SetText(resultStatusText, string.Empty);
            SetText(matchDurationText, string.Empty);
            return;
        }

        if (!resultVisible)
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }
        resultVisible = true;

        bool hasResult = result.HasValue;
        if (hasResult)
        {
            SetText(matchDurationText, "경기 시간: " + FormatDuration(result.Value.MatchSeconds));
            RenderPlayers(result.Value);
        }
        else
        {
            SetText(matchDurationText, string.Empty);
            RenderPlayers(null);
        }

        if (networkMatch)
        {
            // 방장 전용 로비 복귀·전원 공통 나가기·오프라인 버튼 제외
            bool isHost = session != null && session.IsHost;
            bool canReturn = hasResult && isHost && session.CanReturnToLobby;
            SetActions(isHost, true, false, false);
            SetButtonInteractable(returnToLobbyButton, canReturn);
            SetButtonText(returnToLobbyButton, canReturn ? "로비로 돌아가기" : "로비 복귀 준비 중");
            SetButtonText(leaveNetworkButton, "나가기");

            if (!hasResult)
                SetText(resultStatusText, "경기 결과를 기다리는 중입니다.");
            else if (!isHost)
                SetText(resultStatusText, "방장이 로비로 돌아가기를 기다리는 중입니다.");
            else if (!canReturn)
                SetText(resultStatusText, "로비 복귀를 준비하고 있습니다.");
            else
                SetText(resultStatusText, string.Empty);
        }
        else
        {
            SetActions(false, false, true, true);
            SetText(resultStatusText, string.Empty);
            SetButtonText(restartOfflineButton, "다시 시작");
            SetButtonText(returnOfflineToMenuButton, "메뉴로 돌아가기");
        }
    }

    // 로비·전투·종료 전환 구간의 네트워크 결과 분기 유지
    private bool IsNetworkMatch()
    {
        if (NetworkGameSession.IsNetworkSessionActive)
            return true;
        if (session == null)
            return false;

        switch (session.State)
        {
            case NetworkGameSessionState.Lobby:
            case NetworkGameSessionState.LoadingMatch:
            case NetworkGameSessionState.Running:
            case NetworkGameSessionState.Ended:
            case NetworkGameSessionState.ReturningToLobby:
            case NetworkGameSessionState.Stopping:
                return true;
            default:
                return false;
        }
    }

    private void RenderPlayers(MatchResult? matchResult)
    {
        if (playerColumns == null)
            return;

        PlayerResult first = default;
        PlayerResult second = default;
        int count = 0;
        if (matchResult.HasValue && matchResult.Value.Players != null)
        {
            PlayerResult[] players = matchResult.Value.Players;
            count = Mathf.Min(2, players.Length);
            if (count > 0)
                first = players[0];
            if (count > 1)
                second = players[1];

            // 로컬 참가자 결과의 첫 번째 열 우선 배치
            if (count > 1 && TryGetLocalClientId(out ulong localClientId) &&
                second.ClientId == localClientId && first.ClientId != localClientId)
            {
                PlayerResult swap = first;
                first = second;
                second = swap;
            }
        }

        for (int i = 0; i < playerColumns.Length; i++)
        {
            PlayerResultUI column = playerColumns[i];
            if (column == null)
                continue;
            if (i == 0 && count > 0)
                column.Display(first);
            else if (i == 1 && count > 1)
                column.Display(second);
            else
                column.SetVisible(false);
        }
    }

    // 결과 열 정렬용 로컬 네트워크 참가자 ID
    private bool TryGetLocalClientId(out ulong clientId)
    {
        NetworkManager manager = session != null ? session.NetworkManager : NetworkManager.Singleton;
        if (manager != null)
        {
            clientId = manager.LocalClientId;
            return true;
        }

        clientId = 0;
        return false;
    }

    private void SetResultPanelActive(bool active)
    {
        if (resultPanel != null && resultPanel.activeSelf != active)
            resultPanel.SetActive(active);
        if (!active)
            resultVisible = false;
    }

    private void SetActions(bool showReturnToLobby, bool showLeaveNetwork,
        bool showRestartOffline, bool showReturnOfflineToMenu)
    {
        SetButtonVisible(returnToLobbyButton, showReturnToLobby);
        SetButtonVisible(leaveNetworkButton, showLeaveNetwork);
        SetButtonVisible(restartOfflineButton, showRestartOffline);
        SetButtonVisible(returnOfflineToMenuButton, showReturnOfflineToMenu);
    }

    // 세션 복귀 가능 조건 확인·로비 복귀 요청
    private void HandleReturnToLobby()
    {
        if (session != null && session.CanReturnToLobby)
            session.ReturnToLobby();
    }

    // 네트워크 세션 이탈 요청
    private void HandleLeaveNetwork()
    {
        if (session != null)
            session.Leave();
    }

    private void HandleRestartOffline()
    {
        GameManager.Instance?.RestartOfflineMatch();
    }

    private void HandleReturnOfflineToMenu()
    {
        GameManager.Instance?.ReturnOfflineToMenu();
    }

    private static void AddListener(Button button, UnityEngine.Events.UnityAction listener)
    {
        if (button != null)
            button.onClick.AddListener(listener);
    }

    private static void RemoveListener(Button button, UnityEngine.Events.UnityAction listener)
    {
        if (button != null)
            button.onClick.RemoveListener(listener);
    }

    private static void SetButtonVisible(Button button, bool visible)
    {
        if (button != null && button.gameObject.activeSelf != visible)
            button.gameObject.SetActive(visible);
    }

    private static void SetButtonInteractable(Button button, bool interactable)
    {
        if (button != null)
            button.interactable = interactable;
    }

    private static void SetButtonText(Button button, string value)
    {
        if (button == null)
            return;
        TMP_Text label = button.GetComponentInChildren<TMP_Text>(true);
        SetText(label, value);
    }

    private static void SetText(TMP_Text label, string value)
    {
        if (label != null)
            label.text = value;
    }

    private static string FormatDuration(float seconds)
    {
        long totalSeconds = System.Math.Max(0L, (long)System.Math.Floor(seconds));
        return (totalSeconds / 60L).ToString("00", CultureInfo.InvariantCulture) + ":" +
               (totalSeconds % 60L).ToString("00", CultureInfo.InvariantCulture);
    }
}
