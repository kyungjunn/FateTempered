using System;
using Unity.Netcode;
using UnityEngine;

// 서버가 계산한 플레이어별 레벨/경험치를 모든 복제본에 전달한다.
public sealed class NetworkPlayerProgress : NetworkBehaviour
{
    private struct ProgressSnapshot : INetworkSerializable, IEquatable<ProgressSnapshot>
    {
        public int Level;
        public int CurrentExp;
        public int RequiredExp;

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            serializer.SerializeValue(ref Level);
            serializer.SerializeValue(ref CurrentExp);
            serializer.SerializeValue(ref RequiredExp);
        }

        public bool Equals(ProgressSnapshot other)
        {
            return Level == other.Level && CurrentExp == other.CurrentExp && RequiredExp == other.RequiredExp;
        }

        public override bool Equals(object obj) => obj is ProgressSnapshot other && Equals(other);

        public override int GetHashCode() => unchecked((Level * 397) ^ (CurrentExp * 31) ^ RequiredExp);
    }

    private readonly NetworkVariable<ProgressSnapshot> snapshot = new NetworkVariable<ProgressSnapshot>(
        default, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    private PlayerLevelSystem levelSystem;

    public override void OnNetworkSpawn()
    {
        levelSystem = GetComponent<PlayerLevelSystem>();
        if (levelSystem == null)
        {
            Debug.LogError("NetworkPlayerProgress requires PlayerLevelSystem.", this);
            return;
        }

        if (IsServer)
        {
            levelSystem.onExpChanged += Publish;
            Publish();
            return;
        }

        snapshot.OnValueChanged += HandleChanged;
        Apply(snapshot.Value);
    }

    public override void OnNetworkDespawn()
    {
        if (levelSystem != null) levelSystem.onExpChanged -= Publish;
        snapshot.OnValueChanged -= HandleChanged;
        levelSystem = null;
    }

    private void Publish()
    {
        if (!IsSpawned || !IsServer || levelSystem == null) return;
        snapshot.Value = new ProgressSnapshot
        {
            Level = levelSystem.level,
            CurrentExp = levelSystem.currentExp,
            RequiredExp = levelSystem.requiredExp
        };
    }

    private void HandleChanged(ProgressSnapshot previous, ProgressSnapshot current)
    {
        Apply(current);
    }

    private void Apply(ProgressSnapshot value)
    {
        if (levelSystem == null || value.Level <= 0) return;
        levelSystem.ApplyReplicated(value.Level, value.CurrentExp, value.RequiredExp);
    }
}
