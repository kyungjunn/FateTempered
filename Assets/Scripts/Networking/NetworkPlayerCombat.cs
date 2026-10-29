using Unity.Netcode;
using UnityEngine;

// 공용 BasePlayer의 기본 공격 중계: NetworkObject·PlayerHealth·IPlayerBasicAttack 필요
// 기본 공격만 동기화, 캐릭터별 스킬은 기존 경로 유지
[RequireComponent(typeof(NetworkObject))]
public sealed class NetworkPlayerCombat : NetworkBehaviour
{
    // 서버 실행에 사용할 기존 공격·생존·상태 참조
    private IPlayerBasicAttack basicAttack;
    private PlayerHealth playerHealth;
    private PlayerStateManager stateManager;
    // 기존 공격 구현 재진입 시 네트워크 중계 우회 표식
    private bool executingAuthoritativeAttack;

    // 공격 실행 의존성 캐시
    private void Awake()
    {
        basicAttack = GetComponent<IPlayerBasicAttack>();
        playerHealth = GetComponent<PlayerHealth>();
        stateManager = GetComponent<PlayerStateManager>();
    }

    // 스폰된 플레이어의 로컬 공격 차단·소유자 입력 중계
    // 오프라인·스폰 전·서버 재진입은 기존 구현으로 진행(false)
    public static bool TryRouteAttack(MonoBehaviour attackSource)
    {
        if (attackSource == null)
        {
            return false;
        }

        NetworkPlayerCombat combat = attackSource.GetComponent<NetworkPlayerCombat>();
        if (combat == null || !combat.IsSpawned || combat.executingAuthoritativeAttack)
        {
            return false;
        }

        // 전투 준비 전 입력 소비: 기존 로컬 공격 실행 방지
        if (!NetworkAuthority.IsCombatReady)
            return true;

        // 호스트 소유자는 직접 실행, 원격 소유자는 서버 RPC 요청
        if (combat.IsServer)
        {
            if (combat.IsOwner)
            {
                combat.ExecuteAuthoritativeAttack(combat.ReadAimPitch());
            }
        }
        else if (combat.IsOwner)
        {
            combat.RequestBasicAttackRpc(combat.ReadAimPitch());
        }

        return true;
    }

    // 로컬 카메라 상하 조준각 추출, 피벗 미존재 시 정면
    private float ReadAimPitch()
    {
        var look = GetComponent<PlayerLook>();
        return look != null && look.cameraPivot != null
            ? Mathf.DeltaAngle(0f, look.cameraPivot.localEulerAngles.x) : 0f;
    }

    // 서버 공격 실행: 전투·생존·스킬 차단·유한 조준값 검증
    private void ExecuteAuthoritativeAttack(float aimPitch)
    {
        if (playerHealth == null) playerHealth = GetComponent<PlayerHealth>();
        var skills = GetComponent<NetworkPlayerSkills>();
        var skillController = GetComponent<PlayerSkillController>();
        if (!NetworkAuthority.IsCombatReady || (skills != null && skills.BlocksBasicAttack) ||
            (skillController != null && skillController.IsSessionEnded)) return;
        if (float.IsNaN(aimPitch) || float.IsInfinity(aimPitch) ||
            !IsSpawned || !IsServer || !NetworkAuthority.CanSimulate(gameObject) ||
            playerHealth == null || playerHealth.IsDead || playerHealth.CurrentHealth <= 0 ||
            (stateManager != null && stateManager.CurrentState == PlayerState.Dead) ||
            basicAttack == null || executingAuthoritativeAttack)
        {
            return;
        }

        bool accepted;
        var look = GetComponent<PlayerLook>();
        // 조준각만 입력 의도 수용, 발사 원점·명중·피해는 서버 권위
        Quaternion previousAim = look != null && look.cameraPivot != null
            ? look.cameraPivot.localRotation : Quaternion.identity;
        if (look != null && look.cameraPivot != null)
            look.cameraPivot.localRotation = Quaternion.Euler(
                Mathf.Clamp(aimPitch, look.downClamp, look.upClamp), 0f, 0f);
        executingAuthoritativeAttack = true;
        try
        {
            // 재진입 표식으로 TryRouteAttack 우회 후 기존 공격 구현 1회 실행
            accepted = basicAttack.TryAttack();
        }
        finally
        {
            // 성공·예외와 무관한 재진입 표식·임시 조준 복원
            executingAuthoritativeAttack = false;
            if (look != null && look.cameraPivot != null) look.cameraPivot.localRotation = previousAim;
        }

        if (accepted)
        {
            AcceptedBasicAttackRpc();
        }
    }

    // 소유자 → 서버: 조준 입력 전달 후 서버 공통 검증
    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Owner)]
    private void RequestBasicAttackRpc(float aimPitch)
    {
        ExecuteAuthoritativeAttack(aimPitch);
    }

    // 서버의 근접 명중 연출 요청, 종료된 전투 제외
    public void PresentMeleeHit(Vector3 contactPoint)
    {
        PlayerSkillController skillController = GetComponent<PlayerSkillController>();
        if (IsServer && IsSpawned && NetworkAuthority.IsCombatReady &&
            (skillController == null || !skillController.IsSessionEnded))
            MeleeHitRpc(contactPoint);
    }

    // 서버 → 공격 소유자: 근접 타격 피드백
    [Rpc(SendTo.Owner, InvokePermission = RpcInvokePermission.Server)]
    private void MeleeHitRpc(Vector3 contactPoint)
    {
        // 시각 피드백만 재생, 서버·클라이언트 시뮬레이션 감속 제외
        PlayerSkillController skillController = GetComponent<PlayerSkillController>();
        if (NetworkAuthority.IsCombatReady && (skillController == null || !skillController.IsSessionEnded))
            GetComponent<SwordCombatFeedback>()?.PlayMeleeImpact(contactPoint, false);
    }

    // 서버 → 전체: 승인된 기본 공격 연출 공유
    [Rpc(SendTo.Everyone, InvokePermission = RpcInvokePermission.Server)]
    private void AcceptedBasicAttackRpc()
    {
        PlayerSkillController skillController = GetComponent<PlayerSkillController>();
        if (!NetworkAuthority.IsCombatReady || (skillController != null && skillController.IsSessionEnded))
            return;

        PlayerController controller = GetComponent<PlayerController>();
        if (controller != null)
        {
            controller.PresentAcceptedBasicAttack();
        }

        // 서버의 실제 공격 연출과 중복되지 않는 클라이언트 근접 연출
        if (!IsServer && TryGetComponent(out MeleePlayerAttack meleeAttack))
        {
            meleeAttack.PlayAcceptedAttackVisual();
        }
    }
}
