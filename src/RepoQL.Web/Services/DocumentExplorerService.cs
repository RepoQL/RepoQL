using System.Globalization;
using Google.Protobuf.WellKnownTypes;

namespace RepoQL.Web.Services;

/// <summary>
/// Provides typed accessors over RepoQL views used by the explorer view.
/// </summary>
internal sealed class DocumentExplorerService
{
    private const string DocumentQuery = """
SELECT
    uri AS document_uri,
    name AS file_name,
    lang AS media_label,
    byte_size,
    '' AS kinds_summary,
    headline,
    summary,
    structure
FROM Files
{FILTER}
ORDER BY lower(name)
LIMIT 250
""";

    private readonly RepoQlConnectionManager _connectionManager;

    public DocumentExplorerService(RepoQlConnectionManager connectionManager)
    {
        _connectionManager = connectionManager;
    }

    public async Task<IReadOnlyList<DocumentListItem>> GetDocumentsAsync(string? searchText = null, CancellationToken cancellationToken = default)
    {
        var client = await _connectionManager.GetClientAsync(cancellationToken).ConfigureAwait(false);

        string sql;
        IReadOnlyList<object?> parameters;

        if (string.IsNullOrWhiteSpace(searchText))
        {
            sql = DocumentQuery.Replace("{FILTER}", string.Empty, StringComparison.Ordinal);
            parameters = Array.Empty<object?>();
        }
        else
        {
            sql = DocumentQuery.Replace("{FILTER}", "WHERE upper(file_name) LIKE ? OR upper(document_uri) LIKE ?", StringComparison.Ordinal);
            var pattern = $"%{searchText.Trim().ToUpperInvariant()}%";
            parameters = new object?[] { pattern, pattern };
        }

        var response = await client.ExecuteRawQueryAsync(sql, parameters, cancellationToken: cancellationToken).ConfigureAwait(false);

        var results = new List<DocumentListItem>(response.Rows.Count);
        foreach (var row in response.Rows)
        {
            var uri = GetString(row, response.Columns, "document_uri");
            var fileName = GetString(row, response.Columns, "file_name");
            var media = GetString(row, response.Columns, "media_label");
            var byteSize = TryParseLong(row, response.Columns, "byte_size");
            var kinds = GetString(row, response.Columns, "kinds_summary");
            var headline = GetNullableString(row, response.Columns, "headline");
            var summary = GetNullableString(row, response.Columns, "summary");
            var structure = GetNullableString(row, response.Columns, "structure");

            results.Add(new DocumentListItem(
                DocumentUri: uri,
                FileName: string.IsNullOrWhiteSpace(fileName) ? uri : fileName,
                MediaLabel: string.IsNullOrWhiteSpace(media) ? "unknown" : media,
                ByteSize: byteSize,
                KindsSummary: kinds,
                Headline: headline,
                Summary: summary,
                Structure: structure));
        }

        return results;
    }

    public async Task<IReadOnlyList<DocumentItem>> GetDocumentItemsAsync(string documentUri, int maxItemsPerDocument = 50, string? includeKinds = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(documentUri);

        var client = await _connectionManager.GetClientAsync(cancellationToken).ConfigureAwait(false);

        // Build kind filter if specified
        var kindFilter = string.IsNullOrWhiteSpace(includeKinds)
            ? ""
            : $"AND c.kind IN ({string.Join(", ", includeKinds.Split(',').Select(k => $"'{k.Trim().ToLowerInvariant()}'"))})";

        var sql = $"""
            SELECT
                c.kind AS item_kind,
                COALESCE(c.properties->>'$.name', c.properties->>'$.text', '?') AS item_label,
                COALESCE(c.uri, d.uri || '#line=' || s.start_line || ',' || s.end_line) AS item_uri
            FROM node d
            JOIN edge e ON e.source_node_id = d.id AND e.is_composition = TRUE
            JOIN node c ON c.id = e.destination_node_id
            LEFT JOIN span s ON s.id = c.span_id
            WHERE lower(d.uri) = lower(?)
              AND d.kind = 'document'
              {kindFilter}
            ORDER BY COALESCE(s.start_line, e.ordinal, 0), c.kind
            LIMIT ?
            """;

        var parameters = new object?[] { documentUri, maxItemsPerDocument };

        var response = await client.ExecuteRawQueryAsync(sql, parameters, cancellationToken: cancellationToken).ConfigureAwait(false);
        var items = new List<DocumentItem>(response.Rows.Count);
        foreach (var row in response.Rows)
        {
            items.Add(new DocumentItem(
                Kind: GetString(row, response.Columns, "item_kind"),
                Label: GetString(row, response.Columns, "item_label"),
                ItemUri: GetString(row, response.Columns, "item_uri")));
        }
        return items;
    }

    public async Task<IReadOnlyList<SnippetLine>> GetSnippetAsync(string focusUri, int contextLines = 3, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(focusUri);

        var client = await _connectionManager.GetClientAsync(cancellationToken).ConfigureAwait(false);
        var sql = """
            SELECT
                line_number,
                text,
                is_focus,
                focus_start_column,
                focus_end_column,
                language
            FROM snippet(?, ?)
            """;

        var response = await client.ExecuteRawQueryAsync(sql, new object?[] { focusUri, contextLines }, cancellationToken: cancellationToken).ConfigureAwait(false);
        var lines = new List<SnippetLine>(response.Rows.Count);
        foreach (var row in response.Rows)
        {
            lines.Add(new SnippetLine(
                LineNumber: (int?)TryParseLong(row, response.Columns, "line_number") ?? 0,
                Text: GetString(row, response.Columns, "text"),
                IsFocus: GetBool(row, response.Columns, "is_focus"),
                FocusStartColumn: (int?)TryParseLong(row, response.Columns, "focus_start_column"),
                FocusEndColumn: (int?)TryParseLong(row, response.Columns, "focus_end_column"),
                Language: GetString(row, response.Columns, "language")));
        }
        return lines;
    }

    private static string GetString(RepoQL.Contracts.RowData row, IList<RepoQL.Contracts.ColumnSchema> columns, string name)
    {
        var idx = FindColumnIndex(columns, name);
        if (idx < 0 || idx >= row.Values.Count)
            return string.Empty;
        return row.Values[idx].StringValue ?? row.Values[idx].ToString() ?? string.Empty;
    }

    private static long? TryParseLong(RepoQL.Contracts.RowData row, IList<RepoQL.Contracts.ColumnSchema> columns, string name)
    {
        var idx = FindColumnIndex(columns, name);
        if (idx < 0 || idx >= row.Values.Count)
            return null;
        var value = row.Values[idx];
        return value.KindCase switch
        {
            Google.Protobuf.WellKnownTypes.Value.KindOneofCase.NumberValue => (long?)Convert.ToInt64(value.NumberValue, CultureInfo.InvariantCulture),
            Google.Protobuf.WellKnownTypes.Value.KindOneofCase.StringValue when long.TryParse(value.StringValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null
        };
    }

    private static string? GetNullableString(RepoQL.Contracts.RowData row, IList<RepoQL.Contracts.ColumnSchema> columns, string name)
    {
        var idx = FindColumnIndex(columns, name);
        if (idx < 0 || idx >= row.Values.Count)
            return null;
        var value = row.Values[idx];
        return value.KindCase switch
        {
            Value.KindOneofCase.StringValue => value.StringValue,
            Value.KindOneofCase.NullValue => null,
            _ => value.ToString()
        };
    }

    private static bool GetBool(RepoQL.Contracts.RowData row, IList<RepoQL.Contracts.ColumnSchema> columns, string name)
    {
        var idx = FindColumnIndex(columns, name);
        if (idx < 0 || idx >= row.Values.Count)
            return false;
        var value = row.Values[idx];
        return value.KindCase switch
        {
            Google.Protobuf.WellKnownTypes.Value.KindOneofCase.BoolValue => value.BoolValue,
            Google.Protobuf.WellKnownTypes.Value.KindOneofCase.NumberValue => Math.Abs(value.NumberValue) > double.Epsilon,
            Google.Protobuf.WellKnownTypes.Value.KindOneofCase.StringValue => bool.TryParse(value.StringValue, out var parsed) && parsed,
            _ => false
        };
    }

    private static int FindColumnIndex(IList<RepoQL.Contracts.ColumnSchema> columns, string name)
    {
        for (var i = 0; i < columns.Count; i++)
        {
            if (string.Equals(columns[i].Name, name, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return -1;
    }
}
