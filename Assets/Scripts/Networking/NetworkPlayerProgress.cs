using System;
using Unity.Netcode;
using UnityEngine;

// 서버 권한 플레이어 성장 상태 복제
public sealed class NetworkPlayerProgress : NetworkBehaviour
{
    // 레벨·경험치 전송 묶음
    private struct ProgressSnapshot : INetworkSerializable, IEquatable<ProgressSnapshot>
    {
        // 현재 레벨·누적 경험치·다음 레벨 요구량
        public int Level;
        public int CurrentExp;
        public int RequiredExp;

        // 성장 상태 직렬화
        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            serializer.SerializeValue(ref Level);
            serializer.SerializeValue(ref CurrentExp);
            serializer.SerializeValue(ref RequiredExp);
        }

        // 복제 변경 감지용 값 비교
        public bool Equals(ProgressSnapshot other)
        {
            return Level == other.Level && CurrentExp == other.CurrentExp && RequiredExp == other.RequiredExp;
        }

        // 객체 형식 성장 상태 비교
        public override bool Equals(object obj) => obj is ProgressSnapshot other && Equals(other);

        // 성장 상태 해시
        public override int GetHashCode() => unchecked((Level * 397) ^ (CurrentExp * 31) ^ RequiredExp);
    }

    // 서버 쓰기·전체 읽기 성장 원본
    private readonly NetworkVariable<ProgressSnapshot> snapshot = new NetworkVariable<ProgressSnapshot>(
        default, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    // 로컬 성장 시스템 연결
    private PlayerLevelSystem levelSystem;

    // 필수 구성 검증·서버 발행 또는 클라이언트 구독
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

    // 성장 이벤트·복제 구독 해제
    public override void OnNetworkDespawn()
    {
        if (levelSystem != null) levelSystem.onExpChanged -= Publish;
        snapshot.OnValueChanged -= HandleChanged;
        levelSystem = null;
    }

    // 스폰된 서버의 성장 상태 발행
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

    // 수신 성장 상태 반영 진입점
    private void HandleChanged(ProgressSnapshot previous, ProgressSnapshot current)
    {
        Apply(current);
    }

    // 미초기화 레벨 제외·클라이언트 성장 복제 반영
    private void Apply(ProgressSnapshot value)
    {
        if (levelSystem == null || value.Level <= 0) return;
        levelSystem.ApplyReplicated(value.Level, value.CurrentExp, value.RequiredExp);
    }
}
