using System.Text;

namespace RepoQL.Formats.Json;

/// <summary>
/// Normalizes JSON5 text into valid JSON that <see cref="JsonStructureParser"/> can parse.
///
/// Purpose: Enables full structure indexing for .json5 files by transforming JSON5 extensions
/// (comments, trailing commas, unquoted keys, single-quoted strings, hex numbers, Infinity/NaN,
/// multi-line strings) into standard JSON.
///
/// Complexity: Char-level single-pass state machine writing to StringBuilder. Cannot be in-place
/// like <see cref="JsonNormalizer"/> because transformations change output length. Preserves
/// newline positions so <see cref="JsonStructureParser"/> line numbers remain accurate.
/// </summary>
public static class Json5Normalizer
{
    /// <summary>
    /// Converts JSON5 text to valid JSON.
    /// </summary>
    public static string Normalize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (text.Length == 0)
            return text;

        var sb = new StringBuilder(text.Length);
        var i = 0;
        var len = text.Length;

        while (i < len)
        {
            var ch = text[i];

            // Line comments
            if (ch == '/' && i + 1 < len && text[i + 1] == '/')
            {
                i = SkipLineComment(text, i, sb);
                continue;
            }

            // Block comments
            if (ch == '/' && i + 1 < len && text[i + 1] == '*')
            {
                i = SkipBlockComment(text, i, sb);
                continue;
            }

            // Double-quoted strings
            if (ch == '"')
            {
                i = CopyDoubleString(text, i, sb);
                continue;
            }

            // Single-quoted strings → convert to double-quoted
            if (ch == '\'')
            {
                i = ConvertSingleString(text, i, sb);
                continue;
            }

            // Trailing commas — skip if followed by } or ]
            if (ch == ',')
            {
                if (IsTrailingComma(text, i))
                {
                    i++;
                    continue;
                }

                sb.Append(',');
                i++;
                continue;
            }

            // Sign before identifier: +Infinity, -Infinity, +NaN, -NaN → null
            if ((ch == '+' || ch == '-') && i + 1 < len && IsIdentStart(text[i + 1]))
            {
                var identEnd = i + 1;
                while (identEnd < len && IsIdentChar(text[identEnd]))
                    identEnd++;

                var word = text.AsSpan(i + 1, identEnd - i - 1);
                if (word is "Infinity" or "NaN")
                {
                    sb.Append("null");
                    i = identEnd;
                    continue;
                }

                // Not a special value — emit sign normally
                sb.Append(ch);
                i++;
                continue;
            }

            // Leading + on numbers — strip (JSON doesn't allow +42)
            if (ch == '+' && i + 1 < len && (char.IsDigit(text[i + 1]) || text[i + 1] == '.'))
            {
                i++;
                continue;
            }

            // Identifiers: unquoted keys, true/false/null, Infinity/NaN
            if (IsIdentStart(ch))
            {
                i = ProcessIdentifier(text, i, sb);
                continue;
            }

            // Hex numbers: 0xFF → 255
            if (ch == '0' && i + 1 < len && text[i + 1] is 'x' or 'X')
            {
                i = ConvertHexNumber(text, i, sb);
                continue;
            }

            // Everything else (digits, braces, colons, whitespace, etc.)
            sb.Append(ch);
            i++;
        }

        return sb.ToString();
    }

    private static int SkipLineComment(string text, int start, StringBuilder sb)
    {
        var i = start + 2; // skip //
        while (i < text.Length && text[i] != '\n' && text[i] != '\r')
            i++;

        // Preserve line terminator
        if (i < text.Length)
        {
            if (text[i] == '\r')
            {
                sb.Append('\r');
                i++;
                if (i < text.Length && text[i] == '\n')
                {
                    sb.Append('\n');
                    i++;
                }
            }
            else
            {
                sb.Append('\n');
                i++;
            }
        }

        return i;
    }

    private static int SkipBlockComment(string text, int start, StringBuilder sb)
    {
        var i = start + 2; // skip /*
        while (i < text.Length)
        {
            if (text[i] == '*' && i + 1 < text.Length && text[i + 1] == '/')
                return i + 2; // skip */

            if (text[i] is '\n' or '\r')
                sb.Append(text[i]); // preserve newlines

            i++;
        }

        return i; // unterminated
    }

    private static int CopyDoubleString(string text, int start, StringBuilder sb)
    {
        sb.Append('"');
        var i = start + 1;

        while (i < text.Length)
        {
            var ch = text[i];

            if (ch == '\\' && i + 1 < text.Length)
            {
                var next = text[i + 1];

                // Multi-line string: \ followed by line terminator → skip both
                if (next == '\n')
                {
                    i += 2;
                    continue;
                }

                if (next == '\r')
                {
                    i += 2;
                    if (i < text.Length && text[i] == '\n')
                        i++;
                    continue;
                }

                // Regular escape — pass through
                sb.Append('\\');
                sb.Append(next);
                i += 2;
                continue;
            }

            if (ch == '"')
            {
                sb.Append('"');
                return i + 1;
            }

            sb.Append(ch);
            i++;
        }

        return i; // unterminated
    }

    private static int ConvertSingleString(string text, int start, StringBuilder sb)
    {
        sb.Append('"'); // open with double quote
        var i = start + 1;

        while (i < text.Length)
        {
            var ch = text[i];

            if (ch == '\\' && i + 1 < text.Length)
            {
                var next = text[i + 1];

                // Multi-line continuation
                if (next == '\n')
                {
                    i += 2;
                    continue;
                }

                if (next == '\r')
                {
                    i += 2;
                    if (i < text.Length && text[i] == '\n')
                        i++;
                    continue;
                }

                // \' in single-quoted string → emit bare '
                if (next == '\'')
                {
                    sb.Append('\'');
                    i += 2;
                    continue;
                }

                // Other escapes — pass through
                sb.Append('\\');
                sb.Append(next);
                i += 2;
                continue;
            }

            if (ch == '\'')
            {
                sb.Append('"'); // close with double quote
                return i + 1;
            }

            // Internal double quotes need escaping
            if (ch == '"')
            {
                sb.Append("\\\"");
                i++;
                continue;
            }

            sb.Append(ch);
            i++;
        }

        return i; // unterminated
    }

    /// <summary>
    /// Scans ahead from a comma position to determine if it's a trailing comma
    /// (followed only by whitespace/comments then <c>}</c> or <c>]</c>).
    /// </summary>
    private static bool IsTrailingComma(string text, int commaIndex)
    {
        var i = commaIndex + 1;

        while (i < text.Length)
        {
            var ch = text[i];

            if (char.IsWhiteSpace(ch))
            {
                i++;
                continue;
            }

            // Skip line comments in lookahead
            if (ch == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                i += 2;
                while (i < text.Length && text[i] != '\n' && text[i] != '\r')
                    i++;
                if (i < text.Length)
                    i++;
                continue;
            }

            // Skip block comments in lookahead
            if (ch == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                i += 2;
                while (i < text.Length)
                {
                    if (text[i] == '*' && i + 1 < text.Length && text[i + 1] == '/')
                    {
                        i += 2;
                        break;
                    }

                    i++;
                }

                continue;
            }

            return ch is '}' or ']';
        }

        return false;
    }

    private static int ProcessIdentifier(string text, int start, StringBuilder sb)
    {
        var i = start;
        while (i < text.Length && IsIdentChar(text[i]))
            i++;

        var word = text.AsSpan(start, i - start);

        // Look ahead for : (unquoted key)
        var j = i;
        while (j < text.Length && char.IsWhiteSpace(text[j]))
            j++;

        if (j < text.Length && text[j] == ':')
        {
            sb.Append('"');
            sb.Append(word);
            sb.Append('"');
            return i;
        }

        // JSON literals — pass through
        if (word is "true" or "false" or "null")
        {
            sb.Append(word);
            return i;
        }

        // Non-finite values → null
        if (word is "Infinity" or "NaN")
        {
            sb.Append("null");
            return i;
        }

        // Unknown identifier — emit as-is
        sb.Append(word);
        return i;
    }

    private static int ConvertHexNumber(string text, int start, StringBuilder sb)
    {
        var i = start + 2; // skip 0x
        var hexStart = i;
        while (i < text.Length && char.IsAsciiHexDigit(text[i]))
            i++;

        if (i == hexStart)
        {
            sb.Append(text, start, 2); // no hex digits, emit 0x as-is
            return i;
        }

        var hexSpan = text.AsSpan(hexStart, i - hexStart);
        if (long.TryParse(hexSpan, System.Globalization.NumberStyles.HexNumber, null, out var value))
            sb.Append(value);
        else
            sb.Append(text, start, i - start); // overflow — emit raw

        return i;
    }

    private static bool IsIdentStart(char ch)
        => char.IsLetter(ch) || ch is '_' or '$';

    private static bool IsIdentChar(char ch)
        => char.IsLetterOrDigit(ch) || ch is '_' or '$';
}
