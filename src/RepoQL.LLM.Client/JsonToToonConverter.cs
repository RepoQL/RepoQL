using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RepoQL.LLM.Client;

/// <summary>
/// Converts JSON array data to TOON (Token-Oriented Object Notation) format.
/// TOON is a compact, LLM-friendly format that reduces token usage by ~40% compared to JSON.
/// See https://toonformat.dev/ for specification.
/// </summary>
public static partial class JsonToToonConverter
{
    /// <summary>
    /// Converts a JSON array of objects to TOON format.
    /// </summary>
    /// <param name="json">JSON array string, e.g., [{"uri":"file:///a.cs","headline":"test"}]</param>
    /// <returns>TOON formatted string.</returns>
    public static string Convert(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return "";

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Array)
                return json; // Return as-is if not an array

            var rows = root.EnumerateArray().ToList();
            if (rows.Count == 0)
                return "";

            // Get column names from first row
            var columns = new List<string>();
            if (rows[0].ValueKind == JsonValueKind.Object)
            {
                columns = rows[0].EnumerateObject().Select(p => p.Name).ToList();
            }

            if (columns.Count == 0)
                return json; // Return as-is if no columns

            var sb = new StringBuilder();

            // Header: [count]{field1,field2,...}:
            sb.Append('[');
            sb.Append(rows.Count);
            sb.Append("]{");
            sb.Append(string.Join(",", columns.Select(FormatFieldName)));
            sb.AppendLine("}:");

            // Data rows
            foreach (var row in rows)
            {
                if (row.ValueKind != JsonValueKind.Object)
                    continue;

                var values = new List<string>();
                foreach (var colName in columns)
                {
                    if (row.TryGetProperty(colName, out var prop))
                    {
                        values.Add(FormatValue(prop));
                    }
                    else
                    {
                        values.Add("null");
                    }
                }
                sb.AppendLine(string.Join(",", values));
            }

            return sb.ToString().TrimEnd();
        }
        catch (JsonException)
        {
            // If parsing fails, return as-is
            return json;
        }
    }

    private static string FormatFieldName(string name)
    {
        if (NeedsQuoting(name))
            return Quote(name);
        return name;
    }

    private static string FormatValue(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Null => "null",
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Number => FormatNumber(element),
            JsonValueKind.String => FormatString(element.GetString() ?? ""),
            JsonValueKind.Array => FormatArray(element),
            JsonValueKind.Object => FormatObject(element),
            _ => "null"
        };
    }

    private static string FormatNumber(JsonElement element)
    {
        if (element.TryGetInt64(out var longVal))
            return longVal.ToString();
        if (element.TryGetDouble(out var doubleVal))
        {
            if (double.IsNaN(doubleVal) || double.IsInfinity(doubleVal))
                return "null";
            if (doubleVal == Math.Floor(doubleVal) && doubleVal >= long.MinValue && doubleVal <= long.MaxValue)
                return ((long)doubleVal).ToString();
            return doubleVal.ToString("G15").TrimEnd('0').TrimEnd('.');
        }
        return element.GetRawText();
    }

    private static string FormatString(string s)
    {
        if (NeedsQuoting(s))
            return Quote(s);
        return s;
    }

    private static string FormatArray(JsonElement element)
    {
        // For arrays, use compact JSON representation
        return element.GetRawText();
    }

    private static string FormatObject(JsonElement element)
    {
        // For nested objects, use compact JSON representation
        return element.GetRawText();
    }

    private static bool NeedsQuoting(string s)
    {
        if (string.IsNullOrEmpty(s))
            return true;

        // Leading/trailing whitespace
        if (char.IsWhiteSpace(s[0]) || char.IsWhiteSpace(s[^1]))
            return true;

        // Reserved words (case-sensitive)
        if (s is "true" or "false" or "null")
            return true;

        // Starts with hyphen (could be confused with list item)
        if (s.StartsWith('-'))
            return true;

        // Looks like a number
        if (NumericPattern().IsMatch(s))
            return true;

        // Contains special characters
        foreach (var c in s)
        {
            // Control characters
            if (char.IsControl(c))
                return true;

            // Special TOON characters
            if (c is ',' or '"' or '\\' or ':' or '[' or ']' or '{' or '}')
                return true;

            // Newline
            if (c is '\n' or '\r')
                return true;
        }

        return false;
    }

    private static string Quote(string s)
    {
        var sb = new StringBuilder(s.Length + 2);
        sb.Append('"');
        foreach (var c in s)
        {
            sb.Append(c switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                _ => c.ToString()
            });
        }
        sb.Append('"');
        return sb.ToString();
    }

    [GeneratedRegex(@"^-?\d+(?:\.\d+)?(?:e[+-]?\d+)?$|^0\d+$", RegexOptions.IgnoreCase)]
    private static partial Regex NumericPattern();
}
