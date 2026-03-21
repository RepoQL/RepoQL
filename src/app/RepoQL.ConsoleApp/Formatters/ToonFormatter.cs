using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Google.Protobuf.WellKnownTypes;
using RepoQL.ConsoleApp.Commands;
using RepoQL.Contracts;

namespace RepoQL.ConsoleApp.Formatters;

/// <summary>
/// Formats query results as TOON (Token-Oriented Object Notation).
/// TOON is a compact, LLM-friendly format that reduces token usage by ~40% compared to JSON.
/// See https://toonformat.dev/ for specification.
/// </summary>
public class ToonFormatter : IResultFormatter
{
    private const int MaxInlineValueLength = 80;

    // Regex for numeric-looking strings that need quoting
    private static readonly Regex NumericPattern = new(
        @"^-?\d+(?:\.\d+)?(?:e[+-]?\d+)?$|^0\d+$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public ResultFormat Format => ResultFormat.Toon;

    public Task<string[]> FormatAsync(
        RawQueryResponse result,
        int maxRows = 100,
        long? totalRowCount = null,
        CancellationToken cancellationToken = default)
    {
        var cols = result.Columns?.ToArray() ?? [];
        var rows = result.Rows?.ToArray() ?? [];
        var take = Math.Min(rows.Length, maxRows);

        if (cols.Length == 0 || take == 0)
            return Task.FromResult(Array.Empty<string>());

        // Detect output mode
        if (cols.Length == 1)
            return Task.FromResult(FormatSingleColumn(cols, rows, take));

        if (cols.Length == 2 && HasUriColumn(cols, out var uriIndex))
            return Task.FromResult(FormatTwoColumnUri(cols, rows, take, uriIndex));

        return Task.FromResult(FormatTabular(cols, rows, take));
    }

    /// <summary>
    /// Single-column mode: just values, one per line (no header)
    /// For single-row multiline strings (like tree output), return raw lines unquoted.
    /// </summary>
    private static string[] FormatSingleColumn(ColumnSchema[] cols, RowData[] rows, int take)
    {
        var lines = new List<string>(take);

        // Special case: single row with a multiline string - return raw lines
        if (take == 1 && rows.Length > 0 && rows[0].Values.Count > 0)
        {
            var val = rows[0].Values[0];
            if (val.KindCase == Value.KindOneofCase.StringValue)
            {
                var s = val.StringValue ?? "";
                if (s.Contains('\n'))
                {
                    // Return raw lines without quoting - preserves tree structure
                    return s.Split('\n').Select(line => line.TrimEnd('\r')).ToArray();
                }
            }
        }

        for (var r = 0; r < take; r++)
        {
            var val = r < rows.Length && rows[r].Values.Count > 0
                ? rows[r].Values[0]
                : Value.ForNull();
            lines.Add(FormatValue(val));
        }
        return lines.ToArray();
    }

    /// <summary>
    /// Two-column URI mode: uri: value (inline or block format)
    /// </summary>
    private static string[] FormatTwoColumnUri(ColumnSchema[] cols, RowData[] rows, int take, int uriIndex)
    {
        var valueIndex = uriIndex == 0 ? 1 : 0;
        var lines = new List<string>(take * 2); // may need extra lines for block format

        for (var r = 0; r < take; r++)
        {
            var uriVal = r < rows.Length && rows[r].Values.Count > uriIndex
                ? rows[r].Values[uriIndex]
                : Value.ForNull();
            var otherVal = r < rows.Length && rows[r].Values.Count > valueIndex
                ? rows[r].Values[valueIndex]
                : Value.ForNull();

            var uri = FormatValueRaw(uriVal);
            var value = FormatValueRaw(otherVal);

            // Decide inline vs block
            var useBlock = value.Length > MaxInlineValueLength || value.Contains('\n');

            if (useBlock)
            {
                lines.Add($"{uri}:");
                // Indent each line of the value
                foreach (var line in value.Split('\n'))
                {
                    lines.Add($"  {line}");
                }
            }
            else
            {
                lines.Add($"{uri}: {value}");
            }
        }
        return lines.ToArray();
    }

    /// <summary>
    /// Multi-column tabular mode: TOON format with header
    /// </summary>
    private static string[] FormatTabular(ColumnSchema[] cols, RowData[] rows, int take)
    {
        var lines = new List<string>(take + 1);

        // Header: [count]{field1,field2,...}:
        var fieldNames = string.Join(",", cols.Select(c => FormatFieldName(c.Name)));
        lines.Add($"[{take}]{{{fieldNames}}}:");

        // Data rows
        for (var r = 0; r < take; r++)
        {
            var values = new List<string>(cols.Length);
            for (var c = 0; c < cols.Length; c++)
            {
                var val = c < rows[r].Values.Count ? rows[r].Values[c] : Value.ForNull();
                values.Add(FormatValue(val));
            }
            lines.Add(string.Join(",", values));
        }

        return lines.ToArray();
    }

    private static bool HasUriColumn(ColumnSchema[] cols, out int uriIndex)
    {
        for (var i = 0; i < cols.Length; i++)
        {
            if (cols[i].Name.Contains("uri", StringComparison.OrdinalIgnoreCase))
            {
                uriIndex = i;
                return true;
            }
        }
        uriIndex = -1;
        return false;
    }

    private static string FormatFieldName(string name)
    {
        // Field names in TOON header only need quoting if they contain special chars
        if (NeedsQuoting(name, isFieldName: true))
            return Quote(name);
        return name;
    }

    /// <summary>
    /// Format a value with TOON quoting rules applied.
    /// Only string values need quoting consideration - other types render as-is.
    /// </summary>
    private static string FormatValue(Value v)
    {
        // Only string values need quoting consideration
        if (v.KindCase == Value.KindOneofCase.StringValue)
        {
            var s = v.StringValue ?? "";
            if (NeedsQuoting(s, isFieldName: false))
                return Quote(s);
            return s;
        }

        // All other types (null, bool, number, struct, list) render without quoting
        return FormatValueRaw(v);
    }

    /// <summary>
    /// Format a value without quoting (raw string representation)
    /// </summary>
    private static string FormatValueRaw(Value v)
    {
        return v.KindCase switch
        {
            Value.KindOneofCase.NullValue => "null",
            Value.KindOneofCase.BoolValue => v.BoolValue ? "true" : "false",
            Value.KindOneofCase.NumberValue => FormatNumber(v.NumberValue),
            Value.KindOneofCase.StringValue => v.StringValue ?? "",
            Value.KindOneofCase.StructValue => FormatStruct(v.StructValue),
            Value.KindOneofCase.ListValue => FormatList(v.ListValue),
            _ => "null"
        };
    }

    /// <summary>
    /// Format number in canonical form (no exponent, no trailing zeros)
    /// </summary>
    private static string FormatNumber(double num)
    {
        if (double.IsNaN(num) || double.IsInfinity(num))
            return "null";

        // Normalize -0 to 0
        if (num == 0)
            return "0";

        // Check if it's an integer value
        if (num == Math.Floor(num) && num >= long.MinValue && num <= long.MaxValue)
            return ((long)num).ToString();

        // Use G15 for reasonable precision without floating-point artifacts
        // G17 gives full round-trip precision but shows artifacts like 3.1400000000000001
        var str = num.ToString("G15");

        // Remove exponent notation if present
        if (str.Contains('E') || str.Contains('e'))
        {
            // Try converting to decimal for cleaner representation
            try
            {
                return ((decimal)num).ToString("G");
            }
            catch
            {
                // Fall back to the original string if decimal conversion fails
                return str;
            }
        }

        // Remove trailing zeros after decimal point
        if (str.Contains('.'))
        {
            str = str.TrimEnd('0').TrimEnd('.');
        }

        return str;
    }

    /// <summary>
    /// Format struct as inline JSON
    /// </summary>
    private static string FormatStruct(Struct s)
    {
        var abw = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(abw, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            foreach (var kv in s.Fields)
            {
                writer.WritePropertyName(kv.Key);
                WriteJsonValue(writer, kv.Value);
            }
            writer.WriteEndObject();
            writer.Flush();
        }
        return Encoding.UTF8.GetString(abw.WrittenSpan);
    }

    /// <summary>
    /// Format list as inline JSON array
    /// </summary>
    private static string FormatList(ListValue l)
    {
        var abw = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(abw, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartArray();
            foreach (var item in l.Values)
                WriteJsonValue(writer, item);
            writer.WriteEndArray();
            writer.Flush();
        }
        return Encoding.UTF8.GetString(abw.WrittenSpan);
    }

    private static void WriteJsonValue(Utf8JsonWriter writer, Value v)
    {
        switch (v.KindCase)
        {
            case Value.KindOneofCase.NullValue:
                writer.WriteNullValue();
                break;
            case Value.KindOneofCase.BoolValue:
                writer.WriteBooleanValue(v.BoolValue);
                break;
            case Value.KindOneofCase.NumberValue:
                writer.WriteNumberValue(v.NumberValue);
                break;
            case Value.KindOneofCase.StringValue:
                writer.WriteStringValue(v.StringValue ?? "");
                break;
            case Value.KindOneofCase.StructValue:
                writer.WriteStartObject();
                foreach (var kv in v.StructValue.Fields)
                {
                    writer.WritePropertyName(kv.Key);
                    WriteJsonValue(writer, kv.Value);
                }
                writer.WriteEndObject();
                break;
            case Value.KindOneofCase.ListValue:
                writer.WriteStartArray();
                foreach (var item in v.ListValue.Values)
                    WriteJsonValue(writer, item);
                writer.WriteEndArray();
                break;
            default:
                writer.WriteNullValue();
                break;
        }
    }

    /// <summary>
    /// Check if a string needs quoting per TOON spec
    /// </summary>
    private static bool NeedsQuoting(string s, bool isFieldName)
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
        if (NumericPattern.IsMatch(s))
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

    /// <summary>
    /// Quote a string with TOON escape rules
    /// </summary>
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
}
