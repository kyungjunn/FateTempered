using Unity.Cinemachine;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

// 플레이어 공통 소유권·서버 시뮬레이션·복제 표시 분리, 오프라인 동작 유지
[DefaultExecutionOrder(-100)]
[RequireComponent(typeof(NetworkObject))]
public sealed class PlayerNetworkOwnership : NetworkBehaviour
{
    // 소유자 조작 컴포넌트·초기 활성 상태
    private Behaviour[] localBehaviours;
    private bool[] initialEnabled;
    // 소유자 전용 카메라·초기 활성 상태
    private CinemachineCamera[] playerCameras;
    private bool[] initialCameraEnabled;
    // 서버 물리 시뮬레이션 대상
    private Rigidbody body;
    // 직전 로컬 조작·서버 시뮬레이션 적용 상태
    private bool hadLocalControl;
    private bool localSimulationEnabled;
    // 최초 권한 적용 여부
    private bool controlStateInitialized;
    // 로컬 등록 여부·중복 등록 시도 방지
    private bool registeredLocalPlayer;
    private bool localRegistrationAttempted;
    // 디스폰 이후 재활성화 차단
    private bool networkDespawned;

    // 초기 상태 저장·네트워크 스폰 전 입력 차단
    private void Awake()
    {
        localBehaviours = new Behaviour[]
        {
            GetComponent<PlayerInput>(), GetComponent<PlayerInputHandler>(),
            GetComponent<PlayerController>(), GetComponent<PlayerMovement>(),
            GetComponent<PlayerLook>(), GetComponent<PlayerSkillController>(),
            GetComponent<PlayerSkillInput>(), GetComponent<WizardSkillTargeting>()
        };
        initialEnabled = new bool[localBehaviours.Length];
        for (int i = 0; i < localBehaviours.Length; i++)
            initialEnabled[i] = localBehaviours[i] != null && localBehaviours[i].enabled;
        playerCameras = GetComponentsInChildren<CinemachineCamera>(true);
        initialCameraEnabled = new bool[playerCameras.Length];
        for (int i = 0; i < playerCameras.Length; i++)
            initialCameraEnabled[i] = playerCameras[i].enabled;
        body = GetComponent<Rigidbody>();

        // 네트워크 스폰 전 원격 입력 인스턴스의 장치 선점 방지
        if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening)
            SetLocalControl(false);
    }

    // 스폰 시 소유권 기준 초기 권한 적용
    public override void OnNetworkSpawn()
    {
        networkDespawned = false;
        controlStateInitialized = false;
        SetLocalControl(IsOwner);
    }

    // 소유권 획득 시 로컬 등록 재시도·조작 활성화
    public override void OnGainedOwnership()
    {
        if (IsSpawned)
        {
            localRegistrationAttempted = false;
            SetLocalControl(true);
        }
    }

    // 소유권 상실 시 로컬 조작·등록 해제
    public override void OnLostOwnership()
    {
        if (IsSpawned)
        {
            localRegistrationAttempted = false;
            SetLocalControl(false);
        }
    }

    // 디스폰 시 스킬 세션·로컬 등록·잔여 물리 속도 정리
    public override void OnNetworkDespawn()
    {
        networkDespawned = true;
        if (TryGetComponent(out PlayerSkillController skills))
            skills.EndSession();
        UnregisterLocalPlayer();
        localRegistrationAttempted = false;
        if (body != null && !body.isKinematic)
        {
            body.linearVelocity = Vector3.zero;
            body.angularVelocity = Vector3.zero;
        }
        SetLocalControl(false);
    }

    // 네트워크 전투 준비 상태에 따른 권한 재평가
    private void Update()
    {
        if (IsSpawned && NetworkGameSession.IsNetworkSessionActive)
            SetLocalControl(IsOwner);
    }

    // 소유자 조작·서버 실행·원격 복제 표시 권한 분리 적용
    private void SetLocalControl(bool local)
    {
        bool networkSession = NetworkGameSession.IsNetworkSessionActive;
        GameManager gameManager = GameManager.Instance;
        bool combatReady = NetworkAuthority.IsCombatReady;
        // 전투 준비 완료·디스폰 이전 소유자 조작과 서버 시뮬레이션
        bool localControl = local && combatReady && !networkDespawned;
        bool serverSimulation = IsSpawned && IsServer && combatReady && !networkDespawned;

        // 로컬 플레이어 등록 실패 시 조작 허용 보류
        if (networkSession && local && combatReady && !registeredLocalPlayer && !localRegistrationAttempted)
        {
            localRegistrationAttempted = true;
            if (gameManager == null || !gameManager.RegisterLocalPlayer(gameObject))
                localControl = false;
            else
                registeredLocalPlayer = true;
        }
        if (!local || !combatReady)
            localRegistrationAttempted = false;
        if (!localControl)
            UnregisterLocalPlayer();

        // 동일 권한 상태의 중복 컴포넌트 설정 생략
        if (controlStateInitialized && hadLocalControl == localControl &&
            localSimulationEnabled == serverSimulation)
            return;

        for (int i = 0; i < localBehaviours.Length; i++)
            if (localBehaviours[i] != null)
            {
                // 서버의 원격 플레이어 이동·스킬 실행 허용
                bool simulation = localBehaviours[i] is PlayerMovement ||
                    localBehaviours[i] is PlayerSkillController;
                localBehaviours[i].enabled = (localControl || (serverSimulation && simulation)) && initialEnabled[i];
            }
        for (int i = 0; i < playerCameras.Length; i++)
            playerCameras[i].enabled = localControl && initialCameraEnabled[i];
        // 전투 준비 완료 서버만 물리 이동, 클라이언트는 NetworkTransform 추종
        if (body != null) body.isKinematic = !serverSimulation;
        if (localControl)
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }
        else if (hadLocalControl)
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }
        hadLocalControl = localControl;
        localSimulationEnabled = serverSimulation;
        controlStateInitialized = true;
    }

    // 등록된 로컬 플레이어 참조의 단일 해제
    private void UnregisterLocalPlayer()
    {
        if (!registeredLocalPlayer) return;
        registeredLocalPlayer = false;
        GameManager.Instance?.UnregisterLocalPlayer(gameObject);
    }
}
