using System;
using System.Globalization;
using System.Text;
using UnityEngine;

// 로컬 표시 이름 관리 — 플레이어·계정 식별자 용도 아님
public static class LocalPlayerProfile
{
    // 저장값 부재·손상 시 기본 이름
    public const string DefaultDisplayName = "Player";

    // 저장 키와 이름별 유니코드 스칼라·UTF-8 바이트 상한
    private const string DisplayNamePreferenceKey = "local_player_profile.display_name.v1";
    private const int DisplayNameScalarLimit = 24;
    private const int DisplayNameUtf8Limit = 64;
    private const int RoomNameScalarLimit = 32;
    private const int RoomNameUtf8Limit = 96;

    // 저장 이름 검증·정규화, 읽기 실패 시 기본값 반환
    public static string LoadDisplayName()
    {
        try
        {
            if (!PlayerPrefs.HasKey(DisplayNamePreferenceKey))
            {
                return DefaultDisplayName;
            }

            string storedName = PlayerPrefs.GetString(DisplayNamePreferenceKey, string.Empty);
            string normalizedName;
            string error;
            return TryValidateDisplayName(storedName, out normalizedName, out error)
                ? normalizedName
                : DefaultDisplayName;
        }
        catch (Exception)
        {
            return DefaultDisplayName;
        }
    }

    // 사용자 변경 확정 시에만 저장 — PlayerPrefs에 계정 식별 정보 없음
    public static bool TrySaveDisplayName(string value, out string savedDisplayName, out string error)
    {
        if (!TryValidateDisplayName(value, out savedDisplayName, out error))
        {
            return false;
        }

        try
        {
            PlayerPrefs.SetString(DisplayNamePreferenceKey, savedDisplayName);
            PlayerPrefs.Save();
            error = null;
            return true;
        }
        catch (Exception exception)
        {
            error = "Could not save the display name: " + exception.Message;
            return false;
        }
    }

    // 표시 이름 전용 길이 제한 검증
    public static bool TryValidateDisplayName(string value, out string normalizedName, out string error)
    {
        return TryValidateName(value, DisplayNameScalarLimit, DisplayNameUtf8Limit, "Display name", out normalizedName, out error);
    }

    // 방 이름 전용 길이 제한 검증
    public static bool TryValidateRoomName(string value, out string normalizedName, out string error)
    {
        return TryValidateName(value, RoomNameScalarLimit, RoomNameUtf8Limit, "Room name", out normalizedName, out error);
    }

    // 공백 제거·NFC 정규화와 표시 안전성·전송 길이 검증
    private static bool TryValidateName(
        string value,
        int scalarLimit,
        int utf8Limit,
        string label,
        out string normalizedName,
        out string error)
    {
        normalizedName = null;
        error = null;
        if (value == null)
        {
            error = label + " is required.";
            return false;
        }

        try
        {
            normalizedName = value.Trim().Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            error = label + " contains invalid Unicode.";
            return false;
        }

        if (normalizedName.Length == 0)
        {
            error = label + " is required.";
            return false;
        }

        // UTF-16 코드 단위와 별개인 스칼라 수·UTF-8 크기 누적
        int scalarCount = 0;
        int utf8ByteCount = 0;
        for (int index = 0; index < normalizedName.Length;)
        {
            char first = normalizedName[index];
            int scalar;
            int codePoint;

            // 잘못된 서로게이트 쌍 차단
            if (char.IsHighSurrogate(first))
            {
                if (index + 1 >= normalizedName.Length || !char.IsLowSurrogate(normalizedName[index + 1]))
                {
                    error = label + " contains invalid Unicode.";
                    normalizedName = null;
                    return false;
                }

                scalar = 2;
                codePoint = char.ConvertToUtf32(first, normalizedName[index + 1]);
            }
            else if (char.IsLowSurrogate(first))
            {
                error = label + " contains invalid Unicode.";
                normalizedName = null;
                return false;
            }
            else
            {
                scalar = 1;
                codePoint = first;
            }

            // 제어·서식 문자 및 줄바꿈을 통한 표시 교란 차단
            UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(normalizedName, index);
            if (category == UnicodeCategory.Control ||
                category == UnicodeCategory.Format ||
                category == UnicodeCategory.LineSeparator ||
                category == UnicodeCategory.ParagraphSeparator)
            {
                error = label + " cannot contain control or formatting characters.";
                normalizedName = null;
                return false;
            }

            // 리치 텍스트 태그 구분자 차단
            if (codePoint == '<' || codePoint == '>')
            {
                error = label + " cannot contain < or >.";
                normalizedName = null;
                return false;
            }

            scalarCount++;
            if (codePoint <= 0x7F)
            {
                utf8ByteCount++;
            }
            else if (codePoint <= 0x7FF)
            {
                utf8ByteCount += 2;
            }
            else if (codePoint <= 0xFFFF)
            {
                utf8ByteCount += 3;
            }
            else
            {
                utf8ByteCount += 4;
            }

            if (scalarCount > scalarLimit || utf8ByteCount > utf8Limit)
            {
                error = label + " is too long (maximum " + scalarLimit + " Unicode characters and " + utf8Limit + " UTF-8 bytes).";
                normalizedName = null;
                return false;
            }

            index += scalar;
        }

        return true;
    }
}
