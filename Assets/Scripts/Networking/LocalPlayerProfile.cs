using System;
using System.Globalization;
using System.Text;
using UnityEngine;

// A local display label only; this is deliberately not a player/account identity.
public static class LocalPlayerProfile
{
    public const string DefaultDisplayName = "Player";

    private const string DisplayNamePreferenceKey = "local_player_profile.display_name.v1";
    private const int DisplayNameScalarLimit = 24;
    private const int DisplayNameUtf8Limit = 64;
    private const int RoomNameScalarLimit = 32;
    private const int RoomNameUtf8Limit = 96;

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

    // Call only when the player confirms a profile change. PlayerPrefs contains no account identity.
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

    public static bool TryValidateDisplayName(string value, out string normalizedName, out string error)
    {
        return TryValidateName(value, DisplayNameScalarLimit, DisplayNameUtf8Limit, "Display name", out normalizedName, out error);
    }

    public static bool TryValidateRoomName(string value, out string normalizedName, out string error)
    {
        return TryValidateName(value, RoomNameScalarLimit, RoomNameUtf8Limit, "Room name", out normalizedName, out error);
    }

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

        int scalarCount = 0;
        int utf8ByteCount = 0;
        for (int index = 0; index < normalizedName.Length;)
        {
            char first = normalizedName[index];
            int scalar;
            int codePoint;

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
