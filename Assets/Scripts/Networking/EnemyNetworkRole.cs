using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;

// 적 시뮬레이션은 서버만. 클라이언트는 NetworkTransform/NetworkAnimator/NetworkHealth로 표시만 한다.
public sealed class EnemyNetworkRole : NetworkBehaviour
{
    private BossEnemyAttack bossAttack;


    private void Awake()
    {
        bossAttack = GetComponent<BossEnemyAttack>();
    }

    public override void OnNetworkSpawn()
    {
        if (IsServer)
        {
            if (TryGetComponent(out NavMeshAgent agent) && agent.enabled && !agent.isOnNavMesh &&
                NavMesh.SamplePosition(transform.position, out NavMeshHit hit, 2f, agent.areaMask))
            {
                agent.Warp(hit.position);
            }
            return;
        }

        SetSimulation(false);
    }

    private void SetSimulation(bool simulate)
    {
        if (TryGetComponent(out EnemyAI ai)) ai.enabled = simulate;
        if (TryGetComponent(out PassiveEnemyRoamer roamer)) roamer.enabled = simulate;
        if (TryGetComponent(out BossPlayerCollisionGuard guard)) guard.enabled = simulate;
        if (TryGetComponent(out NavMeshAgent agent)) agent.enabled = simulate;
        if (TryGetComponent(out Rigidbody body)) body.isKinematic = true;
    }

    public void SendStrongAttackIndicator(Vector3 center, bool show)
    {
        if (!IsSpawned || !IsServer) return;
        StrongAttackIndicatorRpc(center, show);
    }

    [Rpc(SendTo.NotServer, InvokePermission = RpcInvokePermission.Server)]
    private void StrongAttackIndicatorRpc(Vector3 center, bool show)
    {
        if (bossAttack != null) bossAttack.ApplyReplicatedIndicator(center, show);
    }
}
