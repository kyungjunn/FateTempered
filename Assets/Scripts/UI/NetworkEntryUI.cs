using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

// 세션·방 탐색 연동과 로컬 진입·퇴장 화면 제어
public sealed class NetworkEntryUI : MonoBehaviour
{
    // 게임 씬 판별 기준과 방 목록 표시 상한
    private const string InGameSceneName = "InGame";
    private const int MaxRoomRows = 64;

    // 진입 화면 패널·입력·방 목록·버튼 참조
    [SerializeField] private GameObject startPanel;
    [SerializeField] private GameObject browserPanel;
    [SerializeField] private GameObject createRoomPanel;
    [SerializeField] private GameObject loadingPanel;
    [SerializeField] private GameObject leavePanel;
    [SerializeField] private GameObject entryCameraRoot;
    [SerializeField] private GameObject entryEventSystemRoot;
    [SerializeField] private TMP_InputField displayNameInput;
    [SerializeField] private TMP_InputField roomNameInput;
    [SerializeField] private TMP_Text statusText;
    [SerializeField] private TMP_Text errorText;
    [SerializeField] private Transform roomListContent;
    [SerializeField] private LanRoomRowUI roomRowTemplate;
    [SerializeField] private Button gameStartButton;
    [SerializeField] private Button saveProfileButton;
    [SerializeField] private Button refreshButton;
    [SerializeField] private Button openCreateRoomButton;
    [SerializeField] private Button createRoomConfirmButton;
    [SerializeField] private Button createRoomCancelButton;
    [SerializeField] private Button browserBackButton;
    [SerializeField] private Button pendingCancelButton;
    [SerializeField] private Button leaveButton;
    [SerializeField] private Button leaveConfirmButton;
    [SerializeField] private Button leaveCancelButton;

    // 재사용 방 행과 세션·탐색 서비스 구독 대상
    private readonly List<LanRoomRowUI> roomRows = new List<LanRoomRowUI>(8);
    private NetworkGameSession session;
    private LanRoomDiscovery discovery;
    // 복제 대상이 아닌 로컬 대화상자·입력 초기화·씬 상태
    private bool createDialogOpen;
    private bool leavePanelOpen;
    private bool profileInputInitialized;
    private bool entrySceneLoaded;
    // 메뉴 진입 전 커서 상태와 로컬 우선 안내 문구
    private CursorLockMode previousCursorLockState;
    private bool previousCursorVisible;
    private string localStatusOverride;
    private string localErrorOverride;
    // 메뉴 진입 당시 플레이어와 입력 컴포넌트별 복원 상태
    private GameObject menuPlayer;
    private Behaviour[] suspendedInputs;
    private bool[] suspendedInputStates;

    // 씬·버튼 구독과 초기 화면 구성
    private void OnEnable()
    {
        SceneManager.sceneLoaded += HandleSceneLoaded;
        SceneManager.sceneUnloaded += HandleSceneUnloaded;
        RegisterButtons();
        ConfigureInputText();
        RefreshEntryInfrastructure();
        EnsureSessionBinding();
        Render();
    }

    // 화면 비활성화 시 구독 해제와 메뉴 정리
    private void OnDisable()
    {
        SceneManager.sceneLoaded -= HandleSceneLoaded;
        SceneManager.sceneUnloaded -= HandleSceneUnloaded;
        UnregisterButtons();
        UnbindSession();
        CloseLeavePanel();
    }

    // 세션 교체 감지와 단축키·화면 갱신
    private void Update()
    {
        EnsureSessionBinding();
        HandleEscape();
        Render();
    }

    // 씬 로드 후 진입 기반 오브젝트 재평가
    private void HandleSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        RefreshEntryInfrastructure();
    }

    // 씬 해제 후 진입 기반 오브젝트 재평가
    private void HandleSceneUnloaded(Scene scene)
    {
        RefreshEntryInfrastructure();
    }

    // 게임 씬과 진입 화면의 카메라·이벤트 시스템 중복 방지
    private void RefreshEntryInfrastructure()
    {
        Scene inGameScene = SceneManager.GetSceneByName(InGameSceneName);
        entrySceneLoaded = inGameScene.IsValid() && inGameScene.isLoaded;
        SetActiveIfChanged(entryCameraRoot, !entrySceneLoaded);
        SetActiveIfChanged(entryEventSystemRoot, !entrySceneLoaded);
    }

    // 저장 이름의 최초 반영과 입력 서식 해석 차단
    private void ConfigureInputText()
    {
        if (displayNameInput != null)
        {
            if (!profileInputInitialized)
            {
                displayNameInput.text = LocalPlayerProfile.LoadDisplayName();
                profileInputInitialized = true;
            }
            SetInputRichText(displayNameInput);
        }

        SetInputRichText(roomNameInput);
    }

    // 입력 본문·안내 문구의 리치 텍스트 비활성화
    private static void SetInputRichText(TMP_InputField input)
    {
        if (input == null)
            return;

        if (input.textComponent != null)
            input.textComponent.richText = false;
        if (input.placeholder is TMP_Text placeholderText)
            placeholderText.richText = false;
    }

    // 화면 버튼 동작 구독
    private void RegisterButtons()
    {
        AddListener(gameStartButton, HandleGameStart);
        AddListener(saveProfileButton, HandleSaveProfile);
        AddListener(refreshButton, HandleRefresh);
        AddListener(openCreateRoomButton, HandleOpenCreateRoom);
        AddListener(createRoomConfirmButton, HandleConfirmCreateRoom);
        AddListener(createRoomCancelButton, HandleCancelCreateRoom);
        AddListener(browserBackButton, HandleBrowserBack);
        AddListener(pendingCancelButton, HandlePendingCancel);
        AddListener(leaveButton, OpenLeavePanel);
        AddListener(leaveConfirmButton, HandleLeaveMatch);
        AddListener(leaveCancelButton, HandleCancelLeave);
    }

    // 화면 버튼 동작 구독 해제
    private void UnregisterButtons()
    {
        RemoveListener(gameStartButton, HandleGameStart);
        RemoveListener(saveProfileButton, HandleSaveProfile);
        RemoveListener(refreshButton, HandleRefresh);
        RemoveListener(openCreateRoomButton, HandleOpenCreateRoom);
        RemoveListener(createRoomConfirmButton, HandleConfirmCreateRoom);
        RemoveListener(createRoomCancelButton, HandleCancelCreateRoom);
        RemoveListener(browserBackButton, HandleBrowserBack);
        RemoveListener(pendingCancelButton, HandlePendingCancel);
        RemoveListener(leaveButton, OpenLeavePanel);
        RemoveListener(leaveConfirmButton, HandleLeaveMatch);
        RemoveListener(leaveCancelButton, HandleCancelLeave);
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

    // 세션 교체 시 이전 구독 해제와 탐색 서비스 재연결
    private void EnsureSessionBinding()
    {
        NetworkGameSession currentSession = NetworkGameSession.Instance;
        if (session == currentSession)
        {
            BindDiscovery(session != null ? session.Discovery : null);
            return;
        }

        UnbindSession();
        session = currentSession;
        if (session != null)
            session.Changed += HandleSessionChanged;
        BindDiscovery(session != null ? session.Discovery : null);
        RefreshRoomRows();
    }

    // 세션·탐색 서비스 구독 일괄 해제
    private void UnbindSession()
    {
        if (session != null)
            session.Changed -= HandleSessionChanged;
        session = null;
        BindDiscovery(null);
    }

    // 탐색 서비스 교체 시 상태·방 목록 구독 이전
    private void BindDiscovery(LanRoomDiscovery currentDiscovery)
    {
        if (discovery == currentDiscovery)
            return;

        if (discovery != null)
        {
            discovery.StatusChanged -= HandleDiscoveryStatusChanged;
            discovery.RoomsChanged -= HandleRoomsChanged;
        }

        discovery = currentDiscovery;
        if (discovery != null)
        {
            discovery.StatusChanged += HandleDiscoveryStatusChanged;
            discovery.RoomsChanged += HandleRoomsChanged;
        }

        RefreshRoomRows();
    }

    // 세션 변경에 따른 로컬 문구 초기화와 탐색·화면 갱신
    private void HandleSessionChanged(NetworkGameSession changedSession)
    {
        if (changedSession != session)
            return;

        localStatusOverride = null;
        localErrorOverride = null;
        if (session.State != NetworkGameSessionState.Browsing)
            createDialogOpen = false;
        BindDiscovery(session.Discovery);
        RefreshRoomRows();
        Render();
    }

    // 탐색 상태 변경의 화면 반영
    private void HandleDiscoveryStatusChanged(LanRoomDiscoveryStatus changedStatus)
    {
        Render();
    }

    // 탐색 결과 변경의 방 목록 반영
    private void HandleRoomsChanged(IReadOnlyList<LanDiscoveredRoom> rooms)
    {
        RefreshRoomRows(rooms);
        Render();
    }

    // 표시 상한 내 방 행 재사용과 잉여 행 숨김
    private void RefreshRoomRows(IReadOnlyList<LanDiscoveredRoom> rooms = null)
    {
        if (discovery == null || roomListContent == null || roomRowTemplate == null)
        {
            ClearRoomRows();
            return;
        }

        if (rooms == null)
            rooms = discovery.Rooms;

        if (roomRowTemplate.gameObject.scene.IsValid() &&
            (roomRowTemplate.transform.parent == roomListContent || roomRowTemplate.transform.IsChildOf(roomListContent)))
        {
            roomRowTemplate.gameObject.SetActive(false);
        }

        int rowCount = Mathf.Min(rooms != null ? rooms.Count : 0, MaxRoomRows);
        while (roomRows.Count < rowCount)
        {
            LanRoomRowUI row = Instantiate(roomRowTemplate, roomListContent, false);
            row.Clear();
            roomRows.Add(row);
        }

        for (int index = 0; index < rowCount; index++)
            roomRows[index].Bind(rooms[index], HandleJoinRoom);
        for (int index = rowCount; index < roomRows.Count; index++)
            roomRows[index].Clear();
    }

    // 재사용 방 행의 바인딩 해제
    private void ClearRoomRows()
    {
        for (int index = 0; index < roomRows.Count; index++)
        {
            if (roomRows[index] != null)
                roomRows[index].Clear();
        }
    }

    // 시작 화면에서 방 탐색 진입
    private void HandleGameStart()
    {
        if (session == null || session.State != NetworkGameSessionState.Entry)
            return;

        createDialogOpen = false;
        ClearLocalMessages();
        session.OpenBrowser();
    }

    // 로컬 이름 검증·저장 결과 표시
    private void HandleSaveProfile()
    {
        string input = displayNameInput != null ? displayNameInput.text : string.Empty;
        if (!LocalPlayerProfile.TrySaveDisplayName(input, out string savedName, out string saveError))
        {
            localErrorOverride = saveError;
            localStatusOverride = null;
            Render();
            return;
        }

        if (displayNameInput != null)
            displayNameInput.text = savedName;
        localErrorOverride = null;
        localStatusOverride = "이름을 저장했습니다.";
        Render();
    }

    // 방 탐색 재요청
    private void HandleRefresh()
    {
        if (session == null || session.State != NetworkGameSessionState.Browsing)
            return;

        ClearLocalMessages();
        session.OpenBrowser();
    }

    // 로컬 방 생성 대화상자 표시
    private void HandleOpenCreateRoom()
    {
        if (session == null || session.State != NetworkGameSessionState.Browsing)
            return;

        createDialogOpen = true;
        ClearLocalMessages();
        Render();
    }

    // 방 이름 검증 후 세션 생성 요청
    private void HandleConfirmCreateRoom()
    {
        if (session == null || session.State != NetworkGameSessionState.Browsing)
            return;

        string requestedName = roomNameInput != null ? roomNameInput.text : string.Empty;
        if (!LocalPlayerProfile.TryValidateRoomName(requestedName, out string normalizedName, out string validationError))
        {
            localErrorOverride = validationError;
            localStatusOverride = null;
            Render();
            return;
        }

        ClearLocalMessages();
        session.CreateRoom(normalizedName);
        if (session.State != NetworkGameSessionState.Browsing)
            createDialogOpen = false;
        Render();
    }

    // 방 생성 대화상자 취소
    private void HandleCancelCreateRoom()
    {
        createDialogOpen = false;
        ClearLocalMessages();
        Render();
    }

    // 방 탐색에서 시작 화면 복귀
    private void HandleBrowserBack()
    {
        if (session == null || session.State != NetworkGameSessionState.Browsing)
            return;

        createDialogOpen = false;
        ClearLocalMessages();
        session.BackToStart();
    }

    // 접속·종료 대기 중 시작 화면 복귀 요청
    private void HandlePendingCancel()
    {
        if (session == null || !IsPendingState(session.State))
            return;

        createDialogOpen = false;
        session.BackToStart();
    }

    // 참가 가능한 탐색 결과의 세션 참가 요청
    private void HandleJoinRoom(LanDiscoveredRoom room)
    {
        if (session == null || session.State != NetworkGameSessionState.Browsing || room == null || !room.IsJoinable)
            return;

        ClearLocalMessages();
        session.JoinRoom(room);
    }

    // 게임 실행 중 세션 퇴장 요청
    private void HandleLeaveMatch()
    {
        if (session == null || session.State != NetworkGameSessionState.Running)
            return;

        session.Leave();
    }

    // 퇴장 취소와 게임 입력 복원 시도
    private void HandleCancelLeave()
    {
        if (session == null || session.State != NetworkGameSessionState.Running)
            return;

        CloseLeavePanel();
        Render();
    }

    // 게임 실행 중 취소 키의 퇴장 메뉴 전환
    private void HandleEscape()
    {
        if (session == null || session.State != NetworkGameSessionState.Running || Keyboard.current == null ||
            !Keyboard.current.escapeKey.wasPressedThisFrame)
        {
            return;
        }

        if (leavePanelOpen)
            CloseLeavePanel();
        else
            OpenLeavePanel();
        Render();
    }

    // 로컬 입력 상태 보관·중지와 메뉴용 커서 해제
    private void OpenLeavePanel()
    {
        if (leavePanelOpen || session == null || session.State != NetworkGameSessionState.Running)
            return;

        previousCursorLockState = Cursor.lockState;
        previousCursorVisible = Cursor.visible;
        leavePanelOpen = true;
        menuPlayer = GameManager.Instance != null ? GameManager.Instance.CurrentPlayer : null;
        if (menuPlayer != null)
        {
            suspendedInputs = new Behaviour[]
            {
                menuPlayer.GetComponent<PlayerInput>(), menuPlayer.GetComponent<PlayerController>(),
                menuPlayer.GetComponent<PlayerLook>(), menuPlayer.GetComponent<PlayerSkillInput>(),
                menuPlayer.GetComponent<WizardSkillTargeting>()
            };
            suspendedInputStates = new bool[suspendedInputs.Length];
            for (int i = 0; i < suspendedInputs.Length; i++)
            {
                if (suspendedInputs[i] == null) continue;
                suspendedInputStates[i] = suspendedInputs[i].enabled;
                suspendedInputs[i].enabled = false;
            }
            menuPlayer.GetComponent<PlayerInputHandler>()?.ClearInput();
        }
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
        Render();
    }

    // 동일한 생존 플레이어의 실행 중 세션에 한정한 기존 입력·커서 복원
    private void CloseLeavePanel()
    {
        if (!leavePanelOpen)
            return;

        leavePanelOpen = false;
        bool restore = session != null && session.State == NetworkGameSessionState.Running &&
            menuPlayer != null && GameManager.Instance != null && GameManager.Instance.CurrentPlayer == menuPlayer &&
            (!menuPlayer.TryGetComponent(out PlayerHealth health) || !health.IsDead);
        if (restore && suspendedInputs != null)
            for (int i = 0; i < suspendedInputs.Length; i++)
                if (suspendedInputs[i] != null) suspendedInputs[i].enabled = suspendedInputStates[i];
        suspendedInputs = null;
        suspendedInputStates = null;
        menuPlayer = null;
        Cursor.lockState = restore ? previousCursorLockState : CursorLockMode.None;
        Cursor.visible = !restore || previousCursorVisible;
    }

    // 세션·로컬 대화상자 상태별 패널·버튼·문구 표시
    private void Render()
    {
        NetworkGameSessionState state = session != null ? session.State : NetworkGameSessionState.Entry;
        bool hasSession = session != null;
        bool isEntryOrBrowser = state == NetworkGameSessionState.Entry || state == NetworkGameSessionState.Browsing;
        bool isBrowsing = state == NetworkGameSessionState.Browsing;
        bool isRunning = state == NetworkGameSessionState.Running;
        bool isPending = IsPendingState(state);

        if (!isRunning && leavePanelOpen)
            CloseLeavePanel();

        SetPanel(startPanel, hasSession && state == NetworkGameSessionState.Entry);
        SetPanel(browserPanel, hasSession && isBrowsing);
        SetPanel(createRoomPanel, hasSession && isBrowsing && createDialogOpen);
        SetPanel(loadingPanel, hasSession && isPending);
        SetPanel(leavePanel, hasSession && isRunning && leavePanelOpen);

        SetInteractable(gameStartButton, hasSession && state == NetworkGameSessionState.Entry);
        SetInteractable(saveProfileButton, true);
        SetInteractable(refreshButton, isBrowsing);
        SetInteractable(openCreateRoomButton, isBrowsing);
        SetInteractable(createRoomConfirmButton, isBrowsing && createDialogOpen);
        SetInteractable(createRoomCancelButton, isBrowsing && createDialogOpen);
        SetInteractable(browserBackButton, isBrowsing);
        SetInteractable(pendingCancelButton, isPending);
        SetInteractable(leaveButton, isRunning);
        SetActiveIfChanged(leaveButton != null ? leaveButton.gameObject : null, isRunning);
        SetInteractable(leaveConfirmButton, isRunning && leavePanelOpen);
        SetInteractable(leaveCancelButton, isRunning);

        bool showEntryStatus = isEntryOrBrowser || isPending;
        if (showEntryStatus)
        {
            // 로컬 안내 우선, 세션 안내와 탐색 오류의 순차 반영
            string status = localStatusOverride;
            if (string.IsNullOrEmpty(status))
                status = hasSession ? session.Status : "Network session is unavailable.";
            SetText(statusText, status);

            string error = localErrorOverride;
            if (string.IsNullOrEmpty(error) && hasSession)
                error = session.Error;
            if (string.IsNullOrEmpty(error) && isBrowsing && discovery != null && discovery.Status != null &&
                discovery.Status.State == LanRoomDiscoveryState.Error)
            {
                error = discovery.Status.Message;
            }
            SetText(errorText, error);
        }
        else
        {
            SetText(statusText, string.Empty);
            SetText(errorText, string.Empty);
        }

        if (entryCameraRoot != null && entryCameraRoot.activeSelf == entrySceneLoaded)
            entryCameraRoot.SetActive(!entrySceneLoaded);
        if (entryEventSystemRoot != null && entryEventSystemRoot.activeSelf == entrySceneLoaded)
            entryEventSystemRoot.SetActive(!entrySceneLoaded);
    }

    // 생성·참가·종료 대기 상태 판별
    private static bool IsPendingState(NetworkGameSessionState state)
    {
        return state == NetworkGameSessionState.Creating || state == NetworkGameSessionState.Joining ||
               state == NetworkGameSessionState.Stopping;
    }

    // 패널 표시 상태 적용
    private static void SetPanel(GameObject panel, bool visible)
    {
        SetActiveIfChanged(panel, visible);
    }

    // 활성 상태 변경 시에만 오브젝트 전환
    private static void SetActiveIfChanged(GameObject target, bool active)
    {
        if (target != null && target.activeSelf != active)
            target.SetActive(active);
    }

    // 버튼 조작 가능 상태 적용
    private static void SetInteractable(Button button, bool interactable)
    {
        if (button != null)
            button.interactable = interactable;
    }

    // 서식 태그 해석 없는 문구 표시
    private static void SetText(TMP_Text label, string value)
    {
        if (label == null)
            return;

        label.richText = false;
        label.text = value ?? string.Empty;
    }

    // 로컬 상태·오류 문구의 우선 표시 해제
    private void ClearLocalMessages()
    {
        localStatusOverride = null;
        localErrorOverride = null;
    }
}
