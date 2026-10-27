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

public enum LanRoomDiscoveryState
{
    Idle,
    Starting,
    Browsing,
    Hosting,
    Stopping,
    Error
}

public sealed class LanRoomDiscoveryStatus
{
    public LanRoomDiscoveryState State { get; private set; }
    public string Message { get; private set; }

    internal LanRoomDiscoveryStatus(LanRoomDiscoveryState state, string message)
    {
        State = state;
        Message = message;
    }
}

public sealed class LanDiscoveredRoom
{
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

    internal long LastSeenTimestamp { get; private set; }

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

// Main-thread API and UI handoff for one bounded IPv4 browser or host responder.
// Lifecycle calls and event delivery run on Unity's main thread; successful start calls mean
// accepted requests, while bind success or failure arrives asynchronously through StatusChanged.
public sealed class LanRoomDiscovery : MonoBehaviour
{
    private const int MaxQueuedMessages = 64;
    private const int MaxMessagesPerFrame = 16;
    private const int MaxRows = 64;
    private const int MaxRecentQueries = 4;
    private const int MaxBrowserPacketsPerSecond = 128;
    private const int MaxHostQueriesPerSecond = 128;
    private const int MaxBroadcastAddresses = 32;
    private const int ReceiveBufferLength = LanDiscoveryProtocol.MaxDatagramBytes + 1;
    private const int ReceiveTimeoutMilliseconds = 200;
    private const double RoomExpirySeconds = 3.0;
    private const double QueryNonceLifetimeSeconds = 3.0;
    private const double HostSourceRateSeconds = 1.0;
    private const double HostRateEntryLifetimeSeconds = 10.0;
    private const int MaxHostSources = 64;
    private const int MaxHostResponsesPerSecond = 8;

    private static readonly IReadOnlyList<LanDiscoveredRoom> EmptyRoomList =
        Array.AsReadOnly(new LanDiscoveredRoom[0]);

    public event Action<LanRoomDiscoveryStatus> StatusChanged;
    public event Action<IReadOnlyList<LanDiscoveredRoom>> RoomsChanged;

    public LanRoomDiscoveryStatus Status { get { return _status; } }
    public IReadOnlyList<LanDiscoveredRoom> Rooms { get { return _roomSnapshot; } }
    public Guid HostedRoomInstanceId { get; private set; }

    // True only after cancellation has completed and the main-thread queue is drained.
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

    private readonly object _messageLock = new object();
    private readonly LinkedList<WorkerMessage> _messages = new LinkedList<WorkerMessage>();
    private readonly Dictionary<RoomKey, LanDiscoveredRoom> _rooms = new Dictionary<RoomKey, LanDiscoveredRoom>();

    private LanRoomDiscoveryStatus _status = new LanRoomDiscoveryStatus(LanRoomDiscoveryState.Idle, "LAN discovery is idle.");
    private IReadOnlyList<LanDiscoveredRoom> _roomSnapshot = EmptyRoomList;
    private WorkerSession _worker;
    private StartRequest _pendingStart;
    private HostRequest _hostRequest;
    private RequestedMode _requestedMode;
    private int _generation;
    private int _mainThreadId;
    private long _browserReadyTimestamp;
    private string _browserReadyMessage;
    private bool _noRoomsNoticeReported;
    private bool _destroyed;

    private enum WorkerMode
    {
        Browsing,
        Hosting
    }

    private enum RequestedMode
    {
        None,
        Browsing,
        Hosting
    }

    private enum WorkerMessageKind
    {
        Ready,
        Room,
        Error
    }

    private void Awake()
    {
        _mainThreadId = Thread.CurrentThread.ManagedThreadId;
    }

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

    private void OnDisable()
    {
        Stop();
    }

    private void OnDestroy()
    {
        _destroyed = true;
        Stop();
    }

    private void OnApplicationQuit()
    {
        _destroyed = true;
        Stop();
    }

    // Starts periodic IPv4 broadcast and loopback queries; call Stop before joining a selected room.
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

    // The session coordinator supplies its already-created room ID; call after the lobby is ready.
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

    // Updates the immutable snapshot read by the worker; the room ID is unchanged for this room.
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

    // Requests cancellation, closes the socket, and lets Update observe the worker's completion without blocking.
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
                    // A disappearing or unsupported interface does not prevent loopback discovery.
                }
            }
        }
        catch (Exception)
        {
            // Keep loopback as a useful local multi-process path when interface enumeration fails.
        }

        return destinations;
    }

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

    private void FailCurrentMode(string message)
    {
        _requestedMode = RequestedMode.None;
        _pendingStart = null;
        _hostRequest = null;
        HostedRoomInstanceId = Guid.Empty;
        ClearRooms();
        SetStatus(LanRoomDiscoveryState.Error, message);
    }

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

    private void ClearWorkerMessages()
    {
        lock (_messageLock)
        {
            _messages.Clear();
        }
    }

    private void AdvanceGeneration()
    {
        _generation = unchecked(_generation + 1);
        if (_generation == 0)
        {
            _generation = 1;
        }
    }

    private void EnsureMainThread()
    {
        if (_mainThreadId == 0 || Thread.CurrentThread.ManagedThreadId != _mainThreadId)
        {
            throw new InvalidOperationException("LanRoomDiscovery public lifecycle APIs must be called on Unity's main thread.");
        }
    }

    private static bool IsNewerSequence(uint candidate, uint previous)
    {
        return candidate != previous && unchecked((int)(candidate - previous)) > 0;
    }

    private static double ElapsedSeconds(long earlierTimestamp, long laterTimestamp)
    {
        return (double)(laterTimestamp - earlierTimestamp) / Stopwatch.Frequency;
    }

    private static bool IsReceiveTimeout(SocketException exception)
    {
        return exception.SocketErrorCode == SocketError.TimedOut || exception.SocketErrorCode == SocketError.WouldBlock;
    }

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

    private sealed class StartRequest
    {
        public readonly WorkerMode Mode;
        public readonly HostRequest Host;

        public StartRequest(WorkerMode mode, HostRequest host)
        {
            Mode = mode;
            Host = host;
        }
    }

    private sealed class HostRequest
    {
        public readonly Guid RoomInstanceId;
        public readonly ushort GamePort;
        public HostAdvertisement Advertisement;

        public HostRequest(Guid roomInstanceId, ushort gamePort, string roomName, byte occupancy, bool accepting)
        {
            RoomInstanceId = roomInstanceId;
            GamePort = gamePort;
            Advertisement = new HostAdvertisement(roomName, occupancy, accepting);
        }
    }

    private sealed class HostAdvertisement
    {
        public readonly string RoomName;
        public readonly byte Occupancy;
        public readonly bool Accepting;

        public HostAdvertisement(string roomName, byte occupancy, bool accepting)
        {
            RoomName = roomName;
            Occupancy = occupancy;
            Accepting = accepting;
        }
    }

    private sealed class WorkerSession
    {
        public readonly int Generation;
        public readonly StartRequest Request;
        public Task Task;

        private readonly CancellationTokenSource _cancellation = new CancellationTokenSource();
        private readonly object _socketLock = new object();
        private Socket _socket;
        private int _advertisementSequence;
        private int _disposed;

        public CancellationToken Token { get { return _cancellation.Token; } }

        public WorkerSession(int generation, StartRequest request)
        {
            Generation = generation;
            Request = request;
        }

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

        public Socket GetSocket()
        {
            lock (_socketLock)
            {
                return _socket;
            }
        }

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

        public uint NextAdvertisementSequence()
        {
            int sequence = Interlocked.Increment(ref _advertisementSequence);
            if (sequence == 0)
            {
                sequence = Interlocked.Increment(ref _advertisementSequence);
            }

            return unchecked((uint)sequence);
        }

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

    private sealed class WorkerMessage
    {
        public readonly int Generation;
        public readonly WorkerMessageKind Kind;
        public readonly WorkerMode Mode;
        public readonly string Text;
        public readonly IPAddress SourceAddress;
        public readonly LanDiscoveryProtocol.Advertisement Advertisement;

        public WorkerMessage(int generation, WorkerMessageKind kind, string text)
        {
            Generation = generation;
            Kind = kind;
            Mode = kind == WorkerMessageKind.Ready ? WorkerMode.Browsing : default(WorkerMode);
            Text = text;
            SourceAddress = null;
            Advertisement = default(LanDiscoveryProtocol.Advertisement);
        }

        public WorkerMessage(int generation, WorkerMessageKind kind, WorkerMode mode, string text)
        {
            Generation = generation;
            Kind = kind;
            Mode = mode;
            Text = text;
            SourceAddress = null;
            Advertisement = default(LanDiscoveryProtocol.Advertisement);
        }

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

    private struct RecentQuery
    {
        public readonly ulong Nonce;
        public readonly long Timestamp;

        public RecentQuery(ulong nonce, long timestamp)
        {
            Nonce = nonce;
            Timestamp = timestamp;
        }
    }

    private struct RoomKey : IEquatable<RoomKey>
    {
        private readonly Guid _roomInstanceId;
        private readonly IPAddress _address;
        private readonly ushort _gamePort;

        public RoomKey(Guid roomInstanceId, IPAddress address, ushort gamePort)
        {
            _roomInstanceId = roomInstanceId;
            _address = address;
            _gamePort = gamePort;
        }

        public bool Equals(RoomKey other)
        {
            return _roomInstanceId == other._roomInstanceId && _gamePort == other._gamePort &&
                   Equals(_address, other._address);
        }

        public override bool Equals(object obj)
        {
            return obj is RoomKey && Equals((RoomKey)obj);
        }

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

    private sealed class HostRateLimiter
    {
        private readonly Dictionary<IPAddress, long> _lastResponseBySource = new Dictionary<IPAddress, long>();
        private long _globalWindowStart;
        private int _globalResponses;

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
