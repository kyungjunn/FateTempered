using System;
using System.Text;

// The NGO connection approval envelope is deliberately small and contains no champion or prefab data.
public static class RoomConnectionPayload
{
    public const byte ProtocolVersion = 1;
    public const int MaximumNameBytes = 64;
    public const int HeaderLength = 18;
    public const int MaximumPayloadBytes = HeaderLength + MaximumNameBytes;

    private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

    public static bool TryEncode(Guid roomInstanceId, string displayName, out byte[] payload, out string error)
    {
        payload = null;
        error = null;
        if (roomInstanceId == Guid.Empty)
        {
            error = "The room instance ID is missing.";
            return false;
        }

        if (!LocalPlayerProfile.TryValidateDisplayName(displayName, out string normalizedName, out error))
            return false;

        byte[] nameBytes;
        try
        {
            nameBytes = StrictUtf8.GetBytes(normalizedName);
        }
        catch (EncoderFallbackException)
        {
            error = "The display name contains invalid Unicode.";
            return false;
        }

        if (nameBytes.Length == 0 || nameBytes.Length > MaximumNameBytes)
        {
            error = "The display name does not fit the connection payload.";
            return false;
        }

        byte[] guidBytes = roomInstanceId.ToByteArray();
        payload = new byte[HeaderLength + nameBytes.Length];
        payload[0] = ProtocolVersion;
        WriteGuidNetworkOrder(guidBytes, payload, 1);
        payload[17] = (byte)nameBytes.Length;
        Buffer.BlockCopy(nameBytes, 0, payload, HeaderLength, nameBytes.Length);
        return true;
    }

    public static bool TryDecode(byte[] payload, out Guid roomInstanceId, out string displayName, out string error)
    {
        roomInstanceId = Guid.Empty;
        displayName = null;
        error = null;
        if (payload == null || payload.Length < HeaderLength + 1 || payload.Length > MaximumPayloadBytes)
        {
            error = "The connection payload length is invalid.";
            return false;
        }

        if (payload[0] != ProtocolVersion)
        {
            error = "The connection payload protocol version is unsupported.";
            return false;
        }

        int nameByteCount = payload[17];
        if (nameByteCount == 0 || nameByteCount > MaximumNameBytes || payload.Length != HeaderLength + nameByteCount)
        {
            error = "The connection payload name length is invalid.";
            return false;
        }

        byte[] guidBytes = new byte[16];
        ReadGuidNetworkOrder(payload, 1, guidBytes);
        roomInstanceId = new Guid(guidBytes);
        if (roomInstanceId == Guid.Empty)
        {
            error = "The connection payload room instance ID is invalid.";
            return false;
        }

        string decodedName;
        try
        {
            decodedName = StrictUtf8.GetString(payload, HeaderLength, nameByteCount);
        }
        catch (DecoderFallbackException)
        {
            error = "The connection payload name is not valid UTF-8.";
            return false;
        }

        if (!LocalPlayerProfile.TryValidateDisplayName(decodedName, out string normalizedName, out error))
            return false;
        if (!string.Equals(decodedName, normalizedName, StringComparison.Ordinal))
        {
            error = "The connection payload name is not in its canonical form.";
            return false;
        }

        displayName = normalizedName;
        return true;
    }

    private static void WriteGuidNetworkOrder(byte[] guidBytes, byte[] destination, int offset)
    {
        destination[offset] = guidBytes[3];
        destination[offset + 1] = guidBytes[2];
        destination[offset + 2] = guidBytes[1];
        destination[offset + 3] = guidBytes[0];
        destination[offset + 4] = guidBytes[5];
        destination[offset + 5] = guidBytes[4];
        destination[offset + 6] = guidBytes[7];
        destination[offset + 7] = guidBytes[6];
        Buffer.BlockCopy(guidBytes, 8, destination, offset + 8, 8);
    }

    private static void ReadGuidNetworkOrder(byte[] source, int offset, byte[] guidBytes)
    {
        guidBytes[0] = source[offset + 3];
        guidBytes[1] = source[offset + 2];
        guidBytes[2] = source[offset + 1];
        guidBytes[3] = source[offset];
        guidBytes[4] = source[offset + 5];
        guidBytes[5] = source[offset + 4];
        guidBytes[6] = source[offset + 7];
        guidBytes[7] = source[offset + 6];
        Buffer.BlockCopy(source, offset + 8, guidBytes, 8, 8);
    }
}
