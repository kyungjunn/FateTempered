using System;
using System.Collections;
using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEngine;

// Owns projectile spawn state, replica presentation, and server-only network lifetime.
public sealed class NetworkProjectile : NetworkBehaviour
{
    private const byte NoFinishVisual = 0;
    private const byte FinishedWithHit = 1;
    private const byte FinishedWithExplosion = 2;
    private const byte FinishedWithHitAndExplosion = 3;

    private readonly NetworkVariable<float> visualScale = new NetworkVariable<float>(1f,
        NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    private readonly NetworkVariable<bool> hasWizardVisual = new NetworkVariable<bool>(false,
        NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    private readonly NetworkVariable<bool> empoweredWizardAttack = new NetworkVariable<bool>(false,
        NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    private readonly NetworkVariable<bool> hasExplosion = new NetworkVariable<bool>(false,
        NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    private readonly NetworkVariable<byte> finishVisual = new NetworkVariable<byte>(NoFinishVisual,
        NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    private readonly NetworkVariable<Vector3> finishHitPosition = new NetworkVariable<Vector3>(Vector3.zero,
        NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    private bool spawnStateConfigured;
    private bool despawnScheduled;

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
            // DamageProjectile applied visual scale before Spawn; applying it again here would double-scale the host.
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

    public void SendPiercingHitVisual(Vector3 hitPosition)
    {
        RequireSpawnedServer();
        PlayVisualRpc(hitPosition, true, false, false);
    }

    public void SendFinishVisual(Vector3 hitPosition, bool hit, bool exploded)
    {
        RequireSpawnedServer();
        finishHitPosition.Value = hitPosition;
        finishVisual.Value = exploded
            ? hit ? FinishedWithHitAndExplosion : FinishedWithExplosion
            : hit ? FinishedWithHit : NoFinishVisual;
        PlayVisualRpc(hitPosition, hit, true, exploded);
    }

    public void Despawn()
    {
        RequireSpawnedServer();
        NetworkObject.Despawn(true);
    }

    public void DespawnAfter(float delay)
    {
        RequireSpawnedServer();
        if (despawnScheduled)
            return;

        despawnScheduled = true;
        StartCoroutine(DespawnAfterDelay(Mathf.Max(0f, delay)));
    }

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

    private void ApplyReplicatedFinishVisual(DamageProjectile projectile)
    {
        bool hit = finishVisual.Value == FinishedWithHit || finishVisual.Value == FinishedWithHitAndExplosion;
        bool exploded = finishVisual.Value == FinishedWithExplosion || finishVisual.Value == FinishedWithHitAndExplosion;
        if (hit || exploded)
            projectile.PlayNetworkVisual(finishHitPosition.Value, hit, true, exploded);
    }

    private IEnumerator DespawnAfterDelay(float delay)
    {
        yield return new WaitForSeconds(delay);
        if (IsServer && IsSpawned)
            NetworkObject.Despawn(true);
    }

    private void RequireSpawnedServer()
    {
        if (!IsServer || !IsSpawned)
            throw new InvalidOperationException($"Network projectile '{name}' can only be modified by its spawned server instance.");
    }
}
