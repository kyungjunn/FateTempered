using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;

// 서버 전용 적 시뮬레이션, 클라이언트는 NetworkTransform·NetworkAnimator·NetworkHealth로 표시
// 스폰 전·오프라인은 기존 적 컴포넌트 활성 상태 유지
public sealed class EnemyNetworkRole : NetworkBehaviour
{
    // 클라이언트 보스 공격 예고 표시 대상
    private BossEnemyAttack bossAttack;


    // 보스 공격 연출 참조 캐시
    private void Awake()
    {
        bossAttack = GetComponent<BossEnemyAttack>();
    }

    // 서버 내비게이션 위치 보정·클라이언트 AI 및 물리 시뮬레이션 차단
    public override void OnNetworkSpawn()
    {
        if (IsServer)
        {
            // 활성 에이전트가 NavMesh 밖인 경우 반경 2 이내 유효 위치로 이동
            if (TryGetComponent(out NavMeshAgent agent) && agent.enabled && !agent.isOnNavMesh &&
                NavMesh.SamplePosition(transform.position, out NavMeshHit hit, 2f, agent.areaMask))
            {
                agent.Warp(hit.position);
            }
            return;
        }

        SetSimulation(false);
    }

    // AI·배회·충돌 가드·내비게이션 활성 제어, Rigidbody는 키네마틱 고정
    private void SetSimulation(bool simulate)
    {
        if (TryGetComponent(out EnemyAI ai)) ai.enabled = simulate;
        if (TryGetComponent(out PassiveEnemyRoamer roamer)) roamer.enabled = simulate;
        if (TryGetComponent(out BossPlayerCollisionGuard guard)) guard.enabled = simulate;
        if (TryGetComponent(out NavMeshAgent agent)) agent.enabled = simulate;
        if (TryGetComponent(out Rigidbody body)) body.isKinematic = true;
    }

    // 스폰된 서버만 강공격 예고 위치·표시 여부 전송
    public void SendStrongAttackIndicator(Vector3 center, bool show)
    {
        if (!IsSpawned || !IsServer) return;
        StrongAttackIndicatorRpc(center, show);
    }

    // 서버 → 서버 외 클라이언트: 보스 공격 예고 연출만 반영
    [Rpc(SendTo.NotServer, InvokePermission = RpcInvokePermission.Server)]
    private void StrongAttackIndicatorRpc(Vector3 center, bool show)
    {
        if (bossAttack != null) bossAttack.ApplyReplicatedIndicator(center, show);
    }
}
