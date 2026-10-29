using System;
using System.Collections;
using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEngine;

// 서버 투사체 생성·수명 관리와 클라이언트 연출 복제
public sealed class NetworkProjectile : NetworkBehaviour
{
    // 종료 연출 식별값: 없음·명중·폭발·명중과 폭발
    private const byte NoFinishVisual = 0;
    private const byte FinishedWithHit = 1;
    private const byte FinishedWithExplosion = 2;
    private const byte FinishedWithHitAndExplosion = 3;

    // 서버 쓰기·전체 읽기 생성 연출 설정, 피해 판정과 별개
    private readonly NetworkVariable<float> visualScale = new NetworkVariable<float>(1f,
        NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    private readonly NetworkVariable<bool> hasWizardVisual = new NetworkVariable<bool>(false,
        NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    private readonly NetworkVariable<bool> empoweredWizardAttack = new NetworkVariable<bool>(false,
        NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    private readonly NetworkVariable<bool> hasExplosion = new NetworkVariable<bool>(false,
        NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    // 늦은 복제본용 종료 연출·명중 위치 보존
    private readonly NetworkVariable<byte> finishVisual = new NetworkVariable<byte>(NoFinishVisual,
        NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    private readonly NetworkVariable<Vector3> finishHitPosition = new NetworkVariable<Vector3>(Vector3.zero,
        NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    // 생성 준비·지연 제거 중복 방지
    private bool spawnStateConfigured;
    private bool despawnScheduled;

    // 스폰 전 서버·동일 매니저·서버 이동 권한 검증과 초기값 설정
    public void PrepareForSpawn(NetworkManager manager, float projectileVisualScale,
        bool configureWizardVisual, bool empowered, bool explosive)
    {
        NetworkObject networkObject = GetComponent<NetworkObject>();
        NetworkTransform networkTransform = GetComponent<NetworkTransform>();
        if (networkObject == null || networkTransform == null)
            throw new InvalidOperationException(
                $"Network projectile '{name}' requires a root NetworkObject and NetworkTransform.");
        if (manager == null || !manager.IsListening || !manager.IsServer)
            throw new InvalidOperationException(
                $"Network projectile '{name}' can only be prepared by an active server.");
        if (networkObject.IsSpawned)
            throw new InvalidOperationException($"Network projectile '{name}' must be configured before Spawn.");
        if (networkObject.NetworkManager != manager)
            throw new InvalidOperationException(
                $"Network projectile '{name}' and its damage dealer must use the same NetworkManager.");
        if (networkTransform.AuthorityMode != NetworkTransform.AuthorityModes.Server ||
            !networkTransform.IsServerAuthoritative())
            throw new InvalidOperationException(
                $"Network projectile '{name}' requires a server-authoritative NetworkTransform.");

        visualScale.Reset(Mathf.Max(0.1f, projectileVisualScale));
        hasWizardVisual.Reset(configureWizardVisual);
        empoweredWizardAttack.Reset(configureWizardVisual && empowered);
        hasExplosion.Reset(explosive);
        finishVisual.Reset(NoFinishVisual);
        finishHitPosition.Reset(Vector3.zero);
        despawnScheduled = false;
        spawnStateConfigured = true;
    }

    // 서버 권한 구성 검증·비서버 시각 복제본 초기화
    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();

        NetworkTransform networkTransform = GetComponent<NetworkTransform>();
        if (networkTransform == null ||
            networkTransform.AuthorityMode != NetworkTransform.AuthorityModes.Server ||
            !networkTransform.IsServerAuthoritative())
        {
            Debug.LogError(
                $"Network projectile '{name}' requires a server-authoritative NetworkTransform.", this);
            if (IsServer)
                NetworkObject.Despawn(true);
            return;
        }

        if (IsServer)
        {
            if (!spawnStateConfigured)
            {
                Debug.LogError($"Network projectile '{name}' was spawned without server spawn state.", this);
                NetworkObject.Despawn(true);
            }
            // 스폰 전 적용된 시각 크기의 호스트 중복 확대 방지
            return;
        }

        DamageProjectile projectile = GetComponent<DamageProjectile>();
        if (projectile == null)
        {
            Debug.LogError($"Network projectile '{name}' is missing DamageProjectile.", this);
            return;
        }

        projectile.InitializeNetworkReplica(visualScale.Value, hasWizardVisual.Value,
            empoweredWizardAttack.Value, hasExplosion.Value);
        ApplyReplicatedFinishVisual(projectile);
    }

    // 서버 관통 명중 연출 전송·투사체 유지
    public void SendPiercingHitVisual(Vector3 hitPosition)
    {
        RequireSpawnedServer();
        PlayVisualRpc(hitPosition, true, false, false);
    }

    // 서버 종료 연출 상태 보존·즉시 전송
    public void SendFinishVisual(Vector3 hitPosition, bool hit, bool exploded)
    {
        RequireSpawnedServer();
        finishHitPosition.Value = hitPosition;
        finishVisual.Value = exploded
            ? hit ? FinishedWithHitAndExplosion : FinishedWithExplosion
            : hit ? FinishedWithHit : NoFinishVisual;
        PlayVisualRpc(hitPosition, hit, true, exploded);
    }

    // 스폰된 서버 투사체 즉시 제거
    public void Despawn()
    {
        RequireSpawnedServer();
        NetworkObject.Despawn(true);
    }

    // 서버 지연 제거 예약·중복 예약 제외
    public void DespawnAfter(float delay)
    {
        RequireSpawnedServer();
        if (despawnScheduled)
            return;

        despawnScheduled = true;
        StartCoroutine(DespawnAfterDelay(Mathf.Max(0f, delay)));
    }

    // 서버 → 비서버 명중·종료 연출, 피해 판정 없음
    [Rpc(SendTo.NotServer, InvokePermission = RpcInvokePermission.Server)]
    private void PlayVisualRpc(Vector3 hitPosition, bool hit, bool projectileFinishes, bool exploded)
    {
        if (IsServer)
            return;

        DamageProjectile projectile = GetComponent<DamageProjectile>();
        if (projectile == null)
        {
            Debug.LogError($"Network projectile '{name}' is missing DamageProjectile.", this);
            return;
        }

        projectile.PlayNetworkVisual(hitPosition, hit, projectileFinishes, exploded);
    }

    // 초기 복제 상태의 종료 연출 재현
    private void ApplyReplicatedFinishVisual(DamageProjectile projectile)
    {
        bool hit = finishVisual.Value == FinishedWithHit || finishVisual.Value == FinishedWithHitAndExplosion;
        bool exploded = finishVisual.Value == FinishedWithExplosion || finishVisual.Value == FinishedWithHitAndExplosion;
        if (hit || exploded)
            projectile.PlayNetworkVisual(finishHitPosition.Value, hit, true, exploded);
    }

    // 대기 후 서버·스폰 상태 재검증과 제거
    private IEnumerator DespawnAfterDelay(float delay)
    {
        yield return new WaitForSeconds(delay);
        if (IsServer && IsSpawned)
            NetworkObject.Despawn(true);
    }

    // 변경 작업의 서버 권한·스폰 상태 강제 검증
    private void RequireSpawnedServer()
    {
        if (!IsServer || !IsSpawned)
            throw new InvalidOperationException($"Network projectile '{name}' can only be modified by its spawned server instance.");
    }
}
