using System;
using System.Text;

// Fixed-endian, exact-length discovery datagrams. This codec has no socket or Unity dependencies.
public static class LanDiscoveryProtocol
{
    public const int MaxDatagramBytes = 512;
    public const int DiscoveryPort = 47777;
    public const ushort DefaultGamePort = 7777;
    public const byte ProtocolVersion = 1;
    public const byte RoomCapacity = 2;

    private const uint Magic = 0x4C414E44; // "LAND"
    private const byte QueryKind = 1;
    private const byte AdvertisementKind = 2;
    private const int HeaderLength = 14;
    private const int AdvertisementFixedLength = 40;
    private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

    public struct Advertisement
    {
        public Guid RoomInstanceId { get; private set; }
        public uint Sequence { get; private set; }
        public ushort GamePort { get; private set; }
        public string RoomName { get; private set; }
        public byte Occupancy { get; private set; }
        public byte Capacity { get; private set; }
        public bool Accepting { get; private set; }

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

    private static bool HasValidHeader(byte[] datagram, int offset, int count)
    {
        if (datagram == null || offset < 0 || count < HeaderLength || count > MaxDatagramBytes ||
            offset > datagram.Length - count)
        {
            return false;
        }

        return ReadUInt32(datagram, offset) == Magic && datagram[offset + 4] == ProtocolVersion;
    }

    private static void WriteHeader(byte[] destination, byte kind, ulong requestNonce)
    {
        WriteUInt32(destination, 0, Magic);
        destination[4] = ProtocolVersion;
        destination[5] = kind;
        WriteUInt64(destination, 6, requestNonce);
    }

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

    private static void WriteUInt16(byte[] destination, int offset, ushort value)
    {
        destination[offset] = (byte)(value >> 8);
        destination[offset + 1] = (byte)value;
    }

    private static ushort ReadUInt16(byte[] datagram, int offset)
    {
        return (ushort)((datagram[offset] << 8) | datagram[offset + 1]);
    }

    private static void WriteUInt32(byte[] destination, int offset, uint value)
    {
        destination[offset] = (byte)(value >> 24);
        destination[offset + 1] = (byte)(value >> 16);
        destination[offset + 2] = (byte)(value >> 8);
        destination[offset + 3] = (byte)value;
    }

    private static uint ReadUInt32(byte[] datagram, int offset)
    {
        return ((uint)datagram[offset] << 24) |
               ((uint)datagram[offset + 1] << 16) |
               ((uint)datagram[offset + 2] << 8) |
               datagram[offset + 3];
    }

    private static void WriteUInt64(byte[] destination, int offset, ulong value)
    {
        for (int index = 7; index >= 0; index--)
        {
            destination[offset + index] = (byte)value;
            value >>= 8;
        }
    }

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
