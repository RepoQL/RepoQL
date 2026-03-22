using System.Text;

namespace RepoQL.Formats.Json;

/// <summary>
/// Normalizes JSON comments by replacing comment bytes with spaces in UTF-8 payloads.
///
/// Purpose: Convert JSONC-style comments into parseable JSON without changing byte offsets.
///
/// Complexity: Performs single-pass byte scanning with string and escape tracking, BOM handling, and line-preserving block replacement.
/// </summary>
public static class JsonNormalizer
{
    private const byte Space = 0x20;
    private const byte Quote = 0x22;
    private const byte Slash = 0x2F;
    private const byte Asterisk = 0x2A;
    private const byte Backslash = 0x5C;
    private const byte LineFeed = 0x0A;
    private const byte CarriageReturn = 0x0D;

    public static void StripComments(byte[] utf8Bytes)
    {
        ArgumentNullException.ThrowIfNull(utf8Bytes);

        if (utf8Bytes.Length == 0)
        {
            return;
        }

        var index = HasUtf8Bom(utf8Bytes) ? 3 : 0;
        var inString = false;

        while (index < utf8Bytes.Length)
        {
            var current = utf8Bytes[index];

            if (inString)
            {
                if (current == Quote && IsUnescapedQuote(utf8Bytes, index))
                {
                    inString = false;
                }

                index++;
                continue;
            }

            if (current == Quote)
            {
                inString = true;
                index++;
                continue;
            }

            if (current != Slash || index + 1 >= utf8Bytes.Length)
            {
                index++;
                continue;
            }

            var next = utf8Bytes[index + 1];
            if (next == Slash)
            {
                index = ReplaceLineComment(utf8Bytes, index);
                continue;
            }

            if (next == Asterisk)
            {
                index = ReplaceBlockComment(utf8Bytes, index);
                continue;
            }

            index++;
        }
    }

    public static byte[] StripComments(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var utf8Bytes = Encoding.UTF8.GetBytes(text);
        StripComments(utf8Bytes);
        return utf8Bytes;
    }

    private static int ReplaceLineComment(byte[] utf8Bytes, int startIndex)
    {
        var index = startIndex;

        while (index < utf8Bytes.Length && utf8Bytes[index] != LineFeed)
        {
            utf8Bytes[index] = Space;
            index++;
        }

        return index;
    }

    private static int ReplaceBlockComment(byte[] utf8Bytes, int startIndex)
    {
        var index = startIndex;

        while (index < utf8Bytes.Length)
        {
            var current = utf8Bytes[index];
            if (current is not (LineFeed or CarriageReturn))
            {
                utf8Bytes[index] = Space;
            }

            if (current == Asterisk && index + 1 < utf8Bytes.Length && utf8Bytes[index + 1] == Slash)
            {
                utf8Bytes[index] = Space;
                utf8Bytes[index + 1] = Space;
                return index + 2;
            }

            index++;
        }

        return index;
    }

    private static bool IsUnescapedQuote(byte[] utf8Bytes, int quoteIndex)
    {
        var backslashCount = 0;

        for (var index = quoteIndex - 1; index >= 0 && utf8Bytes[index] == Backslash; index--)
        {
            backslashCount++;
        }

        return backslashCount % 2 == 0;
    }

    private static bool HasUtf8Bom(byte[] utf8Bytes)
        => utf8Bytes.Length >= 3
           && utf8Bytes[0] == 0xEF
           && utf8Bytes[1] == 0xBB
           && utf8Bytes[2] == 0xBF;
}
