using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using Stopwatch = System.Diagnostics.Stopwatch;

// 탐색 수명주기 — 시작·종료 대기와 실제 동작·오류 상태 구분
public enum LanRoomDiscoveryState
{
    Idle,
    Starting,
    Browsing,
    Hosting,
    Stopping,
    Error
}

// UI 전달용 상태·안내 메시지 스냅샷
public sealed class LanRoomDiscoveryStatus
{
    public LanRoomDiscoveryState State { get; private set; }
    public string Message { get; private set; }

    // 상태 통지 값 구성
    internal LanRoomDiscoveryStatus(LanRoomDiscoveryState state, string message)
    {
        State = state;
        Message = message;
    }
}

// 발견 방의 읽기 전용 스냅샷 — 광고 정보는 인증된 신원 아님
public sealed class LanDiscoveredRoom
{
    // 방 식별·실제 UDP 송신 주소·게임 포트·입장 상태·광고 최신성
    public Guid RoomInstanceId { get; private set; }
    public string RoomName { get; private set; }
    public IPAddress Address { get; private set; }
    public ushort GamePort { get; private set; }
    public byte Occupancy { get; private set; }
    public byte Capacity { get; private set; }
    public bool Accepting { get; private set; }
    public uint AdvertisementSequence { get; private set; }
    public bool IsJoinable { get { return Accepting && Occupancy < Capacity; } }
    public IPEndPoint Endpoint { get { return new IPEndPoint(Address, GamePort); } }

    // 만료 판정용 단조 시계 수신 시각
    internal long LastSeenTimestamp { get; private set; }

    // 검증 광고와 송신 주소·수신 시각 결합
    internal LanDiscoveredRoom(
        Guid roomInstanceId,
        string roomName,
        IPAddress address,
        ushort gamePort,
        byte occupancy,
        byte capacity,
        bool accepting,
        uint advertisementSequence,
        long lastSeenTimestamp)
    {
        RoomInstanceId = roomInstanceId;
        RoomName = roomName;
        Address = address;
        GamePort = gamePort;
        Occupancy = occupancy;
        Capacity = capacity;
        Accepting = accepting;
        AdvertisementSequence = advertisementSequence;
        LastSeenTimestamp = lastSeenTimestamp;
    }
}

// 단일 IPv4 탐색·호스트 응답 작업자와 메인 스레드 UI 연결
// 수명주기 API·이벤트는 메인 스레드 소유, 소켓 입출력은 작업자 소유
// 시작 반환값은 요청 수락 여부, 실제 바인딩 결과는 비동기 StatusChanged 통지
public sealed class LanRoomDiscovery : MonoBehaviour
{
    // 큐·프레임 처리·방 목록·질의 이력·수신량·인터페이스 수의 자원 상한
    private const int MaxQueuedMessages = 64;
    private const int MaxMessagesPerFrame = 16;
    private const int MaxRows = 64;
    private const int MaxRecentQueries = 4;
    private const int MaxBrowserPacketsPerSecond = 128;
    private const int MaxHostQueriesPerSecond = 128;
    private const int MaxBroadcastAddresses = 32;
    // 초과 데이터그램 감지용 여유 1바이트와 취소 재확인용 수신 타임아웃
    private const int ReceiveBufferLength = LanDiscoveryProtocol.MaxDatagramBytes + 1;
    private const int ReceiveTimeoutMilliseconds = 200;
    // 방·질의 난수 만료와 출발지별 응답 간격·이력 보존 시간
    private const double RoomExpirySeconds = 3.0;
    private const double QueryNonceLifetimeSeconds = 3.0;
    private const double HostSourceRateSeconds = 1.0;
    private const double HostRateEntryLifetimeSeconds = 10.0;
    // 응답 증폭·출발지 추적 메모리 제한 — 송신자 인증 대체 아님
    private const int MaxHostSources = 64;
    private const int MaxHostResponsesPerSecond = 8;

    // 빈 방 목록의 공유 읽기 전용 값
    private static readonly IReadOnlyList<LanDiscoveredRoom> EmptyRoomList =
        Array.AsReadOnly(new LanDiscoveredRoom[0]);

    // 메인 스레드 상태·방 목록 변경 통지
    public event Action<LanRoomDiscoveryStatus> StatusChanged;
    public event Action<IReadOnlyList<LanDiscoveredRoom>> RoomsChanged;

    // 외부 조회용 스냅샷과 현재 호스팅 방 ID
    public LanRoomDiscoveryStatus Status { get { return _status; } }
    public IReadOnlyList<LanDiscoveredRoom> Rooms { get { return _roomSnapshot; } }
    public Guid HostedRoomInstanceId { get; private set; }

    // 취소 완료·후속 시작 없음·메인 스레드 전달 큐 소진 여부
    public bool IsFullyStopped
    {
        get
        {
            bool workerComplete = _worker == null || (_worker.Task != null && _worker.Task.IsCompleted);
            lock (_messageLock)
            {
                return _requestedMode == RequestedMode.None && _pendingStart == null && workerComplete && _messages.Count == 0;
            }
        }
    }

    // 스레드 공유 큐만 잠금 보호, 방 사전·UI 상태는 메인 스레드 전용
    private readonly object _messageLock = new object();
    private readonly LinkedList<WorkerMessage> _messages = new LinkedList<WorkerMessage>();
    private readonly Dictionary<RoomKey, LanDiscoveredRoom> _rooms = new Dictionary<RoomKey, LanDiscoveredRoom>();

    // 공개 스냅샷·현재 작업자·교체 시작 요청·호스트 광고 참조
    private LanRoomDiscoveryStatus _status = new LanRoomDiscoveryStatus(LanRoomDiscoveryState.Idle, "LAN discovery is idle.");
    private IReadOnlyList<LanDiscoveredRoom> _roomSnapshot = EmptyRoomList;
    private WorkerSession _worker;
    private StartRequest _pendingStart;
    private HostRequest _hostRequest;
    private RequestedMode _requestedMode;
    // 이전 작업자의 늦은 메시지 차단용 세대와 API 호출 스레드 식별자
    private int _generation;
    private int _mainThreadId;
    // 탐색 준비 후 빈 목록 안내·파괴 이후 재시작 방지 상태
    private long _browserReadyTimestamp;
    private string _browserReadyMessage;
    private bool _noRoomsNoticeReported;
    private bool _destroyed;

    // 작업자 실제 실행 역할
    private enum WorkerMode
    {
        Browsing,
        Hosting
    }

    // 메인 스레드 희망 상태 — None은 취소·정지 의도
    private enum RequestedMode
    {
        None,
        Browsing,
        Hosting
    }

    // 작업자에서 메인 스레드로 전달하는 결과 종류
    private enum WorkerMessageKind
    {
        Ready,
        Room,
        Error
    }

    // 수명주기 API 소유 스레드 기록
    private void Awake()
    {
        _mainThreadId = Thread.CurrentThread.ManagedThreadId;
    }

    // 완료 회수·후속 시작·메시지 반영·방 만료의 메인 스레드 펌프
    private void Update()
    {
        ReapCompletedWorker();
        if (_worker == null && _pendingStart != null)
        {
            LaunchPendingWorker();
        }

        DrainWorkerMessages();
        if (_requestedMode == RequestedMode.Browsing)
        {
            long now = Stopwatch.GetTimestamp();
            ExpireRooms(now);
            ReportNoRoomsIfNeeded(now);
        }
    }

    // 비활성화 시 비차단 정지 요청
    private void OnDisable()
    {
        Stop();
    }

    // 파괴 후 재시작 금지와 정지 요청
    private void OnDestroy()
    {
        _destroyed = true;
        Stop();
    }

    // 앱 종료 후 재시작 금지와 정지 요청
    private void OnApplicationQuit()
    {
        _destroyed = true;
        Stop();
    }

    // IPv4 브로드캐스트·루프백 주기 질의 시작 — 선택 방 접속 전 Stop 필요
    public bool StartBrowsing()
    {
        EnsureMainThread();
        string error;
        if (!CanStart(out error))
        {
            SetStatus(LanRoomDiscoveryState.Error, error);
            return false;
        }

        RequestStart(new StartRequest(WorkerMode.Browsing, null));
        return true;
    }

    // 로비 준비 후 세션 조정자가 생성한 방 ID로 호스트 응답 시작
    public bool StartHosting(
        Guid roomInstanceId,
        string roomName,
        ushort gamePort,
        byte occupancy,
        bool accepting,
        out string error)
    {
        EnsureMainThread();
        if (roomInstanceId == Guid.Empty)
        {
            error = "The room instance ID must be nonempty and created by the session coordinator.";
            return false;
        }

        string normalizedName;
        if (!LocalPlayerProfile.TryValidateRoomName(roomName, out normalizedName, out error))
        {
            return false;
        }

        if (gamePort == 0)
        {
            error = "The game port must be between 1 and 65535.";
            return false;
        }

        if (occupancy < 1 || occupancy > LanDiscoveryProtocol.RoomCapacity ||
            (accepting && occupancy >= LanDiscoveryProtocol.RoomCapacity))
        {
            error = "A hosted room must contain one or two players, and a full room cannot accept players.";
            return false;
        }

        if (!CanStart(out error))
        {
            SetStatus(LanRoomDiscoveryState.Error, error);
            return false;
        }

        HostRequest hostRequest = new HostRequest(roomInstanceId, gamePort, normalizedName, occupancy, accepting);
        RequestStart(new StartRequest(WorkerMode.Hosting, hostRequest));
        error = null;
        return true;
    }

    // 방 ID 유지·광고 불변 스냅샷 원자 교체 — 작업자는 Volatile.Read로 참조
    public bool UpdateAdvertisement(string roomName, byte occupancy, bool accepting, out string error)
    {
        EnsureMainThread();
        error = null;
        if (_requestedMode != RequestedMode.Hosting || _hostRequest == null ||
            (_status.State != LanRoomDiscoveryState.Starting && _status.State != LanRoomDiscoveryState.Hosting))
        {
            error = "This service is not starting or hosting a room.";
            return false;
        }

        string normalizedName;
        if (!LocalPlayerProfile.TryValidateRoomName(roomName, out normalizedName, out error))
        {
            return false;
        }

        if (occupancy < 1 || occupancy > LanDiscoveryProtocol.RoomCapacity ||
            (accepting && occupancy >= LanDiscoveryProtocol.RoomCapacity))
        {
            error = "A hosted room must contain one or two players, and a full room cannot accept players.";
            return false;
        }

        Interlocked.Exchange(ref _hostRequest.Advertisement, new HostAdvertisement(normalizedName, occupancy, accepting));
        error = null;
        return true;
    }

    // 세대 무효화·취소·소켓 종료 요청 — 완료 대기는 Update에서 비차단 처리
    public void Stop()
    {
        EnsureMainThread();
        if (_requestedMode == RequestedMode.None && _pendingStart == null &&
            _status.State != LanRoomDiscoveryState.Stopping && _status.State != LanRoomDiscoveryState.Error &&
            _rooms.Count == 0 && _worker == null)
        {
            return;
        }

        AdvanceGeneration();
        _requestedMode = RequestedMode.None;
        _pendingStart = null;
        _hostRequest = null;
        HostedRoomInstanceId = Guid.Empty;

        if (_worker != null && _worker.Task != null && !_worker.Task.IsCompleted)
        {
            _worker.CancelAndClose();
            ClearWorkerMessages();
            ClearRooms();
            SetStatus(LanRoomDiscoveryState.Stopping, "Stopping the LAN discovery worker.");
            return;
        }

        ClearWorkerMessages();
        ClearRooms();
        ReapCompletedWorker();
        SetStatus(LanRoomDiscoveryState.Idle, "LAN discovery is idle.");
    }

    // 파괴·앱 종료·컴포넌트 비활성화 상태의 시작 차단
    private bool CanStart(out string error)
    {
        error = null;
        if (_destroyed)
        {
            error = "LAN discovery cannot start after the service is destroyed or the application is quitting.";
            return false;
        }

        if (!isActiveAndEnabled)
        {
            error = "Enable the LAN discovery component before starting it.";
            return false;
        }

        return true;
    }

    // 새 세대 Starting 전환·이전 작업자 취소·후속 시작 예약
    private void RequestStart(StartRequest request)
    {
        AdvanceGeneration();
        _requestedMode = request.Mode == WorkerMode.Browsing ? RequestedMode.Browsing : RequestedMode.Hosting;
        _hostRequest = request.Host;
        HostedRoomInstanceId = request.Host == null ? Guid.Empty : request.Host.RoomInstanceId;
        _pendingStart = request;
        _noRoomsNoticeReported = false;
        _browserReadyTimestamp = 0;
        _browserReadyMessage = null;
        ClearRooms();
        SetStatus(LanRoomDiscoveryState.Starting,
            request.Mode == WorkerMode.Browsing ? "Starting IPv4 LAN discovery." : "Starting the LAN room responder.");

        if (_worker != null)
        {
            _worker.CancelAndClose();
        }

        ClearWorkerMessages();
        if (_worker == null)
        {
            LaunchPendingWorker();
        }
    }

    // 이전 작업자 회수 후 단일 백그라운드 작업자 생성
    private void LaunchPendingWorker()
    {
        if (_pendingStart == null || _worker != null)
        {
            return;
        }

        StartRequest request = _pendingStart;
        _pendingStart = null;
        WorkerSession session = new WorkerSession(_generation, request);
        _worker = session;
        try
        {
            session.Task = Task.Run(() => RunWorker(session));
        }
        catch (Exception exception)
        {
            _worker = null;
            session.Dispose();
            if (session.Generation == _generation)
            {
                FailCurrentMode("Could not start the LAN discovery worker: " + exception.Message);
            }
        }
    }

    // 완료 작업자 자원 회수·오류 반영·후속 시작 또는 Idle 전환
    private void ReapCompletedWorker()
    {
        if (_worker == null || _worker.Task == null || !_worker.Task.IsCompleted)
        {
            return;
        }

        WorkerSession completed = _worker;
        _worker = null;
        try
        {
            completed.Task.GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            if (completed.Generation == _generation && _requestedMode != RequestedMode.None)
            {
                FailCurrentMode("The LAN discovery worker stopped unexpectedly: " + exception.Message);
            }
        }
        finally
        {
            completed.Dispose();
        }

        if (_worker == null && _pendingStart != null)
        {
            LaunchPendingWorker();
        }
        else if (_worker == null && _requestedMode == RequestedMode.None && _status.State == LanRoomDiscoveryState.Stopping)
        {
            SetStatus(LanRoomDiscoveryState.Idle, "LAN discovery is idle.");
        }
    }

    // 작업자 소유 UDP 소켓 생성·바인딩·역할 실행·예외 통지·정리
    private void RunWorker(WorkerSession session)
    {
        Socket socket = null;
        try
        {
            if (session.Token.IsCancellationRequested)
            {
                return;
            }

            socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.ReceiveBufferSize = 8192;
            socket.SendBufferSize = 2048;
            socket.ReceiveTimeout = ReceiveTimeoutMilliseconds;
            if (session.Request.Mode == WorkerMode.Browsing)
            {
                socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true);
            }

            int bindPort = session.Request.Mode == WorkerMode.Hosting ? LanDiscoveryProtocol.DiscoveryPort : 0;
            socket.Bind(new IPEndPoint(IPAddress.Any, bindPort));
            if (!session.TryAttachSocket(socket))
            {
                return;
            }

            socket = null;
            Socket workerSocket = session.GetSocket();
            if (workerSocket == null || session.Token.IsCancellationRequested)
            {
                return;
            }

            if (session.Request.Mode == WorkerMode.Browsing)
            {
                RunBrowserWorker(session, workerSocket);
            }
            else
            {
                RunHostWorker(session, workerSocket);
            }
        }
        catch (Exception exception)
        {
            if (!session.Token.IsCancellationRequested)
            {
                string message = session.Request.Mode == WorkerMode.Hosting
                    ? "Could not bind or run IPv4 discovery UDP port " + LanDiscoveryProtocol.DiscoveryPort + ": " + exception.Message
                    : "Could not start or run IPv4 LAN discovery: " + exception.Message;
                EnqueueWorkerMessage(session, new WorkerMessage(session.Generation, WorkerMessageKind.Error, message));
            }
        }
        finally
        {
            if (socket != null)
            {
                socket.Dispose();
            }

            session.Dispose();
        }
    }

    // 초당 질의·제한된 수신 처리·최근 난수 일치 광고만 메인 스레드 전달
    private void RunBrowserWorker(WorkerSession session, Socket socket)
    {
        List<IPEndPoint> destinations = GetBroadcastDestinations();
        string readyMessage = destinations.Count > 1
            ? "Browsing local IPv4 broadcast networks and loopback."
            : "Browsing loopback; no active IPv4 broadcast interface was found.";
        EnqueueWorkerMessage(session, new WorkerMessage(
            session.Generation,
            WorkerMessageKind.Ready,
            WorkerMode.Browsing,
            readyMessage));

        byte[] queryBuffer = new byte[LanDiscoveryProtocol.MaxDatagramBytes];
        byte[] receiveBuffer = new byte[ReceiveBufferLength];
        RecentQuery[] recentQueries = new RecentQuery[MaxRecentQueries];
        int nextRecentQuery = 0;
        long nextQueryTimestamp = 0;
        long packetWindowStart = Stopwatch.GetTimestamp();
        int packetCount = 0;

        // 예측 어려운 요청 난수 — 무관·오래된 응답 필터이며 호스트 인증은 아님
        using (RandomNumberGenerator random = RandomNumberGenerator.Create())
        {
            while (!session.Token.IsCancellationRequested)
            {
                long now = Stopwatch.GetTimestamp();
                if (now >= nextQueryTimestamp)
                {
                    ulong nonce = CreateRequestNonce(random, recentQueries, now);
                    int queryLength;
                    if (LanDiscoveryProtocol.TryEncodeQuery(nonce, queryBuffer, out queryLength))
                    {
                        recentQueries[nextRecentQuery] = new RecentQuery(nonce, now);
                        nextRecentQuery = (nextRecentQuery + 1) % recentQueries.Length;
                        for (int index = 0; index < destinations.Count && !session.Token.IsCancellationRequested; index++)
                        {
                            try
                            {
                                socket.SendTo(queryBuffer, 0, queryLength, SocketFlags.None, destinations[index]);
                            }
                            catch (SocketException)
                            {
                                if (session.Token.IsCancellationRequested)
                                {
                                    break;
                                }
                            }
                        }
                    }

                    nextQueryTimestamp = now + Stopwatch.Frequency;
                }

                EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                int received;
                try
                {
                    received = socket.ReceiveFrom(receiveBuffer, 0, receiveBuffer.Length, SocketFlags.None, ref remote);
                }
                catch (SocketException exception)
                {
                    if (session.Token.IsCancellationRequested)
                    {
                        break;
                    }

                    if (IsReceiveTimeout(exception))
                    {
                        continue;
                    }

                    if (exception.SocketErrorCode == SocketError.MessageSize)
                    {
                        continue;
                    }

                    throw;
                }

                now = Stopwatch.GetTimestamp();
                if (now - packetWindowStart >= Stopwatch.Frequency)
                {
                    packetWindowStart = now;
                    packetCount = 0;
                }

                if (packetCount >= MaxBrowserPacketsPerSecond)
                {
                    continue;
                }

                packetCount++;
                if (received <= 0 || received > LanDiscoveryProtocol.MaxDatagramBytes)
                {
                    continue;
                }

                ulong responseNonce;
                LanDiscoveryProtocol.Advertisement advertisement;
                if (!LanDiscoveryProtocol.TryDecodeAdvertisement(
                        receiveBuffer,
                        0,
                        received,
                        out responseNonce,
                        out advertisement) ||
                    !ContainsRecentNonce(recentQueries, responseNonce, now))
                {
                    continue;
                }

                IPEndPoint source = remote as IPEndPoint;
                if (source == null || source.AddressFamily != AddressFamily.InterNetwork)
                {
                    continue;
                }

                IPAddress sourceAddress = new IPAddress(source.Address.GetAddressBytes());
                EnqueueWorkerMessage(session, new WorkerMessage(
                    session.Generation,
                    WorkerMessageKind.Room,
                    sourceAddress,
                    advertisement));
            }
        }
    }

    // 유효 질의에만 제한된 유니캐스트 응답·최신 광고 스냅샷 전송
    private void RunHostWorker(WorkerSession session, Socket socket)
    {
        HostRateLimiter rateLimiter = new HostRateLimiter();
        byte[] receiveBuffer = new byte[ReceiveBufferLength];
        byte[] responseBuffer = new byte[LanDiscoveryProtocol.MaxDatagramBytes];
        long queryWindowStart = Stopwatch.GetTimestamp();
        int queryCount = 0;

        HostRequest host = session.Request.Host;
        HostAdvertisement initialAdvertisement = Volatile.Read(ref host.Advertisement);
        EnqueueWorkerMessage(session, new WorkerMessage(
            session.Generation,
            WorkerMessageKind.Ready,
            WorkerMode.Hosting,
            "Hosting room '" + initialAdvertisement.RoomName + "' on IPv4 discovery UDP port " + LanDiscoveryProtocol.DiscoveryPort + "."));

        while (!session.Token.IsCancellationRequested)
        {
            EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
            int received;
            try
            {
                received = socket.ReceiveFrom(receiveBuffer, 0, receiveBuffer.Length, SocketFlags.None, ref remote);
            }
            catch (SocketException exception)
            {
                if (session.Token.IsCancellationRequested)
                {
                    break;
                }

                if (IsReceiveTimeout(exception))
                {
                    continue;
                }

                if (exception.SocketErrorCode == SocketError.MessageSize)
                {
                    continue;
                }

                throw;
            }

            long now = Stopwatch.GetTimestamp();
            if (now - queryWindowStart >= Stopwatch.Frequency)
            {
                queryWindowStart = now;
                queryCount = 0;
            }

            if (queryCount >= MaxHostQueriesPerSecond)
            {
                continue;
            }

            queryCount++;
            if (received <= 0 || received > LanDiscoveryProtocol.MaxDatagramBytes)
            {
                continue;
            }

            ulong requestNonce;
            if (!LanDiscoveryProtocol.TryDecodeQuery(receiveBuffer, 0, received, out requestNonce))
            {
                continue;
            }

            IPEndPoint requester = remote as IPEndPoint;
            if (requester == null || requester.AddressFamily != AddressFamily.InterNetwork ||
                !rateLimiter.TryAllow(requester.Address, now))
            {
                continue;
            }

            HostAdvertisement currentAdvertisement = Volatile.Read(ref host.Advertisement);
            if (currentAdvertisement == null)
            {
                continue;
            }

            uint sequence = session.NextAdvertisementSequence();
            int responseLength;
            if (!LanDiscoveryProtocol.TryEncodeAdvertisement(
                    requestNonce,
                    host.RoomInstanceId,
                    sequence,
                    host.GamePort,
                    currentAdvertisement.RoomName,
                    currentAdvertisement.Occupancy,
                    LanDiscoveryProtocol.RoomCapacity,
                    currentAdvertisement.Accepting,
                    responseBuffer,
                    out responseLength))
            {
                continue;
            }

            try
            {
                socket.SendTo(responseBuffer, 0, responseLength, SocketFlags.None, requester);
            }
            catch (SocketException)
            {
                if (session.Token.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }

    // 활성 IPv4 서브넷의 중복 없는 브로드캐스트·루프백 목적지 수집
    private List<IPEndPoint> GetBroadcastDestinations()
    {
        List<IPEndPoint> destinations = new List<IPEndPoint>(MaxBroadcastAddresses + 1);
        HashSet<IPAddress> seen = new HashSet<IPAddress>();
        destinations.Add(new IPEndPoint(IPAddress.Loopback, LanDiscoveryProtocol.DiscoveryPort));
        seen.Add(IPAddress.Loopback);
        int broadcastCount = 0;

        try
        {
            NetworkInterface[] interfaces = NetworkInterface.GetAllNetworkInterfaces();
            for (int interfaceIndex = 0; interfaceIndex < interfaces.Length && broadcastCount < MaxBroadcastAddresses; interfaceIndex++)
            {
                NetworkInterface networkInterface = interfaces[interfaceIndex];
                if (networkInterface.OperationalStatus != OperationalStatus.Up ||
                    networkInterface.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                try
                {
                    foreach (UnicastIPAddressInformation unicast in networkInterface.GetIPProperties().UnicastAddresses)
                    {
                        IPAddress address = unicast.Address;
                        IPAddress mask = unicast.IPv4Mask;
                        if (address.AddressFamily != AddressFamily.InterNetwork || mask == null ||
                            address.Equals(IPAddress.Any) || address.Equals(IPAddress.Loopback))
                        {
                            continue;
                        }

                        byte[] addressBytes = address.GetAddressBytes();
                        if (addressBytes[0] >= 224 && addressBytes[0] <= 239)
                        {
                            continue;
                        }

                        byte[] maskBytes = mask.GetAddressBytes();
                        byte[] broadcastBytes = new byte[4];
                        for (int index = 0; index < broadcastBytes.Length; index++)
                        {
                            broadcastBytes[index] = (byte)(addressBytes[index] | (byte)~maskBytes[index]);
                        }

                        IPAddress broadcast = new IPAddress(broadcastBytes);
                        if (broadcast.Equals(address))
                        {
                            continue;
                        }

                        if (seen.Add(broadcast))
                        {
                            destinations.Add(new IPEndPoint(broadcast, LanDiscoveryProtocol.DiscoveryPort));
                            broadcastCount++;
                            if (broadcastCount >= MaxBroadcastAddresses)
                            {
                                break;
                            }
                        }
                    }
                }
                catch (Exception)
                {
                    // 사라지거나 미지원인 인터페이스 무시·루프백 탐색 유지
                }
            }
        }
        catch (Exception)
        {
            // 인터페이스 열거 실패 시에도 로컬 다중 프로세스용 루프백 유지
        }

        return destinations;
    }

    // 잠금 기반 전달·취소 메시지 차단·포화 시 방 광고 우선 제거
    private void EnqueueWorkerMessage(WorkerSession session, WorkerMessage message)
    {
        lock (_messageLock)
        {
            if (session.Token.IsCancellationRequested)
            {
                return;
            }

            if (_messages.Count >= MaxQueuedMessages)
            {
                LinkedListNode<WorkerMessage> oldestRoom = _messages.First;
                while (oldestRoom != null && oldestRoom.Value.Kind != WorkerMessageKind.Room)
                {
                    oldestRoom = oldestRoom.Next;
                }

                if (oldestRoom != null)
                {
                    _messages.Remove(oldestRoom);
                }
                else if (message.Kind == WorkerMessageKind.Room)
                {
                    return;
                }
                else
                {
                    _messages.RemoveFirst();
                }
            }

            _messages.AddLast(message);
        }
    }

    // 프레임당 제한 처리·이전 세대 폐기·메인 스레드 상태 반영
    private void DrainWorkerMessages()
    {
        for (int drained = 0; drained < MaxMessagesPerFrame; drained++)
        {
            WorkerMessage message;
            lock (_messageLock)
            {
                if (_messages.Count == 0)
                {
                    return;
                }

                message = _messages.First.Value;
                _messages.RemoveFirst();
            }

            if (message.Generation != _generation)
            {
                continue;
            }

            if (message.Kind == WorkerMessageKind.Ready)
            {
                HandleWorkerReady(message);
            }
            else if (message.Kind == WorkerMessageKind.Room)
            {
                if (_requestedMode == RequestedMode.Browsing)
                {
                    AcceptRoom(message);
                }
            }
            else
            {
                FailCurrentMode(message.Text);
            }
        }
    }

    // 현재 요청과 준비 역할 일치 시 Browsing·Hosting 전환
    private void HandleWorkerReady(WorkerMessage message)
    {
        if (message.Mode == WorkerMode.Browsing && _requestedMode == RequestedMode.Browsing)
        {
            _browserReadyTimestamp = Stopwatch.GetTimestamp();
            _browserReadyMessage = message.Text;
            _noRoomsNoticeReported = false;
            SetStatus(LanRoomDiscoveryState.Browsing, message.Text);
        }
        else if (message.Mode == WorkerMode.Hosting && _requestedMode == RequestedMode.Hosting)
        {
            SetStatus(LanRoomDiscoveryState.Hosting, message.Text);
        }
    }

    // 새 순번 광고만 반영·만료 및 목록 상한 정리·탐색 안내 복원
    private void AcceptRoom(WorkerMessage message)
    {
        LanDiscoveryProtocol.Advertisement advertisement = message.Advertisement;
        RoomKey key = new RoomKey(advertisement.RoomInstanceId, message.SourceAddress, advertisement.GamePort);
        LanDiscoveredRoom oldRoom;
        if (_rooms.TryGetValue(key, out oldRoom) && !IsNewerSequence(advertisement.Sequence, oldRoom.AdvertisementSequence))
        {
            return;
        }

        long now = Stopwatch.GetTimestamp();
        ExpireRooms(now);
        if (!_rooms.ContainsKey(key) && _rooms.Count >= MaxRows)
        {
            EvictOldestRoom();
        }

        _rooms[key] = new LanDiscoveredRoom(
            advertisement.RoomInstanceId,
            advertisement.RoomName,
            message.SourceAddress,
            advertisement.GamePort,
            advertisement.Occupancy,
            advertisement.Capacity,
            advertisement.Accepting,
            advertisement.Sequence,
            now);
        bool restoreBrowseStatus = _noRoomsNoticeReported;
        _noRoomsNoticeReported = false;
        if (restoreBrowseStatus)
        {
            SetStatus(LanRoomDiscoveryState.Browsing, _browserReadyMessage ?? "Browsing local IPv4 broadcast networks and loopback.");
        }

        PublishRooms();
    }

    // 마지막 수신 후 유효 기간이 지난 방 제거·목록 재발행
    private void ExpireRooms(long now)
    {
        if (_rooms.Count == 0)
        {
            return;
        }

        List<RoomKey> expired = null;
        foreach (KeyValuePair<RoomKey, LanDiscoveredRoom> pair in _rooms)
        {
            if (ElapsedSeconds(pair.Value.LastSeenTimestamp, now) >= RoomExpirySeconds)
            {
                if (expired == null)
                {
                    expired = new List<RoomKey>();
                }

                expired.Add(pair.Key);
            }
        }

        if (expired == null)
        {
            return;
        }

        for (int index = 0; index < expired.Count; index++)
        {
            _rooms.Remove(expired[index]);
        }

        PublishRooms();
    }

    // 목록 상한 도달 시 가장 오래된 수신 방 축출
    private void EvictOldestRoom()
    {
        bool found = false;
        RoomKey oldestKey = default(RoomKey);
        long oldestTimestamp = long.MaxValue;
        foreach (KeyValuePair<RoomKey, LanDiscoveredRoom> pair in _rooms)
        {
            if (!found || pair.Value.LastSeenTimestamp < oldestTimestamp)
            {
                found = true;
                oldestKey = pair.Key;
                oldestTimestamp = pair.Value.LastSeenTimestamp;
            }
        }

        if (found)
        {
            _rooms.Remove(oldestKey);
        }
    }

    // 탐색 준비 후 유예 시간 경과 시 빈 목록 안내 1회 통지
    private void ReportNoRoomsIfNeeded(long now)
    {
        if (_status.State != LanRoomDiscoveryState.Browsing || _rooms.Count != 0 || _noRoomsNoticeReported ||
            _browserReadyTimestamp == 0 || ElapsedSeconds(_browserReadyTimestamp, now) < RoomExpirySeconds)
        {
            return;
        }

        _noRoomsNoticeReported = true;
        SetStatus(
            LanRoomDiscoveryState.Browsing,
            "No rooms found yet. Check that both devices share a LAN and allow IPv4 UDP " + LanDiscoveryProtocol.DiscoveryPort + ".");
    }

    // 요청·호스팅 정보·목록 해제와 Error 전환
    private void FailCurrentMode(string message)
    {
        _requestedMode = RequestedMode.None;
        _pendingStart = null;
        _hostRequest = null;
        HostedRoomInstanceId = Guid.Empty;
        ClearRooms();
        SetStatus(LanRoomDiscoveryState.Error, message);
    }

    // 방 사전·읽기 전용 스냅샷 초기화
    private void ClearRooms()
    {
        if (_rooms.Count == 0)
        {
            _roomSnapshot = EmptyRoomList;
            return;
        }

        _rooms.Clear();
        PublishRooms();
    }

    // 메인 스레드 읽기 전용 목록 교체·구독자 예외 격리
    private void PublishRooms()
    {
        LanDiscoveredRoom[] snapshot = new LanDiscoveredRoom[_rooms.Count];
        int index = 0;
        foreach (LanDiscoveredRoom room in _rooms.Values)
        {
            snapshot[index++] = room;
        }

        _roomSnapshot = Array.AsReadOnly(snapshot);
        Action<IReadOnlyList<LanDiscoveredRoom>> handler = RoomsChanged;
        if (handler != null)
        {
            try
            {
                handler(_roomSnapshot);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception, this);
            }
        }
    }

    // 동일 상태 통지 생략·상태 스냅샷 교체·구독자 예외 격리
    private void SetStatus(LanRoomDiscoveryState state, string message)
    {
        if (_status.State == state && string.Equals(_status.Message, message, StringComparison.Ordinal))
        {
            return;
        }

        _status = new LanRoomDiscoveryStatus(state, message);
        Action<LanRoomDiscoveryStatus> handler = StatusChanged;
        if (handler != null)
        {
            try
            {
                handler(_status);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception, this);
            }
        }
    }

    // 공유 전달 큐 잠금 초기화
    private void ClearWorkerMessages()
    {
        lock (_messageLock)
        {
            _messages.Clear();
        }
    }

    // 0을 건너뛰는 세대 증가·지연 메시지 무효화
    private void AdvanceGeneration()
    {
        _generation = unchecked(_generation + 1);
        if (_generation == 0)
        {
            _generation = 1;
        }
    }

    // 공개 수명주기 API의 메인 스레드 호출 강제
    private void EnsureMainThread()
    {
        if (_mainThreadId == 0 || Thread.CurrentThread.ManagedThreadId != _mainThreadId)
        {
            throw new InvalidOperationException("LanRoomDiscovery public lifecycle APIs must be called on Unity's main thread.");
        }
    }

    // 순번 순환을 고려한 반 범위 최신성 비교
    private static bool IsNewerSequence(uint candidate, uint previous)
    {
        return candidate != previous && unchecked((int)(candidate - previous)) > 0;
    }

    // 단조 시계 틱 차이의 초 단위 환산
    private static double ElapsedSeconds(long earlierTimestamp, long laterTimestamp)
    {
        return (double)(laterTimestamp - earlierTimestamp) / Stopwatch.Frequency;
    }

    // 재시도 가능한 수신 대기 종료 오류 분류
    private static bool IsReceiveTimeout(SocketException exception)
    {
        return exception.SocketErrorCode == SocketError.TimedOut || exception.SocketErrorCode == SocketError.WouldBlock;
    }

    // 0·유효 이력 중복을 피하는 암호학적 요청 난수 생성
    private static ulong CreateRequestNonce(RandomNumberGenerator random, RecentQuery[] recentQueries, long now)
    {
        byte[] bytes = new byte[8];
        ulong nonce;
        bool recent;
        do
        {
            random.GetBytes(bytes);
            nonce = 0;
            for (int index = 0; index < bytes.Length; index++)
            {
                nonce = (nonce << 8) | bytes[index];
            }

            recent = nonce == 0;
            for (int index = 0; index < recentQueries.Length && !recent; index++)
            {
                if (recentQueries[index].Nonce == nonce &&
                    ElapsedSeconds(recentQueries[index].Timestamp, now) <= QueryNonceLifetimeSeconds)
                {
                    recent = true;
                }
            }
        }
        while (recent);

        return nonce;
    }

    // 응답 난수의 최근 발행 여부·유효 기간 검증
    private static bool ContainsRecentNonce(RecentQuery[] recentQueries, ulong nonce, long now)
    {
        for (int index = 0; index < recentQueries.Length; index++)
        {
            if (recentQueries[index].Nonce == nonce &&
                ElapsedSeconds(recentQueries[index].Timestamp, now) <= QueryNonceLifetimeSeconds)
            {
                return true;
            }
        }

        return false;
    }

    // 작업자 시작 시 고정되는 역할·호스트 요청 묶음
    private sealed class StartRequest
    {
        public readonly WorkerMode Mode;
        public readonly HostRequest Host;

        // 메인 스레드의 시작 의도 캡처
        public StartRequest(WorkerMode mode, HostRequest host)
        {
            Mode = mode;
            Host = host;
        }
    }

    // 고정 방 ID·포트와 스레드 간 원자 교체 가능한 광고 참조
    private sealed class HostRequest
    {
        public readonly Guid RoomInstanceId;
        public readonly ushort GamePort;
        public HostAdvertisement Advertisement;

        // 호스트 요청·최초 광고 스냅샷 구성
        public HostRequest(Guid roomInstanceId, ushort gamePort, string roomName, byte occupancy, bool accepting)
        {
            RoomInstanceId = roomInstanceId;
            GamePort = gamePort;
            Advertisement = new HostAdvertisement(roomName, occupancy, accepting);
        }
    }

    // 메인 스레드 생성·작업자 읽기 전용 광고 스냅샷
    private sealed class HostAdvertisement
    {
        public readonly string RoomName;
        public readonly byte Occupancy;
        public readonly bool Accepting;

        // 방 표시·인원·입장 수락 상태 캡처
        public HostAdvertisement(string roomName, byte occupancy, bool accepting)
        {
            RoomName = roomName;
            Occupancy = occupancy;
            Accepting = accepting;
        }
    }

    // 작업자 세대·요청·취소·소켓 수명 관리 — 메인 스레드와 종료 경쟁 보호
    private sealed class WorkerSession
    {
        // 생성 이후 고정 식별 정보와 메인 스레드 완료 관찰용 작업
        public readonly int Generation;
        public readonly StartRequest Request;
        public Task Task;

        // 취소 공유·소켓 잠금·원자 순번·중복 해제 방지 표식
        private readonly CancellationTokenSource _cancellation = new CancellationTokenSource();
        private readonly object _socketLock = new object();
        private Socket _socket;
        private int _advertisementSequence;
        private int _disposed;

        // 작업자 루프·큐 전달의 취소 관찰 토큰
        public CancellationToken Token { get { return _cancellation.Token; } }

        // 단일 작업자 세대·시작 요청 연결
        public WorkerSession(int generation, StartRequest request)
        {
            Generation = generation;
            Request = request;
        }

        // 취소와 원자적으로 소켓 소유권 인수 — 실패 시 호출자 정리
        public bool TryAttachSocket(Socket socket)
        {
            lock (_socketLock)
            {
                if (_cancellation.IsCancellationRequested)
                {
                    return false;
                }

                _socket = socket;
                return true;
            }
        }

        // 종료 경쟁을 고려한 현재 소켓 참조 조회
        public Socket GetSocket()
        {
            lock (_socketLock)
            {
                return _socket;
            }
        }

        // 취소 신호·소켓 닫기로 수신 대기 해제 — 이미 해제된 세션 무시
        public void CancelAndClose()
        {
            try
            {
                _cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            CloseSocket();
        }

        // 잠금 안에서 소켓 분리·잠금 밖에서 중복 안전 종료
        public void CloseSocket()
        {
            Socket socket;
            lock (_socketLock)
            {
                socket = _socket;
                _socket = null;
            }

            if (socket != null)
            {
                try
                {
                    socket.Dispose();
                }
                catch (SocketException)
                {
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }

        // 0을 건너뛰는 원자 광고 순번 증가
        public uint NextAdvertisementSequence()
        {
            int sequence = Interlocked.Increment(ref _advertisementSequence);
            if (sequence == 0)
            {
                sequence = Interlocked.Increment(ref _advertisementSequence);
            }

            return unchecked((uint)sequence);
        }

        // 작업자·메인 스레드 중 최초 호출만 자원 해제
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            CloseSocket();
            _cancellation.Dispose();
        }
    }

    // 공유 큐의 불변 전달 값 — 세대·종류별 상태 텍스트 또는 방 광고
    private sealed class WorkerMessage
    {
        public readonly int Generation;
        public readonly WorkerMessageKind Kind;
        public readonly WorkerMode Mode;
        public readonly string Text;
        public readonly IPAddress SourceAddress;
        public readonly LanDiscoveryProtocol.Advertisement Advertisement;

        // 오류·기본 탐색 준비 상태 메시지 구성
        public WorkerMessage(int generation, WorkerMessageKind kind, string text)
        {
            Generation = generation;
            Kind = kind;
            Mode = kind == WorkerMessageKind.Ready ? WorkerMode.Browsing : default(WorkerMode);
            Text = text;
            SourceAddress = null;
            Advertisement = default(LanDiscoveryProtocol.Advertisement);
        }

        // 실행 역할을 포함하는 준비 상태 메시지 구성
        public WorkerMessage(int generation, WorkerMessageKind kind, WorkerMode mode, string text)
        {
            Generation = generation;
            Kind = kind;
            Mode = mode;
            Text = text;
            SourceAddress = null;
            Advertisement = default(LanDiscoveryProtocol.Advertisement);
        }

        // 실제 송신 IPv4 주소와 검증 광고 전달
        public WorkerMessage(
            int generation,
            WorkerMessageKind kind,
            IPAddress sourceAddress,
            LanDiscoveryProtocol.Advertisement advertisement)
        {
            Generation = generation;
            Kind = kind;
            Mode = WorkerMode.Browsing;
            Text = null;
            SourceAddress = sourceAddress;
            Advertisement = advertisement;
        }
    }

    // 탐색 작업자 전용 요청 난수·발행 시각 이력
    private struct RecentQuery
    {
        public readonly ulong Nonce;
        public readonly long Timestamp;

        // 난수 유효 기간 판정용 발행 정보 구성
        public RecentQuery(ulong nonce, long timestamp)
        {
            Nonce = nonce;
            Timestamp = timestamp;
        }
    }

    // 방 ID·송신 주소·게임 포트 복합 키 — 같은 ID의 다른 접속 경로 구분
    private struct RoomKey : IEquatable<RoomKey>
    {
        private readonly Guid _roomInstanceId;
        private readonly IPAddress _address;
        private readonly ushort _gamePort;

        // 방 목록 식별 키 구성
        public RoomKey(Guid roomInstanceId, IPAddress address, ushort gamePort)
        {
            _roomInstanceId = roomInstanceId;
            _address = address;
            _gamePort = gamePort;
        }

        // 복합 키의 세 식별 요소 값 비교
        public bool Equals(RoomKey other)
        {
            return _roomInstanceId == other._roomInstanceId && _gamePort == other._gamePort &&
                   Equals(_address, other._address);
        }

        // 객체 비교의 타입 확인·형식화 비교 위임
        public override bool Equals(object obj)
        {
            return obj is RoomKey && Equals((RoomKey)obj);
        }

        // 사전 조회용 복합 해시
        public override int GetHashCode()
        {
            unchecked
            {
                int hash = _roomInstanceId.GetHashCode();
                hash = (hash * 397) ^ (_address == null ? 0 : _address.GetHashCode());
                return (hash * 397) ^ _gamePort.GetHashCode();
            }
        }
    }

    // 호스트 작업자 전용 응답 제한 — 출발지별 간격·전역 초당 상한
    private sealed class HostRateLimiter
    {
        // 제한된 출발지별 마지막 응답 시각과 전역 1초 구간 집계
        private readonly Dictionary<IPAddress, long> _lastResponseBySource = new Dictionary<IPAddress, long>();
        private long _globalWindowStart;
        private int _globalResponses;

        // 오래된 이력 정리·간격·전역량·출발지 수 검증 후 응답 허용 기록
        public bool TryAllow(IPAddress source, long now)
        {
            Prune(now);
            long lastResponse;
            if (_lastResponseBySource.TryGetValue(source, out lastResponse) &&
                ElapsedSeconds(lastResponse, now) < HostSourceRateSeconds)
            {
                return false;
            }

            if (_globalWindowStart == 0 || ElapsedSeconds(_globalWindowStart, now) >= 1.0)
            {
                _globalWindowStart = now;
                _globalResponses = 0;
            }

            if (_globalResponses >= MaxHostResponsesPerSecond)
            {
                return false;
            }

            if (!_lastResponseBySource.ContainsKey(source) && _lastResponseBySource.Count >= MaxHostSources)
            {
                return false;
            }

            _lastResponseBySource[source] = now;
            _globalResponses++;
            return true;
        }

        // 보존 기간이 지난 출발지 제거·추적 메모리 회수
        private void Prune(long now)
        {
            if (_lastResponseBySource.Count == 0)
            {
                return;
            }

            List<IPAddress> expired = null;
            foreach (KeyValuePair<IPAddress, long> entry in _lastResponseBySource)
            {
                if (ElapsedSeconds(entry.Value, now) >= HostRateEntryLifetimeSeconds)
                {
                    if (expired == null)
                    {
                        expired = new List<IPAddress>();
                    }

                    expired.Add(entry.Key);
                }
            }

            if (expired == null)
            {
                return;
            }

            for (int index = 0; index < expired.Count; index++)
            {
                _lastResponseBySource.Remove(expired[index]);
            }
        }
    }
}
