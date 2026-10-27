using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

public enum NetworkLobbyPhase : byte
{
    Lobby,
    LoadingMatch,
    Running,
    Stopping
}

public enum NetworkLobbyNoticeKind : byte
{
    Joined,
    Left
}

public struct NetworkLobbyMemberSnapshot : INetworkSerializable, IEquatable<NetworkLobbyMemberSnapshot>
{
    private FixedString128Bytes displayName;

    public ulong ClientId;
    public int Champion;
    public bool Ready;
    public bool Connected;
    public bool Synchronized;
    public bool IsHost;
    public bool Occupied;

    public string DisplayName => displayName.ToString();

    internal static NetworkLobbyMemberSnapshot Empty
    {
        get
        {
            NetworkLobbyMemberSnapshot empty = default;
            empty.Champion = -1;
            return empty;
        }
    }

    internal NetworkLobbyMemberSnapshot(ulong clientId, string memberName, int champion, bool ready,
        bool connected, bool synchronized, bool isHost)
    {
        ClientId = clientId;
        displayName = new FixedString128Bytes(memberName);
        Champion = champion;
        Ready = ready;
        Connected = connected;
        Synchronized = synchronized;
        IsHost = isHost;
        Occupied = true;
    }

    public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
    {
        serializer.SerializeValue(ref ClientId);
        serializer.SerializeValue(ref displayName);
        serializer.SerializeValue(ref Champion);
        serializer.SerializeValue(ref Ready);
        serializer.SerializeValue(ref Connected);
        serializer.SerializeValue(ref Synchronized);
        serializer.SerializeValue(ref IsHost);
        serializer.SerializeValue(ref Occupied);
    }

    public bool Equals(NetworkLobbyMemberSnapshot other)
    {
        return ClientId == other.ClientId && displayName.Equals(other.displayName) &&
               Champion == other.Champion && Ready == other.Ready && Connected == other.Connected &&
               Synchronized == other.Synchronized && IsHost == other.IsHost && Occupied == other.Occupied;
    }

    public override bool Equals(object obj)
    {
        return obj is NetworkLobbyMemberSnapshot other && Equals(other);
    }

    public override int GetHashCode()
    {
        unchecked
        {
            int hash = ClientId.GetHashCode();
            hash = (hash * 397) ^ displayName.GetHashCode();
            hash = (hash * 397) ^ Champion;
            hash = (hash * 397) ^ Ready.GetHashCode();
            hash = (hash * 397) ^ Connected.GetHashCode();
            hash = (hash * 397) ^ Synchronized.GetHashCode();
            hash = (hash * 397) ^ IsHost.GetHashCode();
            return (hash * 397) ^ Occupied.GetHashCode();
        }
    }
}

public struct NetworkLobbySnapshot : INetworkSerializable, IEquatable<NetworkLobbySnapshot>
{
    private uint roomId0;
    private uint roomId1;
    private uint roomId2;
    private uint roomId3;
    private FixedString128Bytes roomName;

    public NetworkLobbyPhase Phase;
    public uint Revision;
    public uint StartRevision;
    public NetworkLobbyMemberSnapshot Member0;
    public NetworkLobbyMemberSnapshot Member1;

    public Guid RoomInstanceId => RoomGuidBits.ToGuid(roomId0, roomId1, roomId2, roomId3);
    public string RoomName => roomName.ToString();
    public int MemberCount => (Member0.Occupied ? 1 : 0) + (Member1.Occupied ? 1 : 0);

    internal NetworkLobbySnapshot(Guid roomInstanceId, string name, NetworkLobbyPhase phase)
    {
        RoomGuidBits.FromGuid(roomInstanceId, out roomId0, out roomId1, out roomId2, out roomId3);
        roomName = new FixedString128Bytes(name);
        Phase = phase;
        Revision = 1;
        StartRevision = 0;
        Member0 = NetworkLobbyMemberSnapshot.Empty;
        Member1 = NetworkLobbyMemberSnapshot.Empty;
    }

    public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
    {
        serializer.SerializeValue(ref roomId0);
        serializer.SerializeValue(ref roomId1);
        serializer.SerializeValue(ref roomId2);
        serializer.SerializeValue(ref roomId3);
        serializer.SerializeValue(ref roomName);
        serializer.SerializeValue(ref Phase);
        serializer.SerializeValue(ref Revision);
        serializer.SerializeValue(ref StartRevision);
        Member0.NetworkSerialize(serializer);
        Member1.NetworkSerialize(serializer);
    }

    public bool Equals(NetworkLobbySnapshot other)
    {
        return roomId0 == other.roomId0 && roomId1 == other.roomId1 && roomId2 == other.roomId2 &&
               roomId3 == other.roomId3 && roomName.Equals(other.roomName) && Phase == other.Phase &&
               Revision == other.Revision && StartRevision == other.StartRevision &&
               Member0.Equals(other.Member0) && Member1.Equals(other.Member1);
    }

    public override bool Equals(object obj)
    {
        return obj is NetworkLobbySnapshot other && Equals(other);
    }

    public override int GetHashCode()
    {
        unchecked
        {
            int hash = (int)roomId0;
            hash = (hash * 397) ^ (int)roomId1;
            hash = (hash * 397) ^ (int)roomId2;
            hash = (hash * 397) ^ (int)roomId3;
            hash = (hash * 397) ^ roomName.GetHashCode();
            hash = (hash * 397) ^ (int)Phase;
            hash = (hash * 397) ^ (int)Revision;
            hash = (hash * 397) ^ (int)StartRevision;
            hash = (hash * 397) ^ Member0.GetHashCode();
            return (hash * 397) ^ Member1.GetHashCode();
        }
    }
}

public struct NetworkLobbyNotice : INetworkSerializable, IEquatable<NetworkLobbyNotice>
{
    private uint roomId0;
    private uint roomId1;
    private uint roomId2;
    private uint roomId3;
    private FixedString128Bytes displayName;

    public NetworkLobbyNoticeKind Kind;
    public uint Sequence;

    public Guid RoomInstanceId => RoomGuidBits.ToGuid(roomId0, roomId1, roomId2, roomId3);
    public string DisplayName => displayName.ToString();

    internal NetworkLobbyNotice(Guid roomInstanceId, uint sequence, NetworkLobbyNoticeKind kind, string memberName)
    {
        RoomGuidBits.FromGuid(roomInstanceId, out roomId0, out roomId1, out roomId2, out roomId3);
        displayName = new FixedString128Bytes(memberName);
        Kind = kind;
        Sequence = sequence;
    }

    public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
    {
        serializer.SerializeValue(ref roomId0);
        serializer.SerializeValue(ref roomId1);
        serializer.SerializeValue(ref roomId2);
        serializer.SerializeValue(ref roomId3);
        serializer.SerializeValue(ref displayName);
        serializer.SerializeValue(ref Kind);
        serializer.SerializeValue(ref Sequence);
    }

    public bool Equals(NetworkLobbyNotice other)
    {
        return roomId0 == other.roomId0 && roomId1 == other.roomId1 && roomId2 == other.roomId2 &&
               roomId3 == other.roomId3 && displayName.Equals(other.displayName) &&
               Kind == other.Kind && Sequence == other.Sequence;
    }

    public override bool Equals(object obj)
    {
        return obj is NetworkLobbyNotice other && Equals(other);
    }

    public override int GetHashCode()
    {
        unchecked
        {
            int hash = (int)roomId0;
            hash = (hash * 397) ^ (int)roomId1;
            hash = (hash * 397) ^ (int)roomId2;
            hash = (hash * 397) ^ (int)roomId3;
            hash = (hash * 397) ^ displayName.GetHashCode();
            hash = (hash * 397) ^ (int)Kind;
            return (hash * 397) ^ (int)Sequence;
        }
    }
}

public sealed class NetworkLobbyState : NetworkBehaviour
{
    public const int MaxNotices = 16;
    private const double RequestWindowSeconds = 2.0;
    private const int MaxRequestsPerWindow = 6;

    private readonly NetworkVariable<NetworkLobbySnapshot> snapshot = new NetworkVariable<NetworkLobbySnapshot>(
        default,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);
    private readonly NetworkList<NetworkLobbyNotice> notices = new NetworkList<NetworkLobbyNotice>(
        null,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);
    private readonly Dictionary<ulong, RequestRate> requestRates = new Dictionary<ulong, RequestRate>(2);

    private uint nextNoticeSequence;

    public event Action<NetworkLobbyState> Changed;
    public event Action<NetworkLobbyState> StartRequested;

    public NetworkLobbySnapshot Snapshot => snapshot.Value;
    public NetworkList<NetworkLobbyNotice> Notices => notices;
    public bool CanStart => CanStartFromSnapshot(snapshot.Value);

    public override void OnNetworkSpawn()
    {
        snapshot.OnValueChanged += HandleSnapshotChanged;
        notices.OnListChanged += HandleNoticesChanged;
        RaiseChanged();
    }

    public override void OnNetworkDespawn()
    {
        snapshot.OnValueChanged -= HandleSnapshotChanged;
        notices.OnListChanged -= HandleNoticesChanged;
        requestRates.Clear();
    }

    public override void OnDestroy()
    {
        snapshot.OnValueChanged -= HandleSnapshotChanged;
        notices.OnListChanged -= HandleNoticesChanged;
        base.OnDestroy();
    }

    internal bool InitializeHost(Guid roomInstanceId, string roomTitle, ulong hostClientId, string hostName)
    {
        if (!IsServer || !IsSpawned || roomInstanceId == Guid.Empty ||
            !IsCanonicalValidName(hostName, out string normalizedName) ||
            !LocalPlayerProfile.TryValidateRoomName(roomTitle, out string normalizedTitle, out _))
        {
            return false;
        }

        notices.Clear();
        nextNoticeSequence = 0;
        requestRates.Clear();
        NetworkLobbySnapshot initial = new NetworkLobbySnapshot(roomInstanceId, normalizedTitle, NetworkLobbyPhase.Lobby);
        initial.Member0 = new NetworkLobbyMemberSnapshot(hostClientId, normalizedName, -1, false, true, true, true);
        initial.Revision = 1;
        snapshot.Value = initial;
        return true;
    }

    internal bool AddSynchronizedMember(ulong clientId, string displayName)
    {
        if (!IsServer || !IsSpawned || snapshot.Value.Phase != NetworkLobbyPhase.Lobby ||
            !IsCanonicalValidName(displayName, out string normalizedName))
        {
            return false;
        }

        NetworkLobbySnapshot current = snapshot.Value;
        if (FindMember(current, clientId, out _))
        {
            return true;
        }
        if (current.MemberCount >= 2 || clientId == current.Member0.ClientId)
        {
            return false;
        }

        NetworkLobbyMemberSnapshot member = new NetworkLobbyMemberSnapshot(
            clientId, normalizedName, -1, false, true, true, false);
        if (!current.Member0.Occupied)
        {
            current.Member0 = member;
        }
        else if (!current.Member1.Occupied)
        {
            current.Member1 = member;
        }
        else
        {
            return false;
        }

        current.Revision = NextNonZero(current.Revision);
        snapshot.Value = current;
        AddNotice(current.RoomInstanceId, NetworkLobbyNoticeKind.Joined, normalizedName);
        return true;
    }

    internal bool RemoveMember(ulong clientId)
    {
        if (!IsServer || !IsSpawned) return false;

        NetworkLobbySnapshot current = snapshot.Value;
        if (current.Phase != NetworkLobbyPhase.Lobby && current.Phase != NetworkLobbyPhase.Running)
            return false;

        NetworkLobbyMemberSnapshot member;
        if (current.Member0.Occupied && current.Member0.ClientId == clientId && !current.Member0.IsHost)
        {
            member = current.Member0;
            current.Member0 = NetworkLobbyMemberSnapshot.Empty;
        }
        else if (current.Member1.Occupied && current.Member1.ClientId == clientId && !current.Member1.IsHost)
        {
            member = current.Member1;
            current.Member1 = NetworkLobbyMemberSnapshot.Empty;
        }
        else
        {
            return false;
        }

        current.Revision = NextNonZero(current.Revision);
        snapshot.Value = current;
        requestRates.Remove(clientId);
        AddNotice(current.RoomInstanceId, NetworkLobbyNoticeKind.Left, member.DisplayName);
        return true;
    }

    internal bool TryLockForStart(out NetworkLobbySnapshot frozenSnapshot)
    {
        frozenSnapshot = snapshot.Value;
        if (!IsServer || !IsSpawned || !CanStartFromSnapshot(frozenSnapshot))
            return false;

        frozenSnapshot.Phase = NetworkLobbyPhase.LoadingMatch;
        frozenSnapshot.StartRevision = NextNonZero(frozenSnapshot.StartRevision);
        frozenSnapshot.Revision = NextNonZero(frozenSnapshot.Revision);
        snapshot.Value = frozenSnapshot;
        return true;
    }

    internal bool PublishRunning()
    {
        if (!IsServer || !IsSpawned || snapshot.Value.Phase != NetworkLobbyPhase.LoadingMatch)
            return false;

        NetworkLobbySnapshot current = snapshot.Value;
        current.Phase = NetworkLobbyPhase.Running;
        current.Revision = NextNonZero(current.Revision);
        snapshot.Value = current;
        return true;
    }

    internal void PublishStopping()
    {
        if (!IsServer || !IsSpawned || snapshot.Value.Phase == NetworkLobbyPhase.Stopping)
            return;

        NetworkLobbySnapshot current = snapshot.Value;
        current.Phase = NetworkLobbyPhase.Stopping;
        current.Revision = NextNonZero(current.Revision);
        snapshot.Value = current;
    }

    public void SelectChampion(PlayerCharacterId champion)
    {
        if (!IsSpawned) return;
        if (IsServer)
        {
            HandleSelectChampion(NetworkManager.LocalClientId, (int)champion);
        }
        else
        {
            SelectChampionRpc((int)champion);
        }
    }

    public void SetReady(bool ready)
    {
        if (!IsSpawned) return;
        if (IsServer)
        {
            HandleSetReady(NetworkManager.LocalClientId, ready);
        }
        else
        {
            SetReadyRpc(ready);
        }
    }

    public void RequestStart()
    {
        if (!IsSpawned) return;
        if (IsServer)
        {
            HandleRequestStart(NetworkManager.LocalClientId);
        }
        else
        {
            RequestStartRpc();
        }
    }

    private void HandleSnapshotChanged(NetworkLobbySnapshot previous, NetworkLobbySnapshot current)
    {
        RaiseChanged();
    }

    private void HandleNoticesChanged(NetworkListEvent<NetworkLobbyNotice> changeEvent)
    {
        RaiseChanged();
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    private void SelectChampionRpc(int champion, RpcParams rpcParams = default)
    {
        HandleSelectChampion(rpcParams.Receive.SenderClientId, champion);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    private void SetReadyRpc(bool ready, RpcParams rpcParams = default)
    {
        HandleSetReady(rpcParams.Receive.SenderClientId, ready);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    private void RequestStartRpc(RpcParams rpcParams = default)
    {
        HandleRequestStart(rpcParams.Receive.SenderClientId);
    }

    private void HandleSelectChampion(ulong senderClientId, int champion)
    {
        if (!IsServer || !CanAcceptLobbyRequest(senderClientId) ||
            !Enum.IsDefined(typeof(PlayerCharacterId), champion))
        {
            return;
        }

        NetworkLobbySnapshot current = snapshot.Value;
        if (!TryGetMemberSlot(current, senderClientId, out bool inFirstSlot, out NetworkLobbyMemberSnapshot member))
            return;
        if (member.Champion == champion)
            return;

        member.Champion = champion;
        member.Ready = false;
        if (inFirstSlot) current.Member0 = member;
        else current.Member1 = member;
        current.Revision = NextNonZero(current.Revision);
        snapshot.Value = current;
    }

    private void HandleSetReady(ulong senderClientId, bool ready)
    {
        if (!IsServer || !CanAcceptLobbyRequest(senderClientId) ||
            !TryGetMemberSlot(snapshot.Value, senderClientId, out bool inFirstSlot, out NetworkLobbyMemberSnapshot member) ||
            member.IsHost || (ready && !IsValidChampion(member.Champion)))
        {
            return;
        }

        if (member.Ready == ready) return;
        member.Ready = ready;
        NetworkLobbySnapshot current = snapshot.Value;
        if (inFirstSlot) current.Member0 = member;
        else current.Member1 = member;
        current.Revision = NextNonZero(current.Revision);
        snapshot.Value = current;
    }

    private void HandleRequestStart(ulong senderClientId)
    {
        if (!IsServer || !IsSpawned || senderClientId != NetworkManager.ServerClientId)
            return;

        NetworkLobbySnapshot current = snapshot.Value;
        if (current.Phase != NetworkLobbyPhase.Lobby || !FindMember(current, senderClientId, out _))
            return;

        RaiseStartRequested();
    }

    private bool CanAcceptLobbyRequest(ulong senderClientId)
    {
        NetworkLobbySnapshot current = snapshot.Value;
        if (current.Phase != NetworkLobbyPhase.Lobby || !FindMember(current, senderClientId, out _))
            return false;

        double now = Time.unscaledTimeAsDouble;
        if (!requestRates.TryGetValue(senderClientId, out RequestRate rate) ||
            now - rate.WindowStart >= RequestWindowSeconds || now < rate.WindowStart)
        {
            requestRates[senderClientId] = new RequestRate(now, 1);
            return true;
        }
        if (rate.Count >= MaxRequestsPerWindow)
            return false;

        rate.Count++;
        requestRates[senderClientId] = rate;
        return true;
    }

    private void AddNotice(Guid roomInstanceId, NetworkLobbyNoticeKind kind, string displayName)
    {
        nextNoticeSequence = NextNonZero(nextNoticeSequence);
        if (notices.Count >= MaxNotices)
            notices.RemoveAt(0);
        notices.Add(new NetworkLobbyNotice(roomInstanceId, nextNoticeSequence, kind, displayName));
    }

    private void RaiseChanged()
    {
        Action<NetworkLobbyState> listeners = Changed;
        if (listeners == null) return;
        foreach (Action<NetworkLobbyState> listener in listeners.GetInvocationList())
        {
            try
            {
                listener(this);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception, this);
            }
        }
    }

    private void RaiseStartRequested()
    {
        Action<NetworkLobbyState> listeners = StartRequested;
        if (listeners == null) return;
        foreach (Action<NetworkLobbyState> listener in listeners.GetInvocationList())
        {
            try
            {
                listener(this);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception, this);
            }
        }
    }

    private static bool CanStartFromSnapshot(NetworkLobbySnapshot current)
    {
        if (current.Phase != NetworkLobbyPhase.Lobby || !current.Member0.Occupied || !current.Member0.IsHost ||
            !current.Member0.Connected || !current.Member0.Synchronized || !IsValidChampion(current.Member0.Champion))
        {
            return false;
        }

        if (current.Member1.Occupied && (!current.Member1.Connected || !current.Member1.Synchronized ||
            current.Member1.IsHost || !IsValidChampion(current.Member1.Champion) || !current.Member1.Ready))
        {
            return false;
        }

        return current.MemberCount == 1 || current.MemberCount == 2;
    }

    private static bool FindMember(NetworkLobbySnapshot current, ulong clientId,
        out NetworkLobbyMemberSnapshot member)
    {
        if (current.Member0.Occupied && current.Member0.ClientId == clientId)
        {
            member = current.Member0;
            return true;
        }
        if (current.Member1.Occupied && current.Member1.ClientId == clientId)
        {
            member = current.Member1;
            return true;
        }
        member = default;
        return false;
    }

    private static bool TryGetMemberSlot(NetworkLobbySnapshot current, ulong clientId,
        out bool inFirstSlot, out NetworkLobbyMemberSnapshot member)
    {
        if (current.Member0.Occupied && current.Member0.ClientId == clientId)
        {
            inFirstSlot = true;
            member = current.Member0;
            return true;
        }
        if (current.Member1.Occupied && current.Member1.ClientId == clientId)
        {
            inFirstSlot = false;
            member = current.Member1;
            return true;
        }
        inFirstSlot = false;
        member = default;
        return false;
    }

    private static bool IsCanonicalValidName(string value, out string normalizedName)
    {
        normalizedName = null;
        if (!LocalPlayerProfile.TryValidateDisplayName(value, out string normalized, out _) ||
            !string.Equals(value, normalized, StringComparison.Ordinal))
        {
            return false;
        }
        normalizedName = normalized;
        return true;
    }

    private static bool IsValidChampion(int champion)
    {
        return Enum.IsDefined(typeof(PlayerCharacterId), champion);
    }

    private static uint NextNonZero(uint value)
    {
        value++;
        return value == 0 ? 1u : value;
    }

    private struct RequestRate
    {
        public readonly double WindowStart;
        public int Count;

        public RequestRate(double windowStart, int count)
        {
            WindowStart = windowStart;
            Count = count;
        }
    }
}

internal static class RoomGuidBits
{
    public static void FromGuid(Guid value, out uint part0, out uint part1, out uint part2, out uint part3)
    {
        byte[] bytes = value.ToByteArray();
        part0 = ReadUInt32LittleEndian(bytes, 0);
        part1 = ReadUInt32LittleEndian(bytes, 4);
        part2 = ReadUInt32LittleEndian(bytes, 8);
        part3 = ReadUInt32LittleEndian(bytes, 12);
    }

    public static Guid ToGuid(uint part0, uint part1, uint part2, uint part3)
    {
        return new Guid(
            unchecked((int)part0),
            unchecked((short)part1),
            unchecked((short)(part1 >> 16)),
            (byte)part2,
            (byte)(part2 >> 8),
            (byte)(part2 >> 16),
            (byte)(part2 >> 24),
            (byte)part3,
            (byte)(part3 >> 8),
            (byte)(part3 >> 16),
            (byte)(part3 >> 24));
    }

    private static uint ReadUInt32LittleEndian(byte[] source, int offset)
    {
        return (uint)source[offset] |
               ((uint)source[offset + 1] << 8) |
               ((uint)source[offset + 2] << 16) |
               ((uint)source[offset + 3] << 24);
    }
}
