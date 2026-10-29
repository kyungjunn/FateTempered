using System.Text;
using TMPro;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.UI;

// 복제 로비 데이터 표시와 로컬 참가자 요청 전달
public sealed class TeamLobbyUI : MonoBehaviour
{
    // 로컬 캐릭터 선택 표시 색상
    private static readonly Color SelectedChampionColor = new Color(0.12f, 0.35f, 0.58f, 1f);
    private static readonly Color UnselectedChampionColor = new Color(0.08f, 0.42f, 0.49f, 1f);

    // 로비·로딩 패널과 참가자·캐릭터·조작 요소 참조
    [SerializeField] private GameObject lobbyPanel;
    [SerializeField] private GameObject loadingPanel;
    [SerializeField] private TMP_Text roomTitleText;
    [SerializeField] private TMP_Text member0Text;
    [SerializeField] private TMP_Text member1Text;
    [SerializeField] private Image member0Portrait;
    [SerializeField] private Image member1Portrait;
    [SerializeField] private Sprite[] championPortraits = new Sprite[3];
    [SerializeField] private TMP_Text noticeText;
    [SerializeField] private TMP_Text statusText;
    [SerializeField] private Button swordButton;
    [SerializeField] private Button ninjaButton;
    [SerializeField] private Button wizardButton;
    [SerializeField] private Button readyButton;
    [SerializeField] private Button startButton;
    [SerializeField] private Button leaveButton;

    // 세션·복제 로비 구독 대상과 공지 표시용 재사용 버퍼
    private NetworkGameSession session;
    private NetworkLobbyState lobby;
    private readonly StringBuilder noticeBuilder = new StringBuilder(256);

    // 버튼·세션 구독과 초기 로비 표시
    private void OnEnable()
    {
        RegisterButtons();
        EnsureSessionBinding();
        Render();
    }

    // 버튼·세션·로비 구독 해제
    private void OnDisable()
    {
        UnregisterButtons();
        UnbindSession();
    }

    // 세션 교체 감지와 로비 화면 갱신
    private void Update()
    {
        EnsureSessionBinding();
        Render();
    }

    // 로비 조작 버튼 구독
    private void RegisterButtons()
    {
        AddListener(swordButton, HandleSelectSword);
        AddListener(ninjaButton, HandleSelectNinja);
        AddListener(wizardButton, HandleSelectWizard);
        AddListener(readyButton, HandleReadyToggle);
        AddListener(startButton, HandleStart);
        AddListener(leaveButton, HandleLeave);
    }

    // 로비 조작 버튼 구독 해제
    private void UnregisterButtons()
    {
        RemoveListener(swordButton, HandleSelectSword);
        RemoveListener(ninjaButton, HandleSelectNinja);
        RemoveListener(wizardButton, HandleSelectWizard);
        RemoveListener(readyButton, HandleReadyToggle);
        RemoveListener(startButton, HandleStart);
        RemoveListener(leaveButton, HandleLeave);
    }

    // 유효한 버튼의 클릭 처리 등록
    private static void AddListener(Button button, UnityEngine.Events.UnityAction listener)
    {
        if (button != null)
            button.onClick.AddListener(listener);
    }

    // 유효한 버튼의 클릭 처리 제거
    private static void RemoveListener(Button button, UnityEngine.Events.UnityAction listener)
    {
        if (button != null)
            button.onClick.RemoveListener(listener);
    }

    // 현재 세션과 해당 세션의 복제 로비 재연결
    private void EnsureSessionBinding()
    {
        NetworkGameSession currentSession = NetworkGameSession.Instance;
        if (session != currentSession)
        {
            UnbindSession();
            session = currentSession;
            if (session != null)
                session.Changed += HandleSessionChanged;
        }

        BindLobby(session != null ? session.Lobby : null);
    }

    // 세션과 종속 로비 구독 해제
    private void UnbindSession()
    {
        if (session != null)
            session.Changed -= HandleSessionChanged;
        session = null;
        BindLobby(null);
    }

    // 로비 교체 시 상태·복제 공지 목록 구독 이전
    private void BindLobby(NetworkLobbyState currentLobby)
    {
        if (lobby == currentLobby)
            return;

        if (lobby != null)
        {
            lobby.Changed -= HandleLobbyChanged;
            lobby.Notices.OnListChanged -= HandleNoticesChanged;
        }

        lobby = currentLobby;
        if (lobby != null)
        {
            lobby.Changed += HandleLobbyChanged;
            lobby.Notices.OnListChanged += HandleNoticesChanged;
        }
    }

    // 현재 세션의 로비 참조 교체 반영
    private void HandleSessionChanged(NetworkGameSession changedSession)
    {
        if (changedSession != session)
            return;

        BindLobby(session.Lobby);
        Render();
    }

    // 현재 로비 상태 변경의 화면 반영
    private void HandleLobbyChanged(NetworkLobbyState changedLobby)
    {
        if (changedLobby == lobby)
            Render();
    }

    // 복제 공지 목록 변경의 화면 반영
    private void HandleNoticesChanged(NetworkListEvent<NetworkLobbyNotice> changeEvent)
    {
        Render();
    }

    // 검사 캐릭터 선택 요청
    private void HandleSelectSword()
    {
        SelectChampion(PlayerCharacterId.Sword);
    }

    // 닌자 캐릭터 선택 요청
    private void HandleSelectNinja()
    {
        SelectChampion(PlayerCharacterId.Ninja);
    }

    // 마법사 캐릭터 선택 요청
    private void HandleSelectWizard()
    {
        SelectChampion(PlayerCharacterId.Wizard);
    }

    // 선택 가능한 로컬 참가자의 캐릭터 변경 요청 전달
    private void SelectChampion(PlayerCharacterId champion)
    {
        if (session == null || session.State != NetworkGameSessionState.Lobby || !HasSelectableLocalMember())
            return;

        session.SelectChampion(champion);
    }

    // 방장이 아닌 로컬 참가자의 복제 준비 상태 반전 요청
    private void HandleReadyToggle()
    {
        if (session == null || session.State != NetworkGameSessionState.Lobby ||
            !TryGetLocalMember(out NetworkLobbyMemberSnapshot member) || member.IsHost)
        {
            return;
        }

        session.SetReady(!member.Ready);
    }

    // 세션 시작 조건 충족 시 시작 요청
    private void HandleStart()
    {
        if (session == null || !session.CanStart)
            return;

        session.RequestStart();
    }

    // 로비·로딩·접속 진행 중 세션 퇴장 요청
    private void HandleLeave()
    {
        if (session == null || (session.State != NetworkGameSessionState.Lobby &&
            session.State != NetworkGameSessionState.LoadingMatch &&
            session.State != NetworkGameSessionState.Creating && session.State != NetworkGameSessionState.Joining))
            return;

        session.Leave();
    }

    // 복제 스냅샷 기반 표시와 로컬 참가자 권한별 버튼 구성
    private void Render()
    {
        NetworkLobbySnapshot snapshot = lobby != null ? lobby.Snapshot : default;
        NetworkLobbyPhase phase = lobby != null ? snapshot.Phase : NetworkLobbyPhase.Stopping;
        NetworkGameSessionState state = session != null ? session.State : NetworkGameSessionState.Entry;
        bool lobbyVisible = session != null && lobby != null && state == NetworkGameSessionState.Lobby &&
                            phase == NetworkLobbyPhase.Lobby;
        bool loadingVisible = session != null &&
                              (state == NetworkGameSessionState.LoadingMatch || state == NetworkGameSessionState.Stopping ||
                               state == NetworkGameSessionState.ReturningToLobby ||
                               phase == NetworkLobbyPhase.LoadingMatch || phase == NetworkLobbyPhase.Stopping);

        SetPanel(lobbyPanel, lobbyVisible);
        SetPanel(loadingPanel, loadingVisible);
        // 숨겨진 로비 화면의 게임 화면 가림·입력 차단 방지
        Canvas canvas = GetComponent<Canvas>() ?? GetComponentInParent<Canvas>();
        bool overlayVisible = lobbyVisible || loadingVisible;
        if (canvas != null)
            canvas.enabled = overlayVisible;
        GraphicRaycaster raycaster = canvas != null ? canvas.GetComponent<GraphicRaycaster>() : null;
        if (raycaster != null)
            raycaster.enabled = overlayVisible;

        if (session != null && state != NetworkGameSessionState.Running)
        {
            string status = string.IsNullOrEmpty(session.Error) ? session.Status : session.Error;
            SetText(statusText, status);
        }
        else
        {
            SetText(statusText, string.Empty);
        }

        if (!lobbyVisible)
        {
            SetText(roomTitleText, string.Empty);
            SetText(member0Text, string.Empty);
            SetText(member1Text, string.Empty);
            SetText(noticeText, string.Empty);
            SetLobbyButtons(false, false, default);
            bool canLeave = session != null && (state == NetworkGameSessionState.LoadingMatch ||
                state == NetworkGameSessionState.Creating || state == NetworkGameSessionState.Joining);
            SetButtonVisible(leaveButton, canLeave);
            SetInteractable(leaveButton, canLeave);
            return;
        }

        SetText(roomTitleText, snapshot.RoomName);
        SetText(member0Text, FormatMember(snapshot.Member0, "플레이어 1"));
        SetText(member1Text, FormatMember(snapshot.Member1, "플레이어 2"));
        UpdatePortrait(member0Portrait, snapshot.Member0);
        UpdatePortrait(member1Portrait, snapshot.Member1);
        RenderNotices(snapshot);

        // 복제 데이터 직접 수정 없이 로컬 참가자 상태로 조작 가능 여부 결정
        bool hasLocalMember = TryGetLocalMember(out NetworkLobbyMemberSnapshot localMember);
        bool canChooseChampion = hasLocalMember && localMember.Connected && localMember.Synchronized;
        SetButtonVisible(swordButton, true);
        SetButtonVisible(ninjaButton, true);
        SetButtonVisible(wizardButton, true);
        bool isGuest = hasLocalMember && !localMember.IsHost;
        bool hasValidChampion = hasLocalMember && IsValidChampion(localMember.Champion);
        SetChampionButton(swordButton, PlayerCharacterId.Sword, canChooseChampion,
            hasLocalMember && localMember.Champion == (int)PlayerCharacterId.Sword, "Sword");
        SetChampionButton(ninjaButton, PlayerCharacterId.Ninja, canChooseChampion,
            hasLocalMember && localMember.Champion == (int)PlayerCharacterId.Ninja, "Ninja");
        SetChampionButton(wizardButton, PlayerCharacterId.Wizard, canChooseChampion,
            hasLocalMember && localMember.Champion == (int)PlayerCharacterId.Wizard, "Wizard");

        SetButtonVisible(readyButton, isGuest);
        SetInteractable(readyButton, isGuest && (localMember.Ready || hasValidChampion));
        SetButtonLabel(readyButton, localMember.Ready ? "준비 해제" : "준비");
        SetButtonVisible(startButton, hasLocalMember && localMember.IsHost);
        SetInteractable(startButton, hasLocalMember && localMember.IsHost && session.CanStart);
        SetButtonVisible(leaveButton, true);
        SetInteractable(leaveButton, true);
    }

    // 로비 비표시 구간의 캐릭터·준비·시작 버튼 초기화
    private void SetLobbyButtons(bool visible, bool hasLocalMember, NetworkLobbyMemberSnapshot member)
    {
        SetChampionButton(swordButton, PlayerCharacterId.Sword, false, false, "Sword");
        SetChampionButton(ninjaButton, PlayerCharacterId.Ninja, false, false, "Ninja");
        SetChampionButton(wizardButton, PlayerCharacterId.Wizard, false, false, "Wizard");
        SetButtonVisible(swordButton, visible && hasLocalMember);
        SetButtonVisible(ninjaButton, visible && hasLocalMember);
        SetButtonVisible(wizardButton, visible && hasLocalMember);
        SetButtonVisible(readyButton, visible && hasLocalMember && !member.IsHost);
        SetInteractable(readyButton, false);
        SetButtonVisible(startButton, visible && hasLocalMember && member.IsHost);
        SetInteractable(startButton, false);
    }

    // 현재 방 인스턴스에 속한 복제 참가·퇴장 공지만 표시
    private void RenderNotices(NetworkLobbySnapshot snapshot)
    {
        noticeBuilder.Length = 0;
        if (lobby != null)
        {
            var notices = lobby.Notices;
            for (int index = 0; index < notices.Count; index++)
            {
                NetworkLobbyNotice notice = notices[index];
                if (notice.RoomInstanceId != snapshot.RoomInstanceId)
                    continue;

                if (noticeBuilder.Length > 0)
                    noticeBuilder.Append('\n');
                noticeBuilder.Append('#').Append(notice.Sequence).Append(' ')
                    .Append(notice.DisplayName)
                    .Append(notice.Kind == NetworkLobbyNoticeKind.Joined ? " 참가" : " 퇴장");
            }
        }

        SetText(noticeText, noticeBuilder.Length > 0 ? noticeBuilder.ToString() : "최근 기록이 없습니다.");
    }

    // 연결·동기화를 마친 로컬 참가자 확인
    private bool HasSelectableLocalMember()
    {
        return TryGetLocalMember(out NetworkLobbyMemberSnapshot member) && member.Connected && member.Synchronized;
    }

    // 로컬 클라이언트 식별자와 복제 참가자 슬롯의 대응 조회
    private bool TryGetLocalMember(out NetworkLobbyMemberSnapshot member)
    {
        member = default;
        if (session == null || lobby == null)
            return false;

        NetworkManager manager = session.NetworkManager;
        if (manager == null)
            return false;

        NetworkLobbySnapshot snapshot = lobby.Snapshot;
        ulong localClientId = manager.LocalClientId;
        if (snapshot.Member0.Occupied && snapshot.Member0.ClientId == localClientId)
        {
            member = snapshot.Member0;
            return true;
        }
        if (snapshot.Member1.Occupied && snapshot.Member1.ClientId == localClientId)
        {
            member = snapshot.Member1;
            return true;
        }

        return false;
    }

    // 점유·캐릭터 유효성에 따른 초상화 표시
    private void UpdatePortrait(Image image, NetworkLobbyMemberSnapshot member)
    {
        if (image == null) return;
        Sprite portrait = member.Occupied && IsValidChampion(member.Champion) &&
            member.Champion < championPortraits.Length ? championPortraits[member.Champion] : null;
        image.sprite = portrait;
        image.enabled = portrait != null;
    }

    // 참가자 역할·캐릭터·준비·연결 상태 문구 구성
    private static string FormatMember(NetworkLobbyMemberSnapshot member, string emptyLabel)
    {
        if (!member.Occupied)
            return emptyLabel + " · 대기 중";

        string role = member.IsHost ? "방장" : "팀원";
        string champion = IsValidChampion(member.Champion)
            ? ((PlayerCharacterId)member.Champion).ToString()
            : "캐릭터 미선택";
        string connection = !member.Connected ? "연결 중" :
            !member.Synchronized ? "동기화 중" : "연결 완료";
        string readiness = member.IsHost ? "준비 불필요" : member.Ready ? "준비 완료" : "미준비";
        return role + " · " + member.DisplayName + "\n" + champion + " · " + readiness + " · " + connection;
    }

    // 지원 캐릭터 식별자 범위 확인
    private static bool IsValidChampion(int champion)
    {
        return champion >= (int)PlayerCharacterId.Sword && champion <= (int)PlayerCharacterId.Wizard;
    }

    // 캐릭터 버튼의 선택 문구·조작 가능 상태·색상 적용
    private static void SetChampionButton(Button button, PlayerCharacterId champion, bool interactable,
        bool selected, string label)
    {
        if (button == null)
            return;

        SetButtonLabel(button, selected ? label + " · 선택" : label);
        SetInteractable(button, interactable);

        ColorBlock colors = button.colors;
        colors.normalColor = Color.white;
        colors.highlightedColor = new Color(0.85f, 0.95f, 1f, 1f);
        colors.pressedColor = new Color(0.7f, 0.8f, 0.9f, 1f);
        colors.disabledColor = new Color(0.5f, 0.5f, 0.5f, 1f);
        button.colors = colors;
        if (button.targetGraphic != null)
            button.targetGraphic.color = selected ? SelectedChampionColor : UnselectedChampionColor;
    }

    // 버튼 하위 문구 갱신
    private static void SetButtonLabel(Button button, string label)
    {
        if (button == null)
            return;

        TMP_Text text = button.GetComponentInChildren<TMP_Text>(true);
        SetText(text, label);
    }

    // 패널 표시 상태 적용
    private static void SetPanel(GameObject panel, bool visible)
    {
        SetActiveIfChanged(panel, visible);
    }

    // 버튼 오브젝트 표시 상태 적용
    private static void SetButtonVisible(Button button, bool visible)
    {
        if (button != null)
            SetActiveIfChanged(button.gameObject, visible);
    }

    // 버튼 조작 가능 상태 적용
    private static void SetInteractable(Button button, bool interactable)
    {
        if (button != null)
            button.interactable = interactable;
    }

    // 활성 상태 변경 시에만 오브젝트 전환
    private static void SetActiveIfChanged(GameObject target, bool active)
    {
        if (target != null && target.activeSelf != active)
            target.SetActive(active);
    }

    // 서식 태그 해석 없는 문구 표시
    private static void SetText(TMP_Text label, string value)
    {
        if (label == null)
            return;

        label.richText = false;
        label.text = value ?? string.Empty;
    }
}
