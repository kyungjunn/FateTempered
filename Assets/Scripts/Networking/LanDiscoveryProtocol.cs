using System;
using System.Text;

// 고정 바이트 순서·정확한 길이의 탐색 코덱 — 소켓·Unity 의존성 없음
public static class LanDiscoveryProtocol
{
    // 데이터그램 상한·UDP 포트·호환 버전·고정 방 정원
    public const int MaxDatagramBytes = 512;
    public const int DiscoveryPort = 47777;
    public const ushort DefaultGamePort = 7777;
    public const byte ProtocolVersion = 1;
    public const byte RoomCapacity = 2;

    // 공통 헤더 14바이트: 식별자 4 + 버전 1 + 종류 1 + 요청 난수 8
    private const uint Magic = 0x4C414E44; // 식별자 "LAND"
    private const byte QueryKind = 1;
    private const byte AdvertisementKind = 2;
    private const int HeaderLength = 14;
    // 광고: 헤더 + GUID 16 + 순번 4 + 포트 2 + 이름 길이 1 + 이름 + 인원·정원·수락 각 1
    private const int AdvertisementFixedLength = 40;
    // 잘못된 UTF-8·유니코드의 묵시적 대체 금지
    private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

    // 검증 완료 광고 값 — 요청 난수는 별도 반환
    public struct Advertisement
    {
        // 방 식별·최신성·접속 경로·표시·입장 가능 상태
        public Guid RoomInstanceId { get; private set; }
        public uint Sequence { get; private set; }
        public ushort GamePort { get; private set; }
        public string RoomName { get; private set; }
        public byte Occupancy { get; private set; }
        public byte Capacity { get; private set; }
        public bool Accepting { get; private set; }

        // 디코딩 결과 스냅샷 구성
        internal Advertisement(
            Guid roomInstanceId,
            uint sequence,
            ushort gamePort,
            string roomName,
            byte occupancy,
            byte capacity,
            bool accepting)
        {
            RoomInstanceId = roomInstanceId;
            Sequence = sequence;
            GamePort = gamePort;
            RoomName = roomName;
            Occupancy = occupancy;
            Capacity = capacity;
            Accepting = accepting;
        }
    }

    // 0이 아닌 요청 난수의 고정 길이 질의 인코딩
    public static bool TryEncodeQuery(ulong requestNonce, byte[] destination, out int bytesWritten)
    {
        bytesWritten = 0;
        if (requestNonce == 0 || destination == null || destination.Length < HeaderLength)
        {
            return false;
        }

        WriteHeader(destination, QueryKind, requestNonce);
        bytesWritten = HeaderLength;
        return true;
    }

    // 질의 종류·정확한 길이·요청 난수 검증
    public static bool TryDecodeQuery(byte[] datagram, int offset, int count, out ulong requestNonce)
    {
        requestNonce = 0;
        if (!HasValidHeader(datagram, offset, count) || count != HeaderLength || datagram[offset + 5] != QueryKind)
        {
            return false;
        }

        requestNonce = ReadUInt64(datagram, offset + 6);
        return requestNonce != 0;
    }

    // 방 상태 불변식·정규형 이름·버퍼 크기 검증 후 광고 인코딩
    public static bool TryEncodeAdvertisement(
        ulong requestNonce,
        Guid roomInstanceId,
        uint sequence,
        ushort gamePort,
        string roomName,
        byte occupancy,
        byte capacity,
        bool accepting,
        byte[] destination,
        out int bytesWritten)
    {
        bytesWritten = 0;
        if (requestNonce == 0 || roomInstanceId == Guid.Empty || sequence == 0 || gamePort == 0 ||
            destination == null ||
            capacity != RoomCapacity || occupancy > capacity || (accepting && occupancy >= capacity))
        {
            return false;
        }

        string normalizedName;
        string validationError;
        if (!LocalPlayerProfile.TryValidateRoomName(roomName, out normalizedName, out validationError) ||
            !string.Equals(normalizedName, roomName, StringComparison.Ordinal))
        {
            return false;
        }

        int nameByteCount;
        try
        {
            nameByteCount = StrictUtf8.GetByteCount(roomName);
        }
        catch (EncoderFallbackException)
        {
            return false;
        }

        int totalLength = AdvertisementFixedLength + nameByteCount;
        if (nameByteCount == 0 || nameByteCount > 96 || totalLength > MaxDatagramBytes || destination.Length < totalLength)
        {
            return false;
        }

        WriteHeader(destination, AdvertisementKind, requestNonce);
        WriteGuid(destination, HeaderLength, roomInstanceId);
        WriteUInt32(destination, HeaderLength + 16, sequence);
        WriteUInt16(destination, HeaderLength + 20, gamePort);
        destination[HeaderLength + 22] = (byte)nameByteCount;

        int nameOffset = HeaderLength + 23;
        try
        {
            StrictUtf8.GetBytes(roomName, 0, roomName.Length, destination, nameOffset);
        }
        catch (EncoderFallbackException)
        {
            return false;
        }

        int occupancyOffset = nameOffset + nameByteCount;
        destination[occupancyOffset] = occupancy;
        destination[occupancyOffset + 1] = capacity;
        destination[occupancyOffset + 2] = accepting ? (byte)1 : (byte)0;
        bytesWritten = totalLength;
        return true;
    }

    // 비신뢰 광고의 구조·길이·이름·인원 일관성 검증 — 송신자 인증 아님
    public static bool TryDecodeAdvertisement(
        byte[] datagram,
        int offset,
        int count,
        out ulong requestNonce,
        out Advertisement advertisement)
    {
        requestNonce = 0;
        advertisement = default(Advertisement);
        if (!HasValidHeader(datagram, offset, count) ||
            count < AdvertisementFixedLength ||
            datagram[offset + 5] != AdvertisementKind)
        {
            return false;
        }

        ulong decodedNonce = ReadUInt64(datagram, offset + 6);
        Guid roomInstanceId = ReadGuid(datagram, offset + HeaderLength);
        uint sequence = ReadUInt32(datagram, offset + HeaderLength + 16);
        ushort gamePort = ReadUInt16(datagram, offset + HeaderLength + 20);
        int nameByteCount = datagram[offset + HeaderLength + 22];
        if (decodedNonce == 0 || roomInstanceId == Guid.Empty || sequence == 0 || gamePort == 0 ||
            nameByteCount == 0 || nameByteCount > 96 || count != AdvertisementFixedLength + nameByteCount)
        {
            return false;
        }

        int nameOffset = offset + HeaderLength + 23;
        string roomName;
        try
        {
            roomName = StrictUtf8.GetString(datagram, nameOffset, nameByteCount);
        }
        catch (DecoderFallbackException)
        {
            return false;
        }

        string normalizedName;
        string validationError;
        if (!LocalPlayerProfile.TryValidateRoomName(roomName, out normalizedName, out validationError) ||
            !string.Equals(normalizedName, roomName, StringComparison.Ordinal))
        {
            return false;
        }

        int occupancyOffset = nameOffset + nameByteCount;
        byte occupancy = datagram[occupancyOffset];
        byte capacity = datagram[occupancyOffset + 1];
        byte acceptingByte = datagram[occupancyOffset + 2];
        if (capacity != RoomCapacity || occupancy > capacity || acceptingByte > 1 ||
            (acceptingByte != 0 && occupancy >= capacity))
        {
            return false;
        }

        requestNonce = decodedNonce;
        advertisement = new Advertisement(
            roomInstanceId,
            sequence,
            gamePort,
            roomName,
            occupancy,
            capacity,
            acceptingByte != 0);
        return true;
    }

    // 배열 범위·최대 크기·프로토콜 식별자와 버전 검증
    private static bool HasValidHeader(byte[] datagram, int offset, int count)
    {
        if (datagram == null || offset < 0 || count < HeaderLength || count > MaxDatagramBytes ||
            offset > datagram.Length - count)
        {
            return false;
        }

        return ReadUInt32(datagram, offset) == Magic && datagram[offset + 4] == ProtocolVersion;
    }

    // 공통 헤더 기록 — 요청 난수로 질의·응답 연결
    private static void WriteHeader(byte[] destination, byte kind, ulong requestNonce)
    {
        WriteUInt32(destination, 0, Magic);
        destination[4] = ProtocolVersion;
        destination[5] = kind;
        WriteUInt64(destination, 6, requestNonce);
    }

    // GUID 앞 세 필드의 네트워크 바이트 순서 기록
    private static void WriteGuid(byte[] destination, int offset, Guid value)
    {
        byte[] bytes = value.ToByteArray();
        destination[offset] = bytes[3];
        destination[offset + 1] = bytes[2];
        destination[offset + 2] = bytes[1];
        destination[offset + 3] = bytes[0];
        destination[offset + 4] = bytes[5];
        destination[offset + 5] = bytes[4];
        destination[offset + 6] = bytes[7];
        destination[offset + 7] = bytes[6];
        Buffer.BlockCopy(bytes, 8, destination, offset + 8, 8);
    }

    // 네트워크 순서 GUID의 .NET 형식 복원
    private static Guid ReadGuid(byte[] datagram, int offset)
    {
        byte[] bytes = new byte[16];
        bytes[0] = datagram[offset + 3];
        bytes[1] = datagram[offset + 2];
        bytes[2] = datagram[offset + 1];
        bytes[3] = datagram[offset];
        bytes[4] = datagram[offset + 5];
        bytes[5] = datagram[offset + 4];
        bytes[6] = datagram[offset + 7];
        bytes[7] = datagram[offset + 6];
        Buffer.BlockCopy(datagram, offset + 8, bytes, 8, 8);
        return new Guid(bytes);
    }

    // 16비트 부호 없는 정수의 빅 엔디언 기록
    private static void WriteUInt16(byte[] destination, int offset, ushort value)
    {
        destination[offset] = (byte)(value >> 8);
        destination[offset + 1] = (byte)value;
    }

    // 16비트 빅 엔디언 정수 복원
    private static ushort ReadUInt16(byte[] datagram, int offset)
    {
        return (ushort)((datagram[offset] << 8) | datagram[offset + 1]);
    }

    // 32비트 부호 없는 정수의 빅 엔디언 기록
    private static void WriteUInt32(byte[] destination, int offset, uint value)
    {
        destination[offset] = (byte)(value >> 24);
        destination[offset + 1] = (byte)(value >> 16);
        destination[offset + 2] = (byte)(value >> 8);
        destination[offset + 3] = (byte)value;
    }

    // 32비트 빅 엔디언 정수 복원
    private static uint ReadUInt32(byte[] datagram, int offset)
    {
        return ((uint)datagram[offset] << 24) |
               ((uint)datagram[offset + 1] << 16) |
               ((uint)datagram[offset + 2] << 8) |
               datagram[offset + 3];
    }

    // 64비트 요청 난수의 빅 엔디언 기록
    private static void WriteUInt64(byte[] destination, int offset, ulong value)
    {
        for (int index = 7; index >= 0; index--)
        {
            destination[offset + index] = (byte)value;
            value >>= 8;
        }
    }

    // 64비트 빅 엔디언 요청 난수 복원
    private static ulong ReadUInt64(byte[] datagram, int offset)
    {
        ulong value = 0;
        for (int index = 0; index < 8; index++)
        {
            value = (value << 8) | datagram[offset + index];
        }

        return value;
    }
}
