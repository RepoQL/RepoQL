using System.Globalization;
using Google.Protobuf.Collections;
using Google.Protobuf.WellKnownTypes;
using RepoQL.ConsoleApp.Commands;
using RepoQL.Contracts;

namespace RepoQL.ConsoleApp.Formatters;

public class UnstructuredFormatter : IResultFormatter
{
    private readonly Templating.ITemplateRenderer _renderer;
    public UnstructuredFormatter(Templating.ITemplateRenderer renderer) => _renderer = renderer;

    public ResultFormat Format => ResultFormat.Unstructured;

    public async Task<string[]> FormatAsync(RawQueryResponse result, int maxRows = 100, long? totalRowCount = null, CancellationToken cancellationToken = default)
    {
        var cols = result.Columns;
        var rows = result.Rows;

        if (rows.Count == 0)
            return new[] { "<Empty>" };

        var displayCount = Math.Min(rows.Count, maxRows);
        var total = totalRowCount ?? rows.Count;

        if (cols.Count <= 1)
            return await RenderScalars(rows, displayCount, total, cancellationToken);

        var includeIdx = GetIncludedColumnIndexes(cols, rows, displayCount);
        var hasMultiline = HasMultiline(rows, includeIdx, displayCount);
        if (includeIdx.Count <= 6 && displayCount <= 100 && !hasMultiline)
            return await RenderTable(cols, rows, includeIdx, displayCount, total, cancellationToken);
        return await RenderKv(cols, rows, includeIdx, displayCount, total, cancellationToken);
    }

    private async Task<string[]> RenderScalars(RepeatedField<RowData> rows, int displayCount, long total, CancellationToken ct)
    {
        var items = new List<string>(displayCount);
        for (int i = 0; i < displayCount && i < rows.Count; i++)
        {
            var v = rows[i].Values.Count > 0 ? rows[i].Values[0] : Value.ForNull();
            if (IsEmptyValue(v)) continue;
            if (v.KindCase == Value.KindOneofCase.StringValue)
                items.Add((v.StringValue ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Replace('\t', ' '));
            else
                items.Add(ToSingleLineString(v));
        }
        var payload = new Dictionary<string, object?>
        {
            ["items"] = items,
            ["display_count"] = items.Count,
            ["total_count"] = total
        };
        var text = await _renderer.RenderAsync("Unstructured/list", payload, ct);
        return SplitLines(text);
    }

    private static List<int> GetIncludedColumnIndexes(RepeatedField<ColumnSchema> cols, RepeatedField<RowData> rows, int displayCount)
    {
        var list = new List<int>();
        for (int c = 0; c < cols.Count; c++)
        {
            bool any = false;
            for (int r = 0; r < displayCount && r < rows.Count; r++)
            {
                var v = c < rows[r].Values.Count ? rows[r].Values[c] : Value.ForNull();
                if (!IsEmptyValue(v)) { any = true; break; }
            }
            if (any) list.Add(c);
        }
        return list;
    }

    private Task<string[]> RenderKv(RepeatedField<ColumnSchema> cols, RepeatedField<RowData> rows, List<int> includeIdx, int displayCount, long total, CancellationToken ct)
    {
        var lines = new List<string>();
        var produced = 0;
        var anyOutput = false;

        for (int r = 0; r < displayCount && r < rows.Count; r++)
        {
            var row = rows[r];
            string? firstLine = null;
            var extraLines = new List<string>();

            foreach (var colIdx in includeIdx)
            {
                var name = cols[colIdx].Name;
                string[] textLines;

                if (colIdx < row.Values.Count)
                {
                    var value = row.Values[colIdx];
                    if (value.KindCase == Value.KindOneofCase.NullValue || IsEmptyValue(value))
                        continue;
                    if (value.KindCase == Value.KindOneofCase.StringValue)
                    {
                        textLines = SplitMultiline(value.StringValue ?? string.Empty);
                    }
                    else
                    {
                        textLines = [ToSingleLineString(value)];
                    }
                }
                else
                {
                    textLines = Array.Empty<string>();
                }

                if (textLines.Length == 0)
                    continue;

                if (firstLine is null)
                {
                    firstLine = $"{name}: {textLines[0]}";
                    for (int i = 1; i < textLines.Length; i++)
                        extraLines.Add($"  {textLines[i]}");
                }
                else
                {
                    extraLines.Add($"{name}: {textLines[0]}");
                    for (int i = 1; i < textLines.Length; i++)
                        extraLines.Add($"  {textLines[i]}");
                }
            }

            if (firstLine is not null)
            {
                var rowHasMultipleLines = extraLines.Count > 0;
                if (rowHasMultipleLines && anyOutput)
                {
                    lines.Add("---");
                }

                lines.Add(firstLine);
                if (rowHasMultipleLines)
                {
                    lines.AddRange(extraLines);
                }

                produced++;
                anyOutput = true;
            }
        }

        lines.Add($"[{produced} / {total} rows]");
        return Task.FromResult(lines.ToArray());
    }

    private async Task<string[]> RenderTable(RepeatedField<ColumnSchema> cols, RepeatedField<RowData> rows, List<int> includeIdx, int displayCount, long total, CancellationToken ct)
    {
        const int maxCell = 60;
        var widths = includeIdx.ToDictionary(i => i, i => Math.Max(5, cols[i].Name.Length));
        for (int r = 0; r < displayCount && r < rows.Count; r++)
        {
            var vals = rows[r].Values;
            foreach (var c in includeIdx)
            {
                if (c >= vals.Count) continue;
                var s = ToSingleLineString(vals[c]);
                if (s.Length > maxCell) s = s.Substring(0, maxCell - 3) + "...";
                widths[c] = Math.Max(widths[c], s.Length);
            }
        }
        string Pad(string s, int w) => s.Length >= w ? s : s + new string(' ', w - s.Length);
        var header = string.Join(" | ", includeIdx.Select(i => Pad(cols[i].Name, widths[i])));
        var sep = string.Join("-+-", includeIdx.Select(i => new string('-', widths[i])));
        var outLines = new List<string>(displayCount);
        for (int r = 0; r < displayCount && r < rows.Count; r++)
        {
            var rowCells = new List<string>(includeIdx.Count);
            foreach (var c in includeIdx)
            {
                var v = c < rows[r].Values.Count ? rows[r].Values[c] : Value.ForNull();
                var s = ToSingleLineString(v);
                if (s.Length > maxCell) s = s.Substring(0, maxCell - 3) + "...";
                rowCells.Add(Pad(s, widths[c]));
            }
            if (rowCells.Count > 0) outLines.Add(string.Join(" | ", rowCells));
        }
        var payload = new Dictionary<string, object?>
        {
            ["header"] = header,
            ["separator"] = sep,
            ["rows"] = outLines,
            ["display_count"] = outLines.Count,
            ["total_count"] = total
        };
        var text = await _renderer.RenderAsync("Unstructured/table", payload, ct);
        return SplitLines(text);
    }

    private static string[] SplitLines(string s) => s.Replace("\r\n", "\n").TrimEnd().Split('\n');

    private static string[] SplitMultiline(string text)
    {
        if (string.IsNullOrEmpty(text))
            return Array.Empty<string>();

        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        return normalized.Split('\n');
    }

    private static string ToSingleLineString(Value v) => v.KindCase switch
    {
        Value.KindOneofCase.NullValue => "null",
        Value.KindOneofCase.BoolValue => v.BoolValue ? "true" : "false",
        Value.KindOneofCase.NumberValue => v.NumberValue.ToString("G", CultureInfo.InvariantCulture),
        Value.KindOneofCase.StringValue => (v.StringValue ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' '),
        Value.KindOneofCase.StructValue => MinifyJsonStruct(v.StructValue),
        Value.KindOneofCase.ListValue => MinifyJsonList(v.ListValue),
        _ => string.Empty
    };

    private static string MinifyJsonStruct(Struct s)
    {
        var parts = s.Fields.Select(kv => $"\"{kv.Key}\":{ToSingleLineString(kv.Value)}");
        return "{" + string.Join(',', parts) + "}";
    }

    private static string MinifyJsonList(ListValue l) => "[" + string.Join(',', l.Values.Select(ToSingleLineString)) + "]";

    private static bool IsEmptyValue(Value v)
    {
        switch (v.KindCase)
        {
            case Value.KindOneofCase.NullValue:
                return true;
            case Value.KindOneofCase.StringValue:
                var s = v.StringValue ?? string.Empty;
                if (string.IsNullOrWhiteSpace(s)) return true;
                return string.Equals(s.Trim(), "null", StringComparison.OrdinalIgnoreCase);
            case Value.KindOneofCase.StructValue:
                return v.StructValue?.Fields == null || v.StructValue.Fields.Count == 0;
            case Value.KindOneofCase.ListValue:
                return v.ListValue?.Values == null || v.ListValue.Values.Count == 0;
            default:
                return false;
        }
    }

    private static bool HasMultiline(RepeatedField<RowData> rows, List<int> includeIdx, int displayCount)
    {
        for (int r = 0; r < displayCount && r < rows.Count; r++)
        {
            foreach (var c in includeIdx)
            {
                if (c >= rows[r].Values.Count) continue;
                var vv = rows[r].Values[c];
                if (vv.KindCase == Value.KindOneofCase.StringValue && (vv.StringValue?.IndexOf('\n') ?? -1) >= 0)
                    return true;
            }
        }
        return false;
    }
}
