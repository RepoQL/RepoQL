using System.Text;
using RepoQL.Contracts;

namespace RepoQL.McpServer.Formatting;

/// <summary>
/// Minimal text formatting utilities for MCP responses (no JSON, human/AI friendly).
/// </summary>
public static class TextFormatter
{
    public static string FormatTable(RawQueryResponse result)
    {
        var cols = result.Columns.Select(c => c.Name ?? string.Empty).ToArray();
        var sb = new StringBuilder();

        if (cols.Length == 0)
        {
            // No columns -> print row count/truncated info
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

        // Header
        sb.AppendLine(string.Join(" | ", cols.Select((c, i) => Pad(c, widths[i]))));
        sb.AppendLine(string.Join("-+-", widths.Select(w => new string('-', w))));

        // Rows
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

    public static string FormatSummaries(GetDocumentSummariesResponse response)
    {
        var sb = new StringBuilder();

        foreach (var doc in response.Results)
        {
            // File header
            sb.AppendLine(doc.Uri ?? string.Empty);

            if (!string.IsNullOrEmpty(doc.Error))
            {
                sb.AppendLine($"  ERROR: {doc.Error}");
                sb.AppendLine();
                continue;
            }

            if (doc.Status == SummaryStatus.NotFound)
            {
                sb.AppendLine("  (not found)");
                sb.AppendLine();
                continue;
            }

            if (doc.Annotations.Count == 0)
            {
                sb.AppendLine("  (no annotations)");
                sb.AppendLine();
                continue;
            }

            // Prefer outline annotations first
            var anns = doc.Annotations
                .OrderBy(a => string.Equals(a.Kind, "outline", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(a => a.Kind)
                .ToList();

            foreach (var ann in anns)
            {
                var isOutline = string.Equals(ann.Kind, "outline", StringComparison.OrdinalIgnoreCase);
                if (!isOutline)
                {
                    sb.AppendLine($"  [{ann.Kind}]");
                }

                var lines = (ann.Message ?? string.Empty).Replace("\r\n", "\n").Split('\n');
                foreach (var line in lines)
                {
                    if (!string.IsNullOrWhiteSpace(line))
                        sb.AppendLine($"  {line}");
                }
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    public static string FormatXray(RawQueryResponse result, int level)
    {
        // Expect columns: uri, headline, summary, structure
        var sb = new StringBuilder();
        foreach (var row in result.Rows)
        {
            var vals = row.Values;
            var uri = vals.Count > 0 ? ToDisplay(vals[0]) : string.Empty;
            var headline = vals.Count > 1 ? ToDisplay(vals[1]) : string.Empty;
            var summary = vals.Count > 2 ? ToDisplay(vals[2]) : string.Empty;
            var structure = vals.Count > 3 ? ToDisplay(vals[3]) : string.Empty;

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

    public static string FormatXray(IReadOnlyList<RepoQL.Contracts.RowData> rows, int level)
    {
        var sb = new StringBuilder();
        foreach (var row in rows)
        {
            var vals = row.Values;
            var uri = vals.Count > 0 ? ToDisplay(vals[0]) : string.Empty;
            var headline = vals.Count > 1 ? ToDisplay(vals[1]) : string.Empty;
            var summary = vals.Count > 2 ? ToDisplay(vals[2]) : string.Empty;
            var structure = vals.Count > 3 ? ToDisplay(vals[3]) : string.Empty;

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
