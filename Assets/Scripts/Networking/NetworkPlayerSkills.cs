using System;
using Unity.Netcode;
using UnityEngine;

public enum NetworkSkillSlot { Q, E, Ultimate }

[RequireComponent(typeof(NetworkObject), typeof(PlayerSkillController), typeof(PlayerUltimateGauge))]
public sealed class NetworkPlayerSkills : NetworkBehaviour
{
    private struct SkillSnapshot : INetworkSerializable, IEquatable<SkillSnapshot>
    {
        public double QReady, EReady;
        public int Charges, Capacity;
        public bool QChannel, EChannel, MovementLock, NinjaReady, NinjaDash, CastLock;
        public float Gauge, MaxGauge;

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

    private readonly NetworkVariable<SkillSnapshot> snapshot = new NetworkVariable<SkillSnapshot>(
        default, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    private PlayerSkillController controller;
    private PlayerUltimateGauge gauge;
    private PlayerHealth health;
    private int acceptedFrame = -1;
    private bool executing;
    private double clockOffset;

    public float QRemaining => Remaining(snapshot.Value.QReady);
    public float ERechargeRemaining => Remaining(snapshot.Value.EReady);
    public int ECharges => snapshot.Value.Charges;
    public int EMaxCharges => snapshot.Value.Capacity;
    public bool QChanneling => snapshot.Value.QChannel;
    public bool EChanneling => snapshot.Value.EChannel;
    public bool IsMovementLocked => snapshot.Value.MovementLock;
    public bool UltimateRecastReady => snapshot.Value.NinjaReady;
    public bool IsUltimateDashing => snapshot.Value.NinjaDash;
    public bool BlocksBasicAttack => IsSpawned && (IsServer
        ? executing || acceptedFrame == Time.frameCount ||
          (controller != null && controller.BlocksNetworkBasicAttack)
        : snapshot.Value.CastLock);

    private float Remaining(double deadline)
    {
        return !IsSpawned || deadline <= 0d ? 0f :
            Mathf.Max(0f, (float)(deadline - NetworkManager.ServerTime.Time));
    }

    public override void OnNetworkSpawn()
    {
        controller = GetComponent<PlayerSkillController>();
        gauge = GetComponent<PlayerUltimateGauge>();
        health = GetComponent<PlayerHealth>();
        acceptedFrame = -1;
        executing = false;
        snapshot.OnValueChanged += OnSnapshotChanged;
        // Remote-owned controllers must keep ticking server cooldowns and channel completion.
        controller.enabled = IsServer || IsOwner;
        if (IsServer)
        {
            clockOffset = NetworkManager.ServerTime.Time - Time.time;
            Publish();
        }
        else ApplyReplica(snapshot.Value);
    }

    public override void OnNetworkDespawn()
    {
        snapshot.OnValueChanged -= OnSnapshotChanged;
    }

    private void LateUpdate()
    {
        if (IsSpawned && IsServer) Publish();
    }

    // True means intent was submitted, not that the server accepted a cast.
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

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Owner)]
    private void RequestSkillRpc(NetworkSkillSlot slot, float pitch, Vector3 groundTarget, bool hasGroundTarget)
    {
        Execute(slot, pitch, groundTarget, hasGroundTarget);
    }

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

    private void Publish()
    {
        // Keep deadlines stable between cooldown changes; map scaled gameplay time to network time.
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

    private void OnSnapshotChanged(SkillSnapshot previous, SkillSnapshot current)
    {
        if (!IsServer) ApplyReplica(current);
    }

    private void ApplyReplica(SkillSnapshot current)
    {
        gauge.ApplyNetworkReplica(current.Gauge, current.MaxGauge);
    }

    private static bool ValidSlot(NetworkSkillSlot slot) =>
        slot == NetworkSkillSlot.Q || slot == NetworkSkillSlot.E || slot == NetworkSkillSlot.Ultimate;
    private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    private static bool Finite(Vector3 value) => Finite(value.x) && Finite(value.y) && Finite(value.z);
}
