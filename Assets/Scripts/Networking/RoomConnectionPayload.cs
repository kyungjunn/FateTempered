using System;
using System.Text;

// NGO 접속 승인용 최소 봉투 — 챔피언·프리팹 정보 제외, 인증 수단 아님
public static class RoomConnectionPayload
{
    // 패킷: 버전 1바이트 + 방 GUID 16바이트 + 이름 길이 1바이트 + UTF-8 이름
    public const byte ProtocolVersion = 1;
    public const int MaximumNameBytes = 64;
    public const int HeaderLength = 18;
    public const int MaximumPayloadBytes = HeaderLength + MaximumNameBytes;

    // 잘못된 유니코드의 대체 문자 변환 금지
    private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

    // 방 ID·이름 검증 후 정규형 이름 인코딩
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

    // 외부 입력의 버전·정확한 길이·GUID·UTF-8·이름 정규형 검증
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

    // .NET GUID 앞 세 필드의 네트워크 바이트 순서 변환
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

    // 네트워크 GUID의 .NET 바이트 배열 복원
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
