using System.Text;

namespace WPMT.Core.Common;

public static class StringUtils
{
    private const char PrependChar = '_';
    private const string Elipsis = "..";
    private const int MaxLengthCharsWithNull = 64; // C# string lengths exclude the null terminator. Use '<' instead of '<=' for MaxLengthCharsWithNull.

    public static void SanitizeAndAppend(bool prependIfNotZeroLength, StringBuilder stringBuilder, string value, int maxLengthChars)
    {
        ArgumentNullException.ThrowIfNull(stringBuilder);
        ArgumentNullException.ThrowIfNull(value);

        int prependLength = (prependIfNotZeroLength && stringBuilder.Length > 0) ? 1 : 0;

        if (stringBuilder.MaxCapacity - stringBuilder.Length < maxLengthChars + prependLength)
            throw new InvalidOperationException("Insufficient capacity in StringBuilder");

        if (prependLength > 0)
            stringBuilder.Append(PrependChar);

        StringBuilder sanitized = new(value.Length, value.Length);

        for (int i = 0; i < value.Length; i++)
        {
            char current = char.ToLower(value[i]);
            if ((current >= '0' && current <= '9') ||
                (current >= 'a' && current <= 'z') ||
                current == '_' ||
                current == '.' ||
                current == '-')
            {
                sanitized.Append(current);
            }
        }

        if (sanitized.Length <= maxLengthChars)
        {
            stringBuilder.Append(sanitized);
        }
        else
        {
            int available = maxLengthChars - Elipsis.Length;
            if (available < 0)
                throw new InvalidOperationException("maxLengthChars is too short to insert ellipsis.");

            int rightLength = available / 2;
            int leftLength = available - rightLength;
            stringBuilder.Append(sanitized, 0, leftLength);
            stringBuilder.Append(Elipsis);
            stringBuilder.Append(sanitized, sanitized.Length - rightLength, rightLength);
        }
    }

    public static StringBuilder ConvertNamePrime(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        StringBuilder stringBuilder = new(name.Length);
        foreach (char c in name)
        {
            if (IsValidAssemblyNameCharacter(c))
                stringBuilder.Append(c);
        }

        if (stringBuilder.Length < MaxLengthCharsWithNull)
            return stringBuilder;

        int threshold = MaxLengthCharsWithNull - Elipsis.Length;
        int leftLength = 0;
        int rightLength = 0;
        while (leftLength + rightLength + Elipsis.Length < stringBuilder.Length)
        {
            int charsBeforeLeftDot;
            for (charsBeforeLeftDot = 0; charsBeforeLeftDot < stringBuilder.Length; charsBeforeLeftDot++)
            {
                if (stringBuilder[charsBeforeLeftDot] == '.')
                    break;
            }

            if (charsBeforeLeftDot >= threshold)
            {
                leftLength = charsBeforeLeftDot;
                break;
            }

            leftLength = charsBeforeLeftDot;

            int charsAfterRightDot;
            for (charsAfterRightDot = stringBuilder.Length - 1; charsAfterRightDot >= 0; charsAfterRightDot--)
            {
                if (stringBuilder[charsAfterRightDot] == '.')
                    break;
            }

            if (charsAfterRightDot <= threshold - charsBeforeLeftDot)
            {
                rightLength += threshold - charsBeforeLeftDot;
                break;
            }

            threshold -= charsBeforeLeftDot + charsAfterRightDot;
            rightLength++;
        }

        stringBuilder.Remove(leftLength, stringBuilder.Length - leftLength - rightLength);
        stringBuilder.Insert(leftLength, Elipsis);

        return stringBuilder;
    }

    private static bool IsValidAssemblyNameCharacter(char c)
    {
        return (c >= 'A' && c <= 'Z') ||
               (c >= 'a' && c <= 'z') ||
               (c >= '0' && c <= '9') ||
               c == '.' ||
               c == '-';
    }

    public static byte[] HexStringToByteArray(string hex)
    {
        ArgumentNullException.ThrowIfNull(hex);

        int length = hex.Length;
        if (length % 2 != 0)
            throw new ArgumentException("Hex string must have an even length.");

        byte[] bytes = new byte[length / 2];
        for (int i = 0; i < length; i += 2)
        {
            bytes[i / 2] = Convert.ToByte(hex.Substring(i, 2), 16);
        }
        return bytes;
    }

    public static string ByteArrayToHexString(byte[] bytes, bool uppercase = false)
    {
        StringBuilder hex = new(bytes.Length * 2);
        foreach (byte b in bytes)
        {
            hex.AppendFormat(uppercase ? "{0:X2}" : "{0:x2}", b);
        }
        return hex.ToString();
    }
}