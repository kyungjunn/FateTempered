using System;
using Unity.Netcode;
using UnityEngine;

// 서버의 현재·최대 체력을 하나의 스냅샷으로 전체 복제본에 동기화
// 스폰 전·오프라인 체력 계산은 기존 체력 컴포넌트 담당
public sealed class NetworkHealth : NetworkBehaviour
{
    // 현재·최대 체력의 일관된 전송 단위
    private struct HealthSnapshot : INetworkSerializable, IEquatable<HealthSnapshot>
    {
        // 서버에서 확정된 현재 체력·최대 체력
        public int CurrentHealth;
        public int MaxHealth;

        // 스냅샷 기준 사망 여부
        public bool IsDead => CurrentHealth <= 0;

        // 최대 체력 음수 제거 및 현재 체력 범위 제한
        public HealthSnapshot(int currentHealth, int maxHealth)
        {
            MaxHealth = Mathf.Max(0, maxHealth);
            CurrentHealth = Mathf.Clamp(currentHealth, 0, MaxHealth);
        }

        // 현재·최대 체력의 고정 순서 직렬화
        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            serializer.SerializeValue(ref CurrentHealth);
            serializer.SerializeValue(ref MaxHealth);
        }

        // 두 체력 값 비교를 통한 변경 감지
        public bool Equals(HealthSnapshot other)
        {
            return CurrentHealth == other.CurrentHealth && MaxHealth == other.MaxHealth;
        }

        // 박싱된 스냅샷의 타입·값 동등성 비교
        public override bool Equals(object obj)
        {
            return obj is HealthSnapshot other && Equals(other);
        }

        // 현재·최대 체력 조합 해시
        public override int GetHashCode()
        {
            return unchecked((CurrentHealth * 397) ^ MaxHealth);
        }
    }

    // 전체 읽기·서버 쓰기 전용 체력 복제 상태
    private readonly NetworkVariable<HealthSnapshot> healthSnapshot = new NetworkVariable<HealthSnapshot>(
        default,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    // 체력 원본·적용 대상 참조, 플레이어 우선 연결
    private PlayerHealth playerHealth;
    private EnemyHealth enemyHealth;

    // 체력 이벤트 연결 후 서버 초기 게시·클라이언트 초기 적용
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

    // 복제·원본 체력 이벤트 구독 해제
    public override void OnNetworkDespawn()
    {
        healthSnapshot.OnValueChanged -= HandleHealthSnapshotChanged;
        UnbindHealth();
    }


    // 스폰 시 체력 컴포넌트 탐색 및 변경 이벤트 연결
    private void BindHealthOnSpawn()
    {
        // 일부 프리팹의 PlayerController.Awake에서 추가되는 PlayerHealth 대응
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

    // 체력 이벤트 해제 및 참조 정리
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

    // 플레이어 체력 변경 시 서버 스냅샷 갱신 요청
    private void HandleHealthChanged()
    {
        PublishHealthSnapshot();
    }

    // 적 체력 변경 시 컴포넌트의 최신 값으로 갱신 요청
    private void HandleHealthChanged(int currentHealth, int maxHealth)
    {
        PublishHealthSnapshot();
    }

    // 스폰된 서버만 원본 체력 읽기·복제 상태 기록
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

    // 서버 → 클라이언트 체력 변경 수신, 서버 자기 적용 제외
    private void HandleHealthSnapshotChanged(HealthSnapshot previousValue, HealthSnapshot currentValue)
    {
        if (IsServer)
        {
            return;
        }

        ApplyHealthSnapshot(currentValue);
    }

    // 복제 체력을 로컬 플레이어·적 체력 컴포넌트에 반영
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
