using System.Text;
using TMPro;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.UI;

public sealed class TeamLobbyUI : MonoBehaviour
{
    private static readonly Color SelectedChampionColor = new Color(0.12f, 0.35f, 0.58f, 1f);
    private static readonly Color UnselectedChampionColor = new Color(0.08f, 0.42f, 0.49f, 1f);

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

    private NetworkGameSession session;
    private NetworkLobbyState lobby;
    private readonly StringBuilder noticeBuilder = new StringBuilder(256);

    private void OnEnable()
    {
        RegisterButtons();
        EnsureSessionBinding();
        Render();
    }

    private void OnDisable()
    {
        UnregisterButtons();
        UnbindSession();
    }

    private void Update()
    {
        EnsureSessionBinding();
        Render();
    }

    private void RegisterButtons()
    {
        AddListener(swordButton, HandleSelectSword);
        AddListener(ninjaButton, HandleSelectNinja);
        AddListener(wizardButton, HandleSelectWizard);
        AddListener(readyButton, HandleReadyToggle);
        AddListener(startButton, HandleStart);
        AddListener(leaveButton, HandleLeave);
    }

    private void UnregisterButtons()
    {
        RemoveListener(swordButton, HandleSelectSword);
        RemoveListener(ninjaButton, HandleSelectNinja);
        RemoveListener(wizardButton, HandleSelectWizard);
        RemoveListener(readyButton, HandleReadyToggle);
        RemoveListener(startButton, HandleStart);
        RemoveListener(leaveButton, HandleLeave);
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

    private void UnbindSession()
    {
        if (session != null)
            session.Changed -= HandleSessionChanged;
        session = null;
        BindLobby(null);
    }

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

    private void HandleSessionChanged(NetworkGameSession changedSession)
    {
        if (changedSession != session)
            return;

        BindLobby(session.Lobby);
        Render();
    }

    private void HandleLobbyChanged(NetworkLobbyState changedLobby)
    {
        if (changedLobby == lobby)
            Render();
    }

    private void HandleNoticesChanged(NetworkListEvent<NetworkLobbyNotice> changeEvent)
    {
        Render();
    }

    private void HandleSelectSword()
    {
        SelectChampion(PlayerCharacterId.Sword);
    }

    private void HandleSelectNinja()
    {
        SelectChampion(PlayerCharacterId.Ninja);
    }

    private void HandleSelectWizard()
    {
        SelectChampion(PlayerCharacterId.Wizard);
    }

    private void SelectChampion(PlayerCharacterId champion)
    {
        if (session == null || session.State != NetworkGameSessionState.Lobby || !HasSelectableLocalMember())
            return;

        session.SelectChampion(champion);
    }

    private void HandleReadyToggle()
    {
        if (session == null || session.State != NetworkGameSessionState.Lobby ||
            !TryGetLocalMember(out NetworkLobbyMemberSnapshot member) || member.IsHost)
        {
            return;
        }

        session.SetReady(!member.Ready);
    }

    private void HandleStart()
    {
        if (session == null || !session.CanStart)
            return;

        session.RequestStart();
    }

    private void HandleLeave()
    {
        if (session == null || (session.State != NetworkGameSessionState.Lobby &&
            session.State != NetworkGameSessionState.LoadingMatch &&
            session.State != NetworkGameSessionState.Creating && session.State != NetworkGameSessionState.Joining))
            return;

        session.Leave();
    }

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

    private bool HasSelectableLocalMember()
    {
        return TryGetLocalMember(out NetworkLobbyMemberSnapshot member) && member.Connected && member.Synchronized;
    }

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

    private void UpdatePortrait(Image image, NetworkLobbyMemberSnapshot member)
    {
        if (image == null) return;
        Sprite portrait = member.Occupied && IsValidChampion(member.Champion) &&
            member.Champion < championPortraits.Length ? championPortraits[member.Champion] : null;
        image.sprite = portrait;
        image.enabled = portrait != null;
    }

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

    private static bool IsValidChampion(int champion)
    {
        return champion >= (int)PlayerCharacterId.Sword && champion <= (int)PlayerCharacterId.Wizard;
    }

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

    private static void SetButtonLabel(Button button, string label)
    {
        if (button == null)
            return;

        TMP_Text text = button.GetComponentInChildren<TMP_Text>(true);
        SetText(text, label);
    }

    private static void SetPanel(GameObject panel, bool visible)
    {
        SetActiveIfChanged(panel, visible);
    }

    private static void SetButtonVisible(Button button, bool visible)
    {
        if (button != null)
            SetActiveIfChanged(button.gameObject, visible);
    }

    private static void SetInteractable(Button button, bool interactable)
    {
        if (button != null)
            button.interactable = interactable;
    }

    private static void SetActiveIfChanged(GameObject target, bool active)
    {
        if (target != null && target.activeSelf != active)
            target.SetActive(active);
    }

    private static void SetText(TMP_Text label, string value)
    {
        if (label == null)
            return;

        label.richText = false;
        label.text = value ?? string.Empty;
    }
}
