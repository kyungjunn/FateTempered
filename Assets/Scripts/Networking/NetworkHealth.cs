using System;
using Unity.Netcode;
using UnityEngine;

// Keeps the authoritative current/max health pair together for every replica.
public sealed class NetworkHealth : NetworkBehaviour
{
    private struct HealthSnapshot : INetworkSerializable, IEquatable<HealthSnapshot>
    {
        public int CurrentHealth;
        public int MaxHealth;

        public bool IsDead => CurrentHealth <= 0;

        public HealthSnapshot(int currentHealth, int maxHealth)
        {
            MaxHealth = Mathf.Max(0, maxHealth);
            CurrentHealth = Mathf.Clamp(currentHealth, 0, MaxHealth);
        }

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            serializer.SerializeValue(ref CurrentHealth);
            serializer.SerializeValue(ref MaxHealth);
        }

        public bool Equals(HealthSnapshot other)
        {
            return CurrentHealth == other.CurrentHealth && MaxHealth == other.MaxHealth;
        }

        public override bool Equals(object obj)
        {
            return obj is HealthSnapshot other && Equals(other);
        }

        public override int GetHashCode()
        {
            return unchecked((CurrentHealth * 397) ^ MaxHealth);
        }
    }

    private readonly NetworkVariable<HealthSnapshot> healthSnapshot = new NetworkVariable<HealthSnapshot>(
        default,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    private PlayerHealth playerHealth;
    private EnemyHealth enemyHealth;

    public override void OnNetworkSpawn()
    {
        healthSnapshot.OnValueChanged += HandleHealthSnapshotChanged;
        BindHealthOnSpawn();

        if (IsServer)
        {
            PublishHealthSnapshot();
        }
        else
        {
            ApplyHealthSnapshot(healthSnapshot.Value);
        }
    }

    public override void OnNetworkDespawn()
    {
        healthSnapshot.OnValueChanged -= HandleHealthSnapshotChanged;
        UnbindHealth();
    }

    public static bool CanWrite(GameObject target)
    {
        if (target == null)
        {
            return false;
        }

        NetworkObject networkObject = target.GetComponent<NetworkObject>();
        if (networkObject == null || !networkObject.IsSpawned)
        {
            return true;
        }

        return networkObject.NetworkManager != null && networkObject.NetworkManager.IsServer;
    }

    private void BindHealthOnSpawn()
    {
        // PlayerController adds PlayerHealth in Awake on some player prefabs.
        playerHealth = GetComponent<PlayerHealth>();
        if (playerHealth != null)
        {
            playerHealth.onHealthChanged += HandleHealthChanged;
            return;
        }

        enemyHealth = GetComponent<EnemyHealth>();
        if (enemyHealth != null)
        {
            enemyHealth.OnHealthChanged += HandleHealthChanged;
        }
    }

    private void UnbindHealth()
    {
        if (playerHealth != null)
        {
            playerHealth.onHealthChanged -= HandleHealthChanged;
            playerHealth = null;
        }

        if (enemyHealth != null)
        {
            enemyHealth.OnHealthChanged -= HandleHealthChanged;
            enemyHealth = null;
        }
    }

    private void HandleHealthChanged()
    {
        PublishHealthSnapshot();
    }

    private void HandleHealthChanged(int currentHealth, int maxHealth)
    {
        PublishHealthSnapshot();
    }

    private void PublishHealthSnapshot()
    {
        if (!IsSpawned || !IsServer)
        {
            return;
        }

        if (playerHealth != null)
        {
            healthSnapshot.Value = new HealthSnapshot(playerHealth.CurrentHealth, playerHealth.MaxHealth);
        }
        else if (enemyHealth != null)
        {
            healthSnapshot.Value = new HealthSnapshot(enemyHealth.CurrentHealth, enemyHealth.MaxHealth);
        }
    }

    private void HandleHealthSnapshotChanged(HealthSnapshot previousValue, HealthSnapshot currentValue)
    {
        if (IsServer)
        {
            return;
        }

        ApplyHealthSnapshot(currentValue);
    }

    private void ApplyHealthSnapshot(HealthSnapshot snapshot)
    {
        if (playerHealth != null)
        {
            playerHealth.ApplyReplicatedHealth(snapshot.CurrentHealth, snapshot.MaxHealth);
        }
        else if (enemyHealth != null)
        {
            enemyHealth.ApplyReplicatedHealth(snapshot.CurrentHealth, snapshot.MaxHealth);
        }
    }
}
