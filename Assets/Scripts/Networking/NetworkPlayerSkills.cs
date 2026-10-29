using System;
using Unity.Netcode;
using UnityEngine;

// 네트워크 시전 요청 슬롯
public enum NetworkSkillSlot { Q, E, Ultimate }

// 소유자 시전 요청·서버 판정·스킬 상태 복제
[RequireComponent(typeof(NetworkObject), typeof(PlayerSkillController), typeof(PlayerUltimateGauge))]
public sealed class NetworkPlayerSkills : NetworkBehaviour
{
    // 판정 결과와 UI용 스킬 상태 묶음
    private struct SkillSnapshot : INetworkSerializable, IEquatable<SkillSnapshot>
    {
        // 서버 시각 기준 재사용·충전 완료 시각
        public double QReady, EReady;
        // 충전 수·용량
        public int Charges, Capacity;
        // 채널링·이동·궁극기·공격 제한 상태
        public bool QChannel, EChannel, MovementLock, NinjaReady, NinjaDash, CastLock;
        // 궁극기 게이지 복제값
        public float Gauge, MaxGauge;

        // 스킬 상태 직렬화
        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            serializer.SerializeValue(ref QReady);
            serializer.SerializeValue(ref EReady);
            serializer.SerializeValue(ref Charges);
            serializer.SerializeValue(ref Capacity);
            serializer.SerializeValue(ref QChannel);
            serializer.SerializeValue(ref EChannel);
            serializer.SerializeValue(ref MovementLock);
            serializer.SerializeValue(ref NinjaReady);
            serializer.SerializeValue(ref NinjaDash);
            serializer.SerializeValue(ref CastLock);
            serializer.SerializeValue(ref Gauge);
            serializer.SerializeValue(ref MaxGauge);
        }

        // 복제 변경 감지용 전체 상태 비교
        public bool Equals(SkillSnapshot other)
        {
            return QReady == other.QReady && EReady == other.EReady &&
                Charges == other.Charges && Capacity == other.Capacity &&
                QChannel == other.QChannel && EChannel == other.EChannel &&
                MovementLock == other.MovementLock && NinjaReady == other.NinjaReady &&
                NinjaDash == other.NinjaDash && CastLock == other.CastLock &&
                Gauge == other.Gauge && MaxGauge == other.MaxGauge;
        }
    }

    // 서버 쓰기·전체 읽기 스킬 상태
    private readonly NetworkVariable<SkillSnapshot> snapshot = new NetworkVariable<SkillSnapshot>(
        default, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    // 서버 판정·로컬 표시 구성 요소
    private PlayerSkillController controller;
    private PlayerUltimateGauge gauge;
    private PlayerHealth health;
    // 프레임당 중복 시전·재진입 방지와 게임 시각 보정
    private int acceptedFrame = -1;
    private bool executing;
    private double clockOffset;

    // 복제 상태 기반 쿨다운·충전·행동 제한 조회
    public float QRemaining => Remaining(snapshot.Value.QReady);
    public float ERechargeRemaining => Remaining(snapshot.Value.EReady);
    public int ECharges => snapshot.Value.Charges;
    public int EMaxCharges => snapshot.Value.Capacity;
    public bool QChanneling => snapshot.Value.QChannel;
    public bool EChanneling => snapshot.Value.EChannel;
    public bool IsMovementLocked => snapshot.Value.MovementLock;
    public bool UltimateRecastReady => snapshot.Value.NinjaReady;
    public bool IsUltimateDashing => snapshot.Value.NinjaDash;
    // 서버 실시간 시전 잠금·클라이언트 복제 잠금
    public bool BlocksBasicAttack => IsSpawned && (IsServer
        ? executing || acceptedFrame == Time.frameCount ||
          (controller != null && controller.BlocksNetworkBasicAttack)
        : snapshot.Value.CastLock);

    // 서버 시각 기준 남은 시간·미스폰 상태 보정
    private float Remaining(double deadline)
    {
        return !IsSpawned || deadline <= 0d ? 0f :
            Mathf.Max(0f, (float)(deadline - NetworkManager.ServerTime.Time));
    }

    // 구성 연결·복제 구독·권한별 컨트롤러 활성화
    public override void OnNetworkSpawn()
    {
        controller = GetComponent<PlayerSkillController>();
        gauge = GetComponent<PlayerUltimateGauge>();
        health = GetComponent<PlayerHealth>();
        acceptedFrame = -1;
        executing = false;
        snapshot.OnValueChanged += OnSnapshotChanged;
        // 원격 소유자도 서버 쿨다운·채널링 완료 처리 유지
        controller.enabled = IsServer || IsOwner;
        if (IsServer)
        {
            clockOffset = NetworkManager.ServerTime.Time - Time.time;
            Publish();
        }
        else ApplyReplica(snapshot.Value);
    }

    // 스킬 상태 구독 해제
    public override void OnNetworkDespawn()
    {
        snapshot.OnValueChanged -= OnSnapshotChanged;
    }

    // 서버 프레임 종료 상태 발행
    private void LateUpdate()
    {
        if (IsSpawned && IsServer) Publish();
    }

    // 소유자 입력 검증·요청 제출 여부 반환, 시전 승인과 별개
    public bool RequestSkill(NetworkSkillSlot slot, Vector3 groundTarget = default, bool hasGroundTarget = false)
    {
        if (NetworkGameSession.IsNetworkSessionActive && !NetworkGameSession.IsCombatRunning) return false;
        if (!IsSpawned || !IsOwner || !ValidSlot(slot) || !Finite(groundTarget)) return false;
        var look = GetComponent<PlayerLook>();
        float pitch = look != null && look.cameraPivot != null
            ? Mathf.DeltaAngle(0f, look.cameraPivot.localEulerAngles.x) : 0f;
        if (!Finite(pitch)) return false;
        if (IsServer) Execute(slot, pitch, groundTarget, hasGroundTarget);
        else RequestSkillRpc(slot, pitch, groundTarget, hasGroundTarget);
        return true;
    }

    // 소유자 → 서버 시전 의도 전달
    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Owner)]
    private void RequestSkillRpc(NetworkSkillSlot slot, float pitch, Vector3 groundTarget, bool hasGroundTarget)
    {
        Execute(slot, pitch, groundTarget, hasGroundTarget);
    }

    // 서버 생존·전투·입력·중복 검증 후 시전 판정
    private void Execute(NetworkSkillSlot slot, float pitch, Vector3 groundTarget, bool hasGroundTarget)
    {
        if (NetworkGameSession.IsNetworkSessionActive && !NetworkGameSession.IsCombatRunning) return;
        if (health == null) health = GetComponent<PlayerHealth>();
        if (!IsSpawned || !IsServer || executing || acceptedFrame == Time.frameCount ||
            !ValidSlot(slot) || !Finite(pitch) || !Finite(groundTarget) || controller == null ||
            controller.IsPlayerDead || health == null || health.IsDead || health.CurrentHealth <= 0)
            return;

        var look = GetComponent<PlayerLook>();
        Transform pivot = look != null ? look.cameraPivot : null;
        // 요청 조준각 임시 적용·판정 후 원래 조준 복구
        Quaternion previousAim = pivot != null ? pivot.localRotation : Quaternion.identity;
        if (pivot != null)
            pivot.localRotation = Quaternion.Euler(Mathf.Clamp(pitch, look.downClamp, look.upClamp), 0f, 0f);
        executing = true;
        try
        {
            if (controller.TryCastNetworkSkill(slot, groundTarget, hasGroundTarget))
                acceptedFrame = Time.frameCount;
        }
        finally
        {
            executing = false;
            if (pivot != null) pivot.localRotation = previousAim;
            Publish();
        }
    }

    // 서버 스킬 판정 결과·게이지 발행
    private void Publish()
    {
        // 게임 시각 → 서버 시각 변환·완료 시각 미세 변동 억제
        double offset = NetworkManager.ServerTime.Time - Time.time;
        if (Math.Abs(offset - clockOffset) > 0.05d) clockOffset = offset;
        snapshot.Value = new SkillSnapshot
        {
            QReady = controller.QReadyTime > Time.time ? clockOffset + controller.QReadyTime : 0d,
            Charges = controller.ECharges,
            Capacity = controller.EMaxCharges,
            EReady = controller.EReadyTime > 0f ? clockOffset + controller.EReadyTime : 0d,
            QChannel = controller.QChanneling,
            EChannel = controller.EChanneling,
            MovementLock = controller.IsMovementLocked,
            NinjaReady = controller.UltimateRecastReady,
            NinjaDash = controller.IsUltimateDashing,
            CastLock = BlocksBasicAttack,
            Gauge = gauge.Current,
            MaxGauge = gauge.Max
        };
    }

    // 비서버 복제 상태 변경 수신
    private void OnSnapshotChanged(SkillSnapshot previous, SkillSnapshot current)
    {
        if (!IsServer) ApplyReplica(current);
    }

    // 클라이언트 게이지 표시 반영·시전 실행 없음
    private void ApplyReplica(SkillSnapshot current)
    {
        gauge.ApplyNetworkReplica(current.Gauge, current.MaxGauge);
    }

    // 허용 시전 슬롯 검증
    private static bool ValidSlot(NetworkSkillSlot slot) =>
        slot == NetworkSkillSlot.Q || slot == NetworkSkillSlot.E || slot == NetworkSkillSlot.Ultimate;
    // 비정상 실수 입력 제외
    private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    // 목표 좌표 전체 성분 검증
    private static bool Finite(Vector3 value) => Finite(value.x) && Finite(value.y) && Finite(value.z);
}
