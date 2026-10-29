using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

// 서버가 모든 피어에 복제하는 방·판 진행 단계.
public enum NetworkLobbyPhase : byte
{
    Lobby, // 선택·준비·입장 허용.
    LoadingMatch, // 명단 고정·전원 씬 로드 대기.
    Running, // 권위 전투 진행.
    Ended, // 결과 공개·동일 방 복귀 가능.
    Stopping // 방 종료·세션 정리.
}

// 멤버 입장·이탈 알림 구분.
public enum NetworkLobbyNoticeKind : byte
{
    Joined,
    Left
}

// 고정 슬롯의 복제 멤버 정보; ClientId로 식별.
public struct NetworkLobbyMemberSnapshot : INetworkSerializable, IEquatable<NetworkLobbyMemberSnapshot>
{
    private FixedString128Bytes displayName;

    // 선택·준비·연결·초기 씬 동기화 및 점유 상태.
    public ulong ClientId;
    public int Champion;
    public bool Ready;
    public bool Connected;
    public bool Synchronized;
    public bool IsHost;
    public bool Occupied; // 빈 슬롯과 ClientId 0인 호스트 구분.

    // 고정 문자열 표시 이름.
    public string DisplayName => displayName.ToString();

    // 미점유 슬롯; Champion -1은 선택 없음.
    internal static NetworkLobbyMemberSnapshot Empty
    {
        get
        {
            NetworkLobbyMemberSnapshot empty = default;
            empty.Champion = -1;
            return empty;
        }
    }

    // 서버 확정 멤버의 점유 슬롯 생성.
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
        // 멤버 전체 필드의 고정 순서 송수신.
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
        // 멤버 전체 값 비교; 복제 변경 감지 기준.
        return ClientId == other.ClientId && displayName.Equals(other.displayName) &&
               Champion == other.Champion && Ready == other.Ready && Connected == other.Connected &&
               Synchronized == other.Synchronized && IsHost == other.IsHost && Occupied == other.Occupied;
    }

    public override bool Equals(object obj)
    {
        // 객체 형식 확인 후 멤버 값 비교.
        return obj is NetworkLobbyMemberSnapshot other && Equals(other);
    }

    public override int GetHashCode()
    {
        unchecked
        {
            // 멤버 동등성 필드의 해시 결합.
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

// 방 ID·단계·2인 명단의 원자적 복제 상태.
public struct NetworkLobbySnapshot : INetworkSerializable, IEquatable<NetworkLobbySnapshot>
{
    private uint roomId0;
    private uint roomId1;
    private uint roomId2;
    private uint roomId3;
    private FixedString128Bytes roomName;

    public NetworkLobbyPhase Phase;
    public uint Revision; // 모든 명단·단계 변경 세대.
    public uint StartRevision; // 판 시작 세대; 이전 판 부활 정보 혼입 방지.
    public NetworkLobbyMemberSnapshot Member0;
    public NetworkLobbyMemberSnapshot Member1;

    // 방 GUID·이름·점유 인원 조회.
    public Guid RoomInstanceId => RoomGuidBits.ToGuid(roomId0, roomId1, roomId2, roomId3);
    public string RoomName => roomName.ToString();
    public int MemberCount => (Member0.Occupied ? 1 : 0) + (Member1.Occupied ? 1 : 0);

    // 서버 초기 방 상태·빈 멤버 슬롯 생성.
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
        // 방 식별 비트·단계·세대·명단의 고정 순서 송수신.
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
        // 방·세대·명단 전체 값 비교; 복제 변경 감지 기준.
        return roomId0 == other.roomId0 && roomId1 == other.roomId1 && roomId2 == other.roomId2 &&
               roomId3 == other.roomId3 && roomName.Equals(other.roomName) && Phase == other.Phase &&
               Revision == other.Revision && StartRevision == other.StartRevision &&
               Member0.Equals(other.Member0) && Member1.Equals(other.Member1);
    }

    public override bool Equals(object obj)
    {
        // 객체 형식 확인 후 방 상태 값 비교.
        return obj is NetworkLobbySnapshot other && Equals(other);
    }

    public override int GetHashCode()
    {
        unchecked
        {
            // 방 상태 동등성 필드의 해시 결합.
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

// 멤버별 부활 예약 복제; 로컬 시간이 아닌 NGO 서버 시각 기준.
public struct ReviveSnapshotSlot : INetworkSerializable, IEquatable<ReviveSnapshotSlot>
{
    public ulong ClientId;
    public bool Pending;
    public double ReviveAtServerTime; // Pending일 때만 유효한 서버 절대 시각.

    public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
    {
        // 멤버 ID·예약 여부·서버 부활 시각 송수신.
        serializer.SerializeValue(ref ClientId);
        serializer.SerializeValue(ref Pending);
        serializer.SerializeValue(ref ReviveAtServerTime);
    }

    public bool Equals(ReviveSnapshotSlot other)
    {
        // 부활 예약 슬롯 전체 값 비교.
        return ClientId == other.ClientId && Pending == other.Pending &&
               ReviveAtServerTime.Equals(other.ReviveAtServerTime);
    }

    public override bool Equals(object obj)
    {
        // 객체 형식 확인 후 예약 슬롯 비교.
        return obj is ReviveSnapshotSlot other && Equals(other);
    }

    public override int GetHashCode()
    {
        unchecked
        {
            // 예약 슬롯 동등성 필드의 해시 결합.
            int hash = ClientId.GetHashCode();
            hash = (hash * 397) ^ Pending.GetHashCode();
            return (hash * 397) ^ ReviveAtServerTime.GetHashCode();
        }
    }
}

// 동일 판 시작 세대에 속한 두 멤버의 부활 예약 묶음.
public struct ReviveSnapshot : INetworkSerializable, IEquatable<ReviveSnapshot>
{
    public uint StartRevision;
    public ReviveSnapshotSlot Member0;
    public ReviveSnapshotSlot Member1;

    public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
    {
        // 판 시작 세대·두 예약 슬롯 송수신.
        serializer.SerializeValue(ref StartRevision);
        Member0.NetworkSerialize(serializer);
        Member1.NetworkSerialize(serializer);
    }

    public bool Equals(ReviveSnapshot other)
    {
        // 판 세대·부활 슬롯 전체 값 비교.
        return StartRevision == other.StartRevision && Member0.Equals(other.Member0) &&
               Member1.Equals(other.Member1);
    }

    public override bool Equals(object obj)
    {
        // 객체 형식 확인 후 부활 묶음 비교.
        return obj is ReviveSnapshot other && Equals(other);
    }

    public override int GetHashCode()
    {
        unchecked
        {
            // 판 세대·두 슬롯 해시 결합.
            int hash = (int)StartRevision;
            hash = (hash * 397) ^ Member0.GetHashCode();
            return (hash * 397) ^ Member1.GetHashCode();
        }
    }
}

// 방 인스턴스·순번으로 구분하는 입장·이탈 복제 알림.
public struct NetworkLobbyNotice : INetworkSerializable, IEquatable<NetworkLobbyNotice>
{
    private uint roomId0;
    private uint roomId1;
    private uint roomId2;
    private uint roomId3;
    private FixedString128Bytes displayName;

    public NetworkLobbyNoticeKind Kind;
    public uint Sequence;

    // 알림 대상 방·표시 이름.
    public Guid RoomInstanceId => RoomGuidBits.ToGuid(roomId0, roomId1, roomId2, roomId3);
    public string DisplayName => displayName.ToString();

    // 서버 알림 생성; 표시 이름은 메시지용 값.
    internal NetworkLobbyNotice(Guid roomInstanceId, uint sequence, NetworkLobbyNoticeKind kind, string memberName)
    {
        RoomGuidBits.FromGuid(roomInstanceId, out roomId0, out roomId1, out roomId2, out roomId3);
        displayName = new FixedString128Bytes(memberName);
        Kind = kind;
        Sequence = sequence;
    }

    public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
    {
        // 방 식별 비트·표시 이름·종류·순번 송수신.
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
        // 알림 전체 값 비교; 복제 목록 변경 감지 기준.
        return roomId0 == other.roomId0 && roomId1 == other.roomId1 && roomId2 == other.roomId2 &&
               roomId3 == other.roomId3 && displayName.Equals(other.displayName) &&
               Kind == other.Kind && Sequence == other.Sequence;
    }

    public override bool Equals(object obj)
    {
        // 객체 형식 확인 후 알림 비교.
        return obj is NetworkLobbyNotice other && Equals(other);
    }

    public override int GetHashCode()
    {
        unchecked
        {
            // 알림 동등성 필드의 해시 결합.
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

// 서버만 쓰는 복제 로비; 클라이언트 입력은 송신자 검증 RPC로 요청.
public sealed class NetworkLobbyState : NetworkBehaviour
{
    // 알림 보관 한도·클라이언트별 요청 빈도 제한.
    public const int MaxNotices = 16;
    private const double RequestWindowSeconds = 2.0;
    private const int MaxRequestsPerWindow = 6;

    // 서버 권위 상태·부활 예약·알림 목록; 모든 피어 읽기 허용.
    private readonly NetworkVariable<NetworkLobbySnapshot> snapshot = new NetworkVariable<NetworkLobbySnapshot>(
        default,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);
    private readonly NetworkVariable<ReviveSnapshot> reviveSnapshot = new NetworkVariable<ReviveSnapshot>(
        default,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);
    private readonly NetworkList<NetworkLobbyNotice> notices = new NetworkList<NetworkLobbyNotice>(
        null,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);
    // 서버 로컬 요청 제한 기록; 네트워크 복제 대상 아님.
    private readonly Dictionary<ulong, RequestRate> requestRates = new Dictionary<ulong, RequestRate>(2);

    private uint nextNoticeSequence;

    public event Action<NetworkLobbyState> Changed;
    public event Action<NetworkLobbyState> StartRequested;
    // 판 종료 결과. 서버는 PublishEnded에서, 클라이언트는 RPC로 한 번 받는다.
    public event Action<MatchResult> MatchResultReceived;

    // 복제 스냅샷·부활·알림 조회 및 시작 가능 여부.
    public NetworkLobbySnapshot Snapshot => snapshot.Value;
    public ReviveSnapshot Revives => reviveSnapshot.Value;
    public NetworkList<NetworkLobbyNotice> Notices => notices;
    public bool CanStart => CanStartFromSnapshot(snapshot.Value);

    public override void OnNetworkSpawn()
    {
        // 복제 변경 구독·현재 상태의 로컬 알림.
        snapshot.OnValueChanged += HandleSnapshotChanged;
        notices.OnListChanged += HandleNoticesChanged;
        RaiseChanged();
    }

    public override void OnNetworkDespawn()
    {
        // 복제 수명 종료 시 구독·서버 요청 기록 해제.
        snapshot.OnValueChanged -= HandleSnapshotChanged;
        notices.OnListChanged -= HandleNoticesChanged;
        requestRates.Clear();
    }

    public override void OnDestroy()
    {
        // 파괴 경로에서도 구독 해제·NGO 기본 정리 수행.
        snapshot.OnValueChanged -= HandleSnapshotChanged;
        notices.OnListChanged -= HandleNoticesChanged;
        base.OnDestroy();
    }

    // 스폰된 서버만 방·호스트 초기화; 호스트는 연결·동기화 완료 상태.
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
        ClearRevives(initial.StartRevision);
        return true;
    }

    // 서버 로비에 초기 동기화 완료 멤버 추가·입장 알림 게시.
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

    // 서버 비호스트 이탈 반영; 로딩 중 명단 변경 금지.
    internal bool RemoveMember(ulong clientId)
    {
        if (!IsServer || !IsSpawned) return false;

        NetworkLobbySnapshot current = snapshot.Value;
        if (current.Phase != NetworkLobbyPhase.Lobby && current.Phase != NetworkLobbyPhase.Running &&
            current.Phase != NetworkLobbyPhase.Ended)
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
        ClearRevives(current.StartRevision);
        requestRates.Remove(clientId);
        AddNotice(current.RoomInstanceId, NetworkLobbyNoticeKind.Left, member.DisplayName);
        return true;
    }

    // 서버 시작 조건 확인·판 세대 증가·LoadingMatch 명단 고정.
    internal bool TryLockForStart(out NetworkLobbySnapshot frozenSnapshot)
    {
        frozenSnapshot = snapshot.Value;
        if (!IsServer || !IsSpawned || !CanStartFromSnapshot(frozenSnapshot))
            return false;

        frozenSnapshot.Phase = NetworkLobbyPhase.LoadingMatch;
        frozenSnapshot.StartRevision = NextNonZero(frozenSnapshot.StartRevision);
        frozenSnapshot.Revision = NextNonZero(frozenSnapshot.Revision);
        snapshot.Value = frozenSnapshot;
        ClearRevives(frozenSnapshot.StartRevision);
        return true;
    }

    // 서버가 전원 로드·스폰 완료 후 Running 공개.
    internal bool PublishRunning()
    {
        if (!IsServer || !IsSpawned || snapshot.Value.Phase != NetworkLobbyPhase.LoadingMatch)
            return false;

        NetworkLobbySnapshot current = snapshot.Value;
        current.Phase = NetworkLobbyPhase.Running;
        current.Revision = NextNonZero(current.Revision);
        snapshot.Value = current;
        ClearRevives(current.StartRevision);
        return true;
    }

    // Running 서버의 현재 판 부활 예약만 변경 시 복제.
    internal bool PublishRevives(ReviveSnapshot value)
    {
        if (!IsServer || !IsSpawned || snapshot.Value.Phase != NetworkLobbyPhase.Running ||
            value.StartRevision != snapshot.Value.StartRevision)
        {
            return false;
        }

        if (!reviveSnapshot.Value.Equals(value))
            reviveSnapshot.Value = value;
        return true;
    }

    // 서버: 전원 쓰러짐. Running에서 한 번만.
    internal bool PublishEnded(MatchResult result)
    {
        if (!IsServer || !IsSpawned || snapshot.Value.Phase != NetworkLobbyPhase.Running)
            return false;

        NetworkLobbySnapshot current = snapshot.Value;
        current.Phase = NetworkLobbyPhase.Ended;
        current.Revision = NextNonZero(current.Revision);
        snapshot.Value = current;
        ClearRevives(current.StartRevision);
        try
        {
            MatchResultRpc(result);
        }
        catch (Exception exception)
        {
            Debug.LogException(exception, this);
            return false;
        }
        RaiseMatchResultReceived(result);
        return true;
    }

    // 서버: 결과 화면에서 같은 방의 로비로 돌아간다. 팀원은 다시 준비해야 한다.
    internal bool ReturnToLobby()
    {
        if (!IsServer || !IsSpawned || snapshot.Value.Phase != NetworkLobbyPhase.Ended)
            return false;

        NetworkLobbySnapshot current = snapshot.Value;
        current.Phase = NetworkLobbyPhase.Lobby;
        if (current.Member0.Occupied && !current.Member0.IsHost) current.Member0.Ready = false;
        if (current.Member1.Occupied && !current.Member1.IsHost) current.Member1.Ready = false;
        current.Revision = NextNonZero(current.Revision);
        requestRates.Clear();
        snapshot.Value = current;
        ClearRevives(current.StartRevision);
        return true;
    }

    [Rpc(SendTo.NotServer, InvokePermission = RpcInvokePermission.Server)]
    private void MatchResultRpc(MatchResult result)
    {
        // 서버 권위 결과의 클라이언트 전달; 호스트 중복 수신 제외.
        RaiseMatchResultReceived(result);
    }

    // 결과 구독자별 예외 격리 통지.
    private void RaiseMatchResultReceived(MatchResult result)
    {
        Action<MatchResult> listeners = MatchResultReceived;
        if (listeners == null) return;
        foreach (Action<MatchResult> listener in listeners.GetInvocationList())
        {
            try
            {
                listener(result);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception, this);
            }
        }
    }

    // 서버 종료 단계 공개·부활 예약 제거.
    internal void PublishStopping()
    {
        if (!IsServer || !IsSpawned || snapshot.Value.Phase == NetworkLobbyPhase.Stopping)
            return;

        NetworkLobbySnapshot current = snapshot.Value;
        current.Phase = NetworkLobbyPhase.Stopping;
        current.Revision = NextNonZero(current.Revision);
        snapshot.Value = current;
        ClearRevives(current.StartRevision);
    }

    // 서버 판 세대 유지·예약 슬롯만 초기화.
    private void ClearRevives(uint startRevision)
    {
        if (!IsServer || !IsSpawned) return;

        ReviveSnapshot cleared = new ReviveSnapshot { StartRevision = startRevision };
        if (!reviveSnapshot.Value.Equals(cleared))
            reviveSnapshot.Value = cleared;
    }

    // 호스트 직접 처리·클라이언트 서버 RPC 요청.
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

    // 로컬 준비 입력의 서버 권한 처리 경로 선택.
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

    // 로컬 시작 입력 전달; 실제 권한은 서버 호스트로 제한.
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

    // 복제 스냅샷 변경의 로컬 UI 알림.
    private void HandleSnapshotChanged(NetworkLobbySnapshot previous, NetworkLobbySnapshot current)
    {
        RaiseChanged();
    }

    // 복제 알림 목록 변경의 로컬 UI 알림.
    private void HandleNoticesChanged(NetworkListEvent<NetworkLobbyNotice> changeEvent)
    {
        RaiseChanged();
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    private void SelectChampionRpc(int champion, RpcParams rpcParams = default)
    {
        // 클라이언트 지정 ID 대신 NGO 실제 송신자의 선택 요청 처리.
        HandleSelectChampion(rpcParams.Receive.SenderClientId, champion);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    private void SetReadyRpc(bool ready, RpcParams rpcParams = default)
    {
        // NGO 실제 송신자의 준비 상태만 변경 요청.
        HandleSetReady(rpcParams.Receive.SenderClientId, ready);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    private void RequestStartRpc(RpcParams rpcParams = default)
    {
        // 실제 송신자 기반 서버 시작 권한 검증으로 전달.
        HandleRequestStart(rpcParams.Receive.SenderClientId);
    }

    // 서버 멤버·빈도·캐릭터 검증 후 선택 변경; 기존 준비 해제.
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

    // 비호스트 멤버만 준비 변경; 준비 시 유효 캐릭터 필수.
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

    // 서버 호스트의 로비 시작만 세션 계층에 알림; 씬 전이는 세션 담당.
    private void HandleRequestStart(ulong senderClientId)
    {
        if (!IsServer || !IsSpawned || senderClientId != NetworkManager.ServerClientId)
            return;

        NetworkLobbySnapshot current = snapshot.Value;
        if (current.Phase != NetworkLobbyPhase.Lobby || !FindMember(current, senderClientId, out _))
            return;

        RaiseStartRequested();
    }

    // 로비 멤버 여부·비배율 시간 기반 요청 빈도 검증.
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

    // 서버 알림 순번 발급·한도 초과 시 가장 오래된 항목 제거.
    private void AddNotice(Guid roomInstanceId, NetworkLobbyNoticeKind kind, string displayName)
    {
        nextNoticeSequence = NextNonZero(nextNoticeSequence);
        if (notices.Count >= MaxNotices)
            notices.RemoveAt(0);
        notices.Add(new NetworkLobbyNotice(roomInstanceId, nextNoticeSequence, kind, displayName));
    }

    // 상태 변경 구독자별 예외 격리 통지.
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

    // 시작 요청 구독자별 예외 격리 통지.
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

    // 호스트 선택·연결·동기화 필수; 팀원은 준비까지 완료해야 시작 가능.
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

    // 점유 슬롯에서 ClientId에 해당하는 멤버 조회.
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

    // 멤버 값과 수정할 슬롯 위치 조회.
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

    // 유효하며 이미 정규화된 표시 이름만 허용.
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

    // 정의된 PlayerCharacterId 범위 검증.
    private static bool IsValidChampion(int champion)
    {
        return Enum.IsDefined(typeof(PlayerCharacterId), champion);
    }

    // 오버플로 후에도 미초기화 값 0을 건너뛰는 세대·순번 증가.
    private static uint NextNonZero(uint value)
    {
        value++;
        return value == 0 ? 1u : value;
    }

    // 서버 로컬 송신자별 요청 시간 창·누적 횟수.
    private struct RequestRate
    {
        public readonly double WindowStart;
        public int Count;

        // 새 요청 시간 창·누적 횟수 생성.
        public RequestRate(double windowStart, int count)
        {
            WindowStart = windowStart;
            Count = count;
        }
    }
}

// NGO 지원 기본형 4개로 Guid 비트를 손실 없이 변환.
internal static class RoomGuidBits
{
    // Guid 바이트 배열을 리틀 엔디언 uint 4개로 분할.
    public static void FromGuid(Guid value, out uint part0, out uint part1, out uint part2, out uint part3)
    {
        byte[] bytes = value.ToByteArray();
        part0 = ReadUInt32LittleEndian(bytes, 0);
        part1 = ReadUInt32LittleEndian(bytes, 4);
        part2 = ReadUInt32LittleEndian(bytes, 8);
        part3 = ReadUInt32LittleEndian(bytes, 12);
    }

    // 분할 비트를 Guid 필드 배치에 맞춰 복원.
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

    // 바이트 4개의 명시적 리틀 엔디언 결합.
    private static uint ReadUInt32LittleEndian(byte[] source, int offset)
    {
        return (uint)source[offset] |
               ((uint)source[offset + 1] << 8) |
               ((uint)source[offset + 2] << 16) |
               ((uint)source[offset + 3] << 24);
    }
}
