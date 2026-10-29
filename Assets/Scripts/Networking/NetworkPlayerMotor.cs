using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEngine;

// 소유자는 입력 의도만 제출, NetworkTransform·NetworkAnimator는 서버 권위 필수
// 스폰된 네트워크 이동 전용, 오프라인 이동은 기존 PlayerMovement 경로
[RequireComponent(typeof(NetworkObject), typeof(Rigidbody), typeof(PlayerMovement))]
public sealed class NetworkPlayerMotor : NetworkBehaviour
{
    // 입력 단절 시 이동을 해제할 비스케일 시간 제한
    public const float InputTimeout = 0.25f;
    // 서버가 검증한 최신 이동 입력, NetworkVariable 복제 대상 아님
    public Vector2 CurrentMoveInput { get; private set; }

    // 서버 이동·상태 판정 및 애니메이션 의존성
    private PlayerMovement movement;
    private PlayerSkillController skills;
    private PlayerHealth health;
    private PlayerStateManager state;
    private Animator animator;
    private NetworkAnimator networkAnimator;
    private Rigidbody body;
    private PlayerStats stats;
    // 정규화된 회전각·마지막 입력 시각·다음 물리 틱 점프 요청
    private float yaw;
    private float lastInputTime = float.NegativeInfinity;
    private bool jumpPending;
    // 디스폰 이후 입력·물리 실행 차단 표식
    private bool networkDespawned;

    // 이동·스킬·생존·물리·표현 참조 캐시
    private void Awake()
    {
        movement = GetComponent<PlayerMovement>();
        skills = GetComponent<PlayerSkillController>();
        health = GetComponent<PlayerHealth>();
        state = GetComponent<PlayerStateManager>();
        animator = GetComponentInChildren<Animator>();
        networkAnimator = GetComponentInChildren<NetworkAnimator>();
        body = GetComponent<Rigidbody>();
        stats = GetComponent<PlayerStats>();
    }

    // 서버만 물리 활성화, 네트워크 전투 준비 전에는 서버도 정지
    public override void OnNetworkSpawn()
    {
        networkDespawned = false;
        body.isKinematic = !IsServer ||
            (NetworkGameSession.IsNetworkSessionActive && !NetworkAuthority.IsCombatReady);
        yaw = transform.eulerAngles.y;
        ClearInput();
    }

    // 소유권 상실 시 잔류 입력 제거
    public override void OnLostOwnership() { ClearInput(); }
    // 디스폰 시 입력·속도 제거 및 물리 정지
    public override void OnNetworkDespawn()
    {
        networkDespawned = true;
        ClearInput();
        if (body != null)
        {
            if (!body.isKinematic)
            {
                body.linearVelocity = Vector3.zero;
                body.angularVelocity = Vector3.zero;
            }
            body.isKinematic = true;
        }
    }

    // 이동·점프·입력 유효 시간 초기화
    private void ClearInput()
    {
        CurrentMoveInput = Vector2.zero;
        jumpPending = false;
        lastInputTime = float.NegativeInfinity;
    }


    // 소유자 입력 진입점: 호스트 직접 수락, 클라이언트 서버 RPC 전달
    // 위치·속도·이동 속력·델타 시간·접지 판정의 클라이언트 주장 제외
    public void SubmitInput(Vector2 move, float yawDegrees, bool jump)
    {
        if (networkDespawned || !IsSpawned || !IsOwner || !NetworkAuthority.IsCombatReady) return;
        if (IsServer) AcceptInput(move, yawDegrees, jump);
        else SubmitInputRpc(move, yawDegrees, jump);
    }

    // 소유자 → 서버: 이동 방향·회전각·점프 의도
    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Owner)]
    private void SubmitInputRpc(Vector2 move, float yawDegrees, bool jump)
    {
        AcceptInput(move, yawDegrees, jump);
    }

    // 서버 입력 검증·방향 길이 제한·회전각 정규화·점프 누적
    private void AcceptInput(Vector2 move, float yawDegrees, bool jump)
    {
        if (networkDespawned || !NetworkAuthority.IsCombatReady || (skills != null && skills.IsSessionEnded))
        {
            ClearInput();
            return;
        }
        if (!IsSpawned || !IsServer || !IsFinite(move.x) || !IsFinite(move.y) ||
            !IsFinite(yawDegrees)) return;
        // 극단적인 유한 입력의 크기 계산 오버플로 방지를 위한 성분 우선 제한
        move.x = Mathf.Clamp(move.x, -1f, 1f);
        move.y = Mathf.Clamp(move.y, -1f, 1f);
        CurrentMoveInput = Vector2.ClampMagnitude(move, 1f);
        yaw = Mathf.Repeat(yawDegrees, 360f);
        jumpPending |= jump;
        lastInputTime = Time.unscaledTime;
    }

    // NaN·무한대 입력 제외
    private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);


    // 서버 물리 틱: 입력 만료·행동 제한 적용 후 이동·점프·상태 갱신
    private void FixedUpdate()
    {
        if (networkDespawned || !IsSpawned || !IsServer) return;
        if (!NetworkAuthority.IsCombatReady)
        {
            ClearInput();
            if (body != null) body.isKinematic = true;
            return;
        }
        // 통신 중단 시 이전 이동 입력의 무기한 유지 방지
        if (Time.unscaledTime - lastInputTime > InputTimeout) ClearInput();
        if ((skills != null && skills.IsSessionEnded) ||
            (health != null && health.IsDead) || (state != null && state.CurrentState == PlayerState.Dead))
        {
            ClearInput();
            return;
        }

        // 이동 잠금 중 회전·이동·점프 생략, 요청 점프는 틱 끝에서 소비
        bool locked = skills != null && skills.IsMovementLocked;
        Vector2 move = locked ? Vector2.zero : CurrentMoveInput;
        if (!locked)
        {
            if (Time.unscaledTime - lastInputTime <= InputTimeout)
                body.rotation = Quaternion.Euler(0f, yaw, 0f);
            movement.MoveAuthoritative(move, yaw, Time.fixedDeltaTime);
            if (jumpPending && movement.Jump())
            {
                // NetworkAnimator 경유 트리거 복제, 미존재 시 로컬 Animator 사용
                if (networkAnimator != null) networkAnimator.SetTrigger("Jump");
                else if (animator != null) animator.SetTrigger("Jump");
            }
        }
        jumpPending = false;
        bool grounded = movement.CheckGrounded();
        if (animator != null)
        {
            animator.SetFloat("AttackSpeed", stats != null && stats.AttackSpeed > 0f ? stats.AttackSpeed : 1f);
            animator.SetFloat("MoveSpeed", move.sqrMagnitude <= 0.01f ? 0f : move.y < -0.01f ? -1f : 1f);
            animator.SetFloat("LegSpeed", grounded ? 1f : 0f);
        }
        // 공격 상태 유지, 그 외에는 서버 접지·이동 판정으로 상태 결정
        if (state != null && state.CurrentState != PlayerState.Attack)
            state.ChangeState(!grounded ? PlayerState.Jump : move.sqrMagnitude > 0.01f ? PlayerState.Move : PlayerState.Idle);
    }
}
