using System.Text;
using RepoQL.Contracts;

namespace RepoQL.App.Formatting;

/// <summary>
/// Minimal text formatting utilities for MCP/CLI responses (no JSON, human/AI friendly).
/// </summary>
public static class TextFormatter
{
    public static string FormatTable(RawQueryResponse result)
    {
        var cols = result.Columns.Select(c => c.Name ?? string.Empty).ToArray();
        var sb = new StringBuilder();

        if (cols.Length == 0)
        {
            sb.AppendLine("(no columns)");
            sb.AppendLine($"rows: {result.RowCount}");
            if (result.Truncated) sb.AppendLine("(truncated)");
            return sb.ToString();
        }

        var widths = new int[cols.Length];
        for (var i = 0; i < cols.Length; i++) widths[i] = cols[i].Length;

        foreach (var row in result.Rows)
        {
            for (var i = 0; i < cols.Length && i < row.Values.Count; i++)
            {
                var s = ToDisplay(row.Values[i]);
                if (s.Length > widths[i]) widths[i] = s.Length;
            }
        }

        sb.AppendLine(string.Join(" | ", cols.Select((c, i) => Pad(c, widths[i]))));
        sb.AppendLine(string.Join("-+-", widths.Select(w => new string('-', w))));

        foreach (var row in result.Rows)
        {
            var cells = new List<string>(cols.Length);
            for (var i = 0; i < cols.Length; i++)
            {
                var s = i < row.Values.Count ? ToDisplay(row.Values[i]) : string.Empty;
                cells.Add(Pad(s, widths[i]));
            }
            sb.AppendLine(string.Join(" | ", cells));
        }

        if (result.Truncated)
        {
            sb.AppendLine();
            sb.AppendLine("(truncated)");
        }

        return sb.ToString();
    }

    private static string Pad(string s, int width) => s.PadRight(width);

    private static string ToDisplay(Google.Protobuf.WellKnownTypes.Value v) => v.KindCase switch
    {
        Google.Protobuf.WellKnownTypes.Value.KindOneofCase.NullValue => "NULL",
        Google.Protobuf.WellKnownTypes.Value.KindOneofCase.BoolValue => v.BoolValue ? "true" : "false",
        Google.Protobuf.WellKnownTypes.Value.KindOneofCase.NumberValue => v.NumberValue.ToString(),
        Google.Protobuf.WellKnownTypes.Value.KindOneofCase.StringValue => v.StringValue ?? string.Empty,
        Google.Protobuf.WellKnownTypes.Value.KindOneofCase.ListValue => string.Join(", ", v.ListValue.Values.Select(ToDisplay)),
        Google.Protobuf.WellKnownTypes.Value.KindOneofCase.StructValue => "{...}",
        _ => string.Empty
    };

    private static string ToTextOrEmpty(Google.Protobuf.WellKnownTypes.Value v)
        => v.KindCase == Google.Protobuf.WellKnownTypes.Value.KindOneofCase.StringValue
           ? (v.StringValue ?? string.Empty)
           : string.Empty;

    public static string FormatXray(RawQueryResponse result, int level)
    {
        var sb = new StringBuilder();
        foreach (var row in result.Rows)
        {
            var vals = row.Values;
            var uri = vals.Count > 0 ? ToTextOrEmpty(vals[0]) : string.Empty;
            var headline = vals.Count > 1 ? ToTextOrEmpty(vals[1]) : string.Empty;
            var summary = vals.Count > 2 ? ToTextOrEmpty(vals[2]) : string.Empty;
            var structure = vals.Count > 3 ? ToTextOrEmpty(vals[3]) : string.Empty;

            sb.AppendLine(uri);
            string text = level switch
            {
                2 => !string.IsNullOrWhiteSpace(structure) ? structure : (!string.IsNullOrWhiteSpace(summary) ? summary : headline),
                1 => !string.IsNullOrWhiteSpace(summary) ? summary : (!string.IsNullOrWhiteSpace(structure) ? structure : headline),
                _ => !string.IsNullOrWhiteSpace(headline) ? headline : (!string.IsNullOrWhiteSpace(summary) ? summary : structure)
            } ?? string.Empty;

            if (string.IsNullOrWhiteSpace(text))
            {
                sb.AppendLine("  (no x-ray content)");
                sb.AppendLine();
                continue;
            }

            var lines = text.Replace("\r\n", "\n").Split('\n');
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                sb.AppendLine($"  {line}");
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }

    public static string FormatXray(IReadOnlyList<RowData> rows, int level)
    {
        var sb = new StringBuilder();
        foreach (var row in rows)
        {
            var vals = row.Values;
            var uri = vals.Count > 0 ? ToTextOrEmpty(vals[0]) : string.Empty;
            var headline = vals.Count > 1 ? ToTextOrEmpty(vals[1]) : string.Empty;
            var summary = vals.Count > 2 ? ToTextOrEmpty(vals[2]) : string.Empty;
            var structure = vals.Count > 3 ? ToTextOrEmpty(vals[3]) : string.Empty;

            sb.AppendLine(uri);
            string text = level switch
            {
                2 => !string.IsNullOrWhiteSpace(structure) ? structure : (!string.IsNullOrWhiteSpace(summary) ? summary : headline),
                1 => !string.IsNullOrWhiteSpace(summary) ? summary : (!string.IsNullOrWhiteSpace(structure) ? structure : headline),
                _ => !string.IsNullOrWhiteSpace(headline) ? headline : (!string.IsNullOrWhiteSpace(summary) ? summary : structure)
            } ?? string.Empty;

            if (string.IsNullOrWhiteSpace(text))
            {
                sb.AppendLine("  (no x-ray content)");
                sb.AppendLine();
                continue;
            }

            var lines = text.Replace("\r\n", "\n").Split('\n');
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                sb.AppendLine($"  {line}");
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }
}
