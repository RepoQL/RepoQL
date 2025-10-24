using System.Globalization;
using Google.Protobuf.WellKnownTypes;
using RepoQL.Contracts;

namespace RepoQL.Web.Services;

/// <summary>
/// Provides repository statistics for verification and overview dashboards.
/// </summary>
public sealed class StatsService
{
    private readonly RepoQlConnectionManager _connectionManager;

    public StatsService(RepoQlConnectionManager connectionManager)
    {
        _connectionManager = connectionManager;
    }

    public async Task<StatsOverview> GetOverviewAsync(CancellationToken cancellationToken = default)
    {
        var client = await _connectionManager.GetClientAsync(cancellationToken).ConfigureAwait(false);

        // Get high-level counts
        const string overviewSql = """
            SELECT
              (SELECT COUNT(*) FROM artifact) as files,
              (SELECT COUNT(*) FROM node) as nodes,
              (SELECT COUNT(*) FROM edge) as edges,
              (SELECT COUNT(*) FROM annotation) as annotations
            """;

        var overviewResponse = await client.ExecuteRawQueryAsync(overviewSql, cancellationToken: cancellationToken).ConfigureAwait(false);
        long filesCount = 0, nodesCount = 0, edgesCount = 0, annotationsCount = 0;

        if (overviewResponse.Rows.Count > 0)
        {
            var row = overviewResponse.Rows[0];
            filesCount = GetLong(row, overviewResponse.Columns, "files");
            nodesCount = GetLong(row, overviewResponse.Columns, "nodes");
            edgesCount = GetLong(row, overviewResponse.Columns, "edges");
            annotationsCount = GetLong(row, overviewResponse.Columns, "annotations");
        }

        // Get media breakdown
        var mediaBreakdownTask = client.ExecuteRawQueryAsync(
            """
            SELECT
                COALESCE(media_kind, media_base, 'unknown') AS label,
                COUNT(*) AS total
            FROM xray_documents()
            GROUP BY label
            ORDER BY total DESC
            """,
            cancellationToken: cancellationToken);

        var mediaResponse = await mediaBreakdownTask.ConfigureAwait(false);
        var mediaSlices = new List<MediaSlice>();
        foreach (var row in mediaResponse.Rows)
        {
            var label = GetString(row, mediaResponse.Columns, "label");
            var count = GetDouble(row, mediaResponse.Columns, "total");
            mediaSlices.Add(new MediaSlice(label, count));
        }

        // Ensure the chart has at least one slice
        if (mediaSlices.Count == 0)
        {
            mediaSlices.Add(new MediaSlice("none", 1));
        }

        const string annotationBreakdownSql = "SELECT COALESCE(kind,'unknown') AS kind, COUNT(*) AS total FROM annotation GROUP BY kind ORDER BY total DESC";
        var annotationResponse = await client.ExecuteRawQueryAsync(annotationBreakdownSql, cancellationToken: cancellationToken).ConfigureAwait(false);
        var annotationSummaries = new List<AnnotationSummary>(annotationResponse.Rows.Count);
        foreach (var row in annotationResponse.Rows)
        {
            var kind = GetString(row, annotationResponse.Columns, "kind");
            var total = GetLong(row, annotationResponse.Columns, "total");
            annotationSummaries.Add(new AnnotationSummary(kind, total));
        }

        return new StatsOverview(filesCount, nodesCount, edgesCount, annotationsCount, mediaSlices, annotationSummaries);
    }

    public async Task<IReadOnlyList<MediaTypeDetail>> GetMediaTypeDetailsAsync(CancellationToken cancellationToken = default)
    {
        var client = await _connectionManager.GetClientAsync(cancellationToken).ConfigureAwait(false);

        const string sql = """
            WITH docs AS (
              SELECT
                xd.document_uri,
                COALESCE(xd.media_kind, xd.media_base, 'unknown') AS media_label,
                a.headline,
                a.summary,
                a.structure
              FROM xray_documents() xd
              LEFT JOIN node n ON lower(n.uri) = lower(xd.document_uri)
              LEFT JOIN artifact a ON a.id = n.artifact_id
            )
            SELECT
              media_label,
              COUNT(*) as file_count,
              COUNT(*) FILTER (WHERE headline IS NOT NULL) as with_headline,
              COUNT(*) FILTER (WHERE summary IS NOT NULL) as with_summary,
              COUNT(*) FILTER (WHERE structure IS NOT NULL) as with_structure,
              (SELECT document_uri FROM docs d2 WHERE d2.media_label = d.media_label ORDER BY RANDOM() LIMIT 1) as sample_uri
            FROM docs d
            GROUP BY media_label
            ORDER BY file_count DESC
            """;

        var response = await client.ExecuteRawQueryAsync(sql, cancellationToken: cancellationToken).ConfigureAwait(false);
        var results = new List<MediaTypeDetail>(response.Rows.Count);

        foreach (var row in response.Rows)
        {
            var label = GetString(row, response.Columns, "media_label");
            var fileCount = GetLong(row, response.Columns, "file_count");
            var withHeadline = GetLong(row, response.Columns, "with_headline");
            var withSummary = GetLong(row, response.Columns, "with_summary");
            var withStructure = GetLong(row, response.Columns, "with_structure");
            var sampleUri = GetString(row, response.Columns, "sample_uri");
            var xrayCoverage = fileCount > 0 ? (int)((withHeadline + withSummary + withStructure) / (fileCount * 3.0) * 100) : 0;

            results.Add(new MediaTypeDetail(
                MediaLabel: label,
                FileCount: fileCount,
                WithHeadline: withHeadline,
                WithSummary: withSummary,
                WithStructure: withStructure,
                XRayCoverage: xrayCoverage,
                SampleUri: sampleUri));
        }

        return results;
    }

    public async Task<IReadOnlyList<NodeKindStat>> GetNodeKindStatsAsync(int limit = 0, CancellationToken cancellationToken = default)
    {
        var client = await _connectionManager.GetClientAsync(cancellationToken).ConfigureAwait(false);

        var sql = """
            SELECT
              kind,
              COUNT(*) as count,
              (SELECT uri FROM node n2 WHERE n2.kind = n.kind LIMIT 1) as sample_uri
            FROM node n
            GROUP BY kind
            ORDER BY count DESC
            """;

        if (limit > 0)
        {
            sql += $" LIMIT {limit}";
        }

        var response = await client.ExecuteRawQueryAsync(sql, cancellationToken: cancellationToken).ConfigureAwait(false);
        var results = new List<NodeKindStat>(response.Rows.Count);

        long totalNodes = 0;
        foreach (var row in response.Rows)
        {
            totalNodes += GetLong(row, response.Columns, "count");
        }

        foreach (var row in response.Rows)
        {
            var kind = GetString(row, response.Columns, "kind");
            var count = GetLong(row, response.Columns, "count");
            var sampleUri = GetString(row, response.Columns, "sample_uri");
            var percentage = totalNodes > 0 ? (double)count / totalNodes * 100 : 0;

            results.Add(new NodeKindStat(
                Kind: kind,
                Count: count,
                Percentage: percentage,
                SampleUri: sampleUri));
        }

        return results;
    }

    public async Task<IReadOnlyList<EdgeTypeStat>> GetEdgeTypeStatsAsync(CancellationToken cancellationToken = default)
    {
        var client = await _connectionManager.GetClientAsync(cancellationToken).ConfigureAwait(false);

        const string sql = """
            SELECT
              type,
              COUNT(*) as count
            FROM edge
            GROUP BY type
            ORDER BY count DESC
            """;

        var response = await client.ExecuteRawQueryAsync(sql, cancellationToken: cancellationToken).ConfigureAwait(false);
        var results = new List<EdgeTypeStat>(response.Rows.Count);

        long totalEdges = 0;
        foreach (var row in response.Rows)
        {
            totalEdges += GetLong(row, response.Columns, "count");
        }

        foreach (var row in response.Rows)
        {
            var type = GetString(row, response.Columns, "type");
            var count = GetLong(row, response.Columns, "count");
            var percentage = totalEdges > 0 ? (double)count / totalEdges * 100 : 0;

            results.Add(new EdgeTypeStat(
                Type: type,
                Count: count,
                Percentage: percentage));
        }

        return results;
    }

    public async Task<IReadOnlyList<AnnotationStat>> GetAnnotationStatsAsync(CancellationToken cancellationToken = default)
    {
        var client = await _connectionManager.GetClientAsync(cancellationToken).ConfigureAwait(false);

        const string sql = """
            SELECT
              rule_id,
              severity,
              COUNT(*) as count,
              COUNT(DISTINCT target_uri) as file_count
            FROM annotation
            GROUP BY rule_id, severity
            ORDER BY severity DESC, count DESC
            """;

        var response = await client.ExecuteRawQueryAsync(sql, cancellationToken: cancellationToken).ConfigureAwait(false);
        var results = new List<AnnotationStat>(response.Rows.Count);

        foreach (var row in response.Rows)
        {
            results.Add(new AnnotationStat(
                RuleId: GetString(row, response.Columns, "rule_id"),
                Severity: GetString(row, response.Columns, "severity"),
                Count: GetLong(row, response.Columns, "count"),
                FileCount: GetLong(row, response.Columns, "file_count")));
        }

        return results;
    }

    public async Task<HealthCheckResults> GetHealthChecksAsync(CancellationToken cancellationToken = default)
    {
        var client = await _connectionManager.GetClientAsync(cancellationToken).ConfigureAwait(false);

        var checks = new List<HealthCheck>();

        // Files missing headline
        var missingHeadlineResult = await client.ExecuteRawQueryAsync(
            "SELECT COUNT(*) as count FROM artifact WHERE headline IS NULL",
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var missingHeadlineCount = missingHeadlineResult.Rows.Count > 0
            ? GetLong(missingHeadlineResult.Rows[0], missingHeadlineResult.Columns, "count")
            : 0;

        if (missingHeadlineCount > 0)
        {
            var samplesResult = await client.ExecuteRawQueryAsync(
                "SELECT storage_uri FROM artifact WHERE headline IS NULL LIMIT 3",
                cancellationToken: cancellationToken).ConfigureAwait(false);
            var samples = samplesResult.Rows
                .Select(r => GetString(r, samplesResult.Columns, "storage_uri"))
                .ToArray();

            checks.Add(new HealthCheck(
                Severity: HealthCheckSeverity.Error,
                Message: $"{missingHeadlineCount} file(s) missing headline",
                Count: missingHeadlineCount,
                Samples: samples));
        }
        else
        {
            checks.Add(new HealthCheck(
                Severity: HealthCheckSeverity.Success,
                Message: "All files have headline",
                Count: 0,
                Samples: Array.Empty<string>()));
        }

        // Files missing summary
        var missingSummaryResult = await client.ExecuteRawQueryAsync(
            "SELECT COUNT(*) as count FROM artifact WHERE summary IS NULL",
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var missingSummaryCount = missingSummaryResult.Rows.Count > 0
            ? GetLong(missingSummaryResult.Rows[0], missingSummaryResult.Columns, "count")
            : 0;

        if (missingSummaryCount > 0)
        {
            var samplesResult = await client.ExecuteRawQueryAsync(
                "SELECT storage_uri FROM artifact WHERE summary IS NULL LIMIT 3",
                cancellationToken: cancellationToken).ConfigureAwait(false);
            var samples = samplesResult.Rows
                .Select(r => GetString(r, samplesResult.Columns, "storage_uri"))
                .ToArray();

            checks.Add(new HealthCheck(
                Severity: HealthCheckSeverity.Warning,
                Message: $"{missingSummaryCount} file(s) missing summary",
                Count: missingSummaryCount,
                Samples: samples));
        }
        else
        {
            checks.Add(new HealthCheck(
                Severity: HealthCheckSeverity.Success,
                Message: "All files have summary",
                Count: 0,
                Samples: Array.Empty<string>()));
        }

        // Nodes without spans
        var noSpansResult = await client.ExecuteRawQueryAsync(
            "SELECT COUNT(*) as count FROM node n WHERE n.span_id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM span s WHERE s.id = n.span_id)",
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var noSpansCount = noSpansResult.Rows.Count > 0
            ? GetLong(noSpansResult.Rows[0], noSpansResult.Columns, "count")
            : 0;

        if (noSpansCount > 0)
        {
            var samplesResult = await client.ExecuteRawQueryAsync(
                "SELECT n.uri FROM node n WHERE n.span_id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM span s WHERE s.id = n.span_id) LIMIT 3",
                cancellationToken: cancellationToken).ConfigureAwait(false);
            var samples = samplesResult.Rows
                .Select(r => GetString(r, samplesResult.Columns, "uri"))
                .ToArray();

            checks.Add(new HealthCheck(
                Severity: HealthCheckSeverity.Warning,
                Message: $"{noSpansCount} node(s) without spans",
                Count: noSpansCount,
                Samples: samples));
        }

        // Broken edges (NULL destination_node_id)
        var brokenEdgesResult = await client.ExecuteRawQueryAsync(
            "SELECT COUNT(*) as count FROM edge WHERE destination_node_id IS NULL",
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var brokenEdgesCount = brokenEdgesResult.Rows.Count > 0
            ? GetLong(brokenEdgesResult.Rows[0], brokenEdgesResult.Columns, "count")
            : 0;

        if (brokenEdgesCount > 0)
        {
            var samplesResult = await client.ExecuteRawQueryAsync(
                "SELECT e.id, e.type FROM edge e WHERE e.destination_node_id IS NULL LIMIT 3",
                cancellationToken: cancellationToken).ConfigureAwait(false);
            var samples = samplesResult.Rows
                .Select(r => $"{GetString(r, samplesResult.Columns, "type")} (id: {GetLong(r, samplesResult.Columns, "id")})")
                .ToArray();

            checks.Add(new HealthCheck(
                Severity: HealthCheckSeverity.Warning,
                Message: $"{brokenEdgesCount} broken edge(s) (NULL destination)",
                Count: brokenEdgesCount,
                Samples: samples));
        }

        return new HealthCheckResults(checks);
    }

    private static string GetString(RowData row, IList<ColumnSchema> columns, string name)
    {
        var idx = FindColumnIndex(columns, name);
        if (idx < 0 || idx >= row.Values.Count)
            return string.Empty;
        return row.Values[idx].StringValue ?? row.Values[idx].ToString() ?? string.Empty;
    }

    private static long GetLong(RowData row, IList<ColumnSchema> columns, string name)
    {
        var idx = FindColumnIndex(columns, name);
        if (idx < 0 || idx >= row.Values.Count)
            return 0;
        var value = row.Values[idx];
        return value.KindCase switch
        {
            Value.KindOneofCase.NumberValue => Convert.ToInt64(value.NumberValue, CultureInfo.InvariantCulture),
            Value.KindOneofCase.StringValue when long.TryParse(value.StringValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => 0
        };
    }

    private static double GetDouble(RowData row, IList<ColumnSchema> columns, string name)
    {
        var idx = FindColumnIndex(columns, name);
        if (idx < 0 || idx >= row.Values.Count)
            return 0;
        var value = row.Values[idx];
        return value.KindCase switch
        {
            Value.KindOneofCase.NumberValue => value.NumberValue,
            Value.KindOneofCase.StringValue when double.TryParse(value.StringValue, out var parsed) => parsed,
            _ => 0
        };
    }

    private static int FindColumnIndex(IList<ColumnSchema> columns, string name)
    {
        for (var i = 0; i < columns.Count; i++)
        {
            if (string.Equals(columns[i].Name, name, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return -1;
    }
}