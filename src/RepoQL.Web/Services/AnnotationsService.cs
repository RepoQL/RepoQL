namespace RepoQL.Web.Services;

/// <summary>
/// Service for browsing repository-wide annotations (errors, warnings, diagnostics).
/// Supports filtering by severity, rule, and file pattern, with grouping options.
///
/// <para><b>Purpose:</b> Enable developers to see all problems across the codebase
/// in one place, filter to what matters, and click to jump to the source.</para>
///
/// <para><b>Complexity:</b> Builds dynamic SQL queries based on filters.
/// Supports grouped results (by file or by rule) via SQL aggregation.</para>
/// </summary>
internal sealed class AnnotationsService
{
    private readonly RepoQlConnectionManager _connectionManager;
    private readonly ILogger<AnnotationsService> _logger;

    public AnnotationsService(RepoQlConnectionManager connectionManager, ILogger<AnnotationsService> logger)
    {
        _connectionManager = connectionManager;
        _logger = logger;
    }

    /// <summary>
    /// Get summary counts by severity.
    /// </summary>
    public async Task<AnnotationCounts> GetSummaryAsync(string? filePattern = null, CancellationToken ct = default)
    {
        var client = await _connectionManager.GetClientAsync(ct).ConfigureAwait(false);

        try
        {
            var whereClause = BuildWhereClause(null, null, filePattern);

            var sql = $@"
                SELECT
                    severity,
                    COUNT(*) as count
                FROM Annotations
                {whereClause}
                GROUP BY severity";

            var result = await client.ExecuteRawQueryAsync(sql, rowLimit: 10, cancellationToken: ct).ConfigureAwait(false);

            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in result.Rows)
            {
                var severity = GetString(row, 0);
                var count = GetInt(row, 1);
                if (!string.IsNullOrEmpty(severity))
                {
                    counts[severity] = count;
                }
            }

            return new AnnotationCounts(
                ErrorCount: counts.GetValueOrDefault("error"),
                WarningCount: counts.GetValueOrDefault("warning"),
                InfoCount: counts.GetValueOrDefault("info"),
                HintCount: counts.GetValueOrDefault("hint"));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to get annotation summary");
            return new AnnotationCounts(0, 0, 0, 0);
        }
    }

    /// <summary>
    /// Get list of all rule IDs in the repository.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetRuleIdsAsync(CancellationToken ct = default)
    {
        var client = await _connectionManager.GetClientAsync(ct).ConfigureAwait(false);

        try
        {
            var sql = @"
                SELECT DISTINCT rule_id
                FROM Annotations
                WHERE rule_id IS NOT NULL AND rule_id != ''
                ORDER BY rule_id
                LIMIT 100";

            var result = await client.ExecuteRawQueryAsync(sql, rowLimit: 100, cancellationToken: ct).ConfigureAwait(false);

            return result.Rows.Select(row => GetString(row, 0)).Where(r => !string.IsNullOrEmpty(r)).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to get rule IDs");
            return [];
        }
    }

    /// <summary>
    /// Get annotations with filtering and pagination.
    /// </summary>
    public async Task<AnnotationsResult> GetAnnotationsAsync(
        AnnotationFilters filters,
        int limit = 100,
        int offset = 0,
        CancellationToken ct = default)
    {
        var client = await _connectionManager.GetClientAsync(ct).ConfigureAwait(false);

        try
        {
            var whereClause = BuildWhereClause(filters.Severity, filters.RuleId, filters.FilePattern);

            // Get total count
            var countSql = $"SELECT COUNT(*) FROM Annotations {whereClause}";
            var countResult = await client.ExecuteRawQueryAsync(countSql, rowLimit: 1, cancellationToken: ct).ConfigureAwait(false);
            var totalCount = countResult.Rows.Count > 0 ? GetInt(countResult.Rows[0], 0) : 0;

            // Get annotations with span info for line numbers
            var sql = $@"
                SELECT
                    a.resolved_target_uri,
                    a.severity,
                    a.rule_id,
                    a.message,
                    s.start_line
                FROM Annotations a
                LEFT JOIN span s ON a.target_span_id = s.id
                {whereClause}
                ORDER BY a.severity_rank, a.resolved_target_uri, s.start_line
                LIMIT {limit} OFFSET {offset}";

            var result = await client.ExecuteRawQueryAsync(sql, rowLimit: limit, cancellationToken: ct).ConfigureAwait(false);

            var annotations = result.Rows.Select(row => new AnnotationItem(
                Uri: GetString(row, 0),
                Severity: GetString(row, 1),
                RuleId: GetString(row, 2),
                Message: GetString(row, 3),
                Line: GetIntOrNull(row, 4))).ToList();

            return new AnnotationsResult(
                Annotations: annotations,
                TotalCount: totalCount,
                HasMore: offset + annotations.Count < totalCount);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to get annotations");
            return new AnnotationsResult([], 0, false);
        }
    }

    /// <summary>
    /// Get annotations grouped by file.
    /// </summary>
    public async Task<IReadOnlyList<AnnotationFileGroup>> GetAnnotationsByFileAsync(
        AnnotationFilters filters,
        int limit = 100,
        CancellationToken ct = default)
    {
        var client = await _connectionManager.GetClientAsync(ct).ConfigureAwait(false);

        try
        {
            var whereClause = BuildWhereClause(filters.Severity, filters.RuleId, filters.FilePattern);

            var sql = $@"
                SELECT
                    a.resolved_target_uri,
                    a.severity,
                    a.rule_id,
                    a.message,
                    s.start_line
                FROM Annotations a
                LEFT JOIN span s ON a.target_span_id = s.id
                {whereClause}
                ORDER BY a.resolved_target_uri, a.severity_rank, s.start_line
                LIMIT {limit}";

            var result = await client.ExecuteRawQueryAsync(sql, rowLimit: limit, cancellationToken: ct).ConfigureAwait(false);

            var groups = new Dictionary<string, List<AnnotationItem>>(StringComparer.OrdinalIgnoreCase);

            foreach (var row in result.Rows)
            {
                var uri = GetString(row, 0);
                if (string.IsNullOrEmpty(uri))
                    continue;

                if (!groups.TryGetValue(uri, out var list))
                {
                    list = [];
                    groups[uri] = list;
                }

                list.Add(new AnnotationItem(
                    Uri: uri,
                    Severity: GetString(row, 1),
                    RuleId: GetString(row, 2),
                    Message: GetString(row, 3),
                    Line: GetIntOrNull(row, 4)));
            }

            return groups.Select(g => new AnnotationFileGroup(
                Uri: g.Key,
                Annotations: g.Value,
                Count: g.Value.Count)).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to get annotations by file");
            return [];
        }
    }

    /// <summary>
    /// Get annotations grouped by rule.
    /// </summary>
    public async Task<IReadOnlyList<AnnotationRuleGroup>> GetAnnotationsByRuleAsync(
        AnnotationFilters filters,
        int limit = 100,
        CancellationToken ct = default)
    {
        var client = await _connectionManager.GetClientAsync(ct).ConfigureAwait(false);

        try
        {
            var whereClause = BuildWhereClause(filters.Severity, filters.RuleId, filters.FilePattern);

            // First get rule counts (limit applies to number of rule groups)
            var groupLimit = Math.Min(limit, 50);
            var countSql = $@"
                SELECT rule_id, COUNT(*) as count
                FROM Annotations
                {whereClause}
                GROUP BY rule_id
                ORDER BY count DESC
                LIMIT {groupLimit}";

            var countResult = await client.ExecuteRawQueryAsync(countSql, rowLimit: groupLimit, cancellationToken: ct).ConfigureAwait(false);

            var groups = new List<AnnotationRuleGroup>();

            foreach (var countRow in countResult.Rows)
            {
                var ruleId = GetString(countRow, 0);
                var count = GetInt(countRow, 1);

                if (string.IsNullOrEmpty(ruleId))
                    continue;

                // Get sample annotations for this rule
                var sampleWhereClause = BuildWhereClause(filters.Severity, ruleId, filters.FilePattern);
                var sampleSql = $@"
                    SELECT
                        a.resolved_target_uri,
                        a.severity,
                        a.rule_id,
                        a.message,
                        s.start_line
                    FROM Annotations a
                    LEFT JOIN span s ON a.target_span_id = s.id
                    {sampleWhereClause}
                    ORDER BY a.resolved_target_uri, s.start_line
                    LIMIT 10";

                var sampleResult = await client.ExecuteRawQueryAsync(sampleSql, rowLimit: 10, cancellationToken: ct).ConfigureAwait(false);

                var annotations = sampleResult.Rows.Select(row => new AnnotationItem(
                    Uri: GetString(row, 0),
                    Severity: GetString(row, 1),
                    RuleId: GetString(row, 2),
                    Message: GetString(row, 3),
                    Line: GetIntOrNull(row, 4))).ToList();

                // Get message from first annotation
                var message = annotations.FirstOrDefault()?.Message ?? "";

                groups.Add(new AnnotationRuleGroup(
                    RuleId: ruleId,
                    Message: message,
                    Annotations: annotations,
                    TotalCount: count));
            }

            return groups;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to get annotations by rule");
            return [];
        }
    }

    private static string BuildWhereClause(string? severity, string? ruleId, string? filePattern)
    {
        var conditions = new List<string>();

        if (!string.IsNullOrWhiteSpace(severity))
        {
            conditions.Add($"severity = '{EscapeSql(severity)}'");
        }

        if (!string.IsNullOrWhiteSpace(ruleId))
        {
            conditions.Add($"rule_id = '{EscapeSql(ruleId)}'");
        }

        if (!string.IsNullOrWhiteSpace(filePattern))
        {
            var likePattern = filePattern
                .Replace("**", "%", StringComparison.Ordinal)
                .Replace("*", "%", StringComparison.Ordinal);
            conditions.Add($"resolved_target_uri LIKE '{EscapeSql(likePattern)}'");
        }

        if (conditions.Count == 0)
            return "";

        return "WHERE " + string.Join(" AND ", conditions);
    }

    private static string EscapeSql(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    private static string GetString(Contracts.RowData row, int index)
    {
        if (index >= row.Values.Count) return "";
        var value = row.Values[index];
        return value.KindCase == Google.Protobuf.WellKnownTypes.Value.KindOneofCase.StringValue
            ? value.StringValue
            : "";
    }

    private static int GetInt(Contracts.RowData row, int index)
    {
        if (index >= row.Values.Count) return 0;
        var value = row.Values[index];
        return value.KindCase == Google.Protobuf.WellKnownTypes.Value.KindOneofCase.NumberValue
            ? (int)value.NumberValue
            : 0;
    }

    private static int? GetIntOrNull(Contracts.RowData row, int index)
    {
        if (index >= row.Values.Count) return null;
        var value = row.Values[index];
        return value.KindCase == Google.Protobuf.WellKnownTypes.Value.KindOneofCase.NumberValue
            ? (int)value.NumberValue
            : null;
    }
}

/// <summary>Summary counts by severity.</summary>
internal sealed record AnnotationCounts(
    int ErrorCount,
    int WarningCount,
    int InfoCount,
    int HintCount)
{
    public int Total => ErrorCount + WarningCount + InfoCount + HintCount;
}

/// <summary>Filters for querying annotations.</summary>
internal sealed record AnnotationFilters(
    string? Severity = null,
    string? RuleId = null,
    string? FilePattern = null);

/// <summary>Result of an annotations query.</summary>
internal sealed record AnnotationsResult(
    IReadOnlyList<AnnotationItem> Annotations,
    int TotalCount,
    bool HasMore);

/// <summary>A single annotation item.</summary>
internal sealed record AnnotationItem(
    string Uri,
    string Severity,
    string RuleId,
    string Message,
    int? Line);

/// <summary>Annotations grouped by file.</summary>
internal sealed record AnnotationFileGroup(
    string Uri,
    IReadOnlyList<AnnotationItem> Annotations,
    int Count);

/// <summary>Annotations grouped by rule.</summary>
internal sealed record AnnotationRuleGroup(
    string RuleId,
    string Message,
    IReadOnlyList<AnnotationItem> Annotations,
    int TotalCount);
