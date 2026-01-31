namespace RepoQL.Web.Services;

/// <summary>
/// Service for accessing git-related information: blame, history, hotspots, and related commits.
///
/// <para><b>Purpose:</b> Enable developers to see who changed what and when,
/// find high-churn files, and connect semantic concepts to git history.</para>
///
/// <para><b>Complexity:</b> Wraps git_* SQL functions and views.
/// Handles cases where git isn't available gracefully.</para>
/// </summary>
internal sealed class GitService
{
    private readonly RepoQlConnectionManager _connectionManager;
    private readonly ILogger<GitService> _logger;

    public GitService(RepoQlConnectionManager connectionManager, ILogger<GitService> logger)
    {
        _connectionManager = connectionManager;
        _logger = logger;
    }

    /// <summary>
    /// Get files ranked by change frequency (hotspots).
    /// </summary>
    public async Task<IReadOnlyList<HotspotInfo>> GetHotspotsAsync(int limit = 50, CancellationToken ct = default)
    {
        var client = await _connectionManager.GetClientAsync(ct).ConfigureAwait(false);

        try
        {
            var sql = $@"
                SELECT uri, commits, authors, churn, total_insertions, total_deletions, last_changed
                FROM git_hotspots
                ORDER BY commits DESC
                LIMIT {limit}";

            var result = await client.ExecuteRawQueryAsync(sql, rowLimit: limit, cancellationToken: ct).ConfigureAwait(false);

            return result.Rows.Select(row => new HotspotInfo(
                Uri: GetString(row, 0),
                CommitCount: GetInt(row, 1),
                AuthorCount: GetInt(row, 2),
                Churn: GetInt(row, 3),
                Insertions: GetInt(row, 4),
                Deletions: GetInt(row, 5),
                LastChanged: GetDateTimeOrNull(row, 6))).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to get hotspots");
            return [];
        }
    }

    /// <summary>
    /// Get blame information for a file.
    /// </summary>
    public async Task<IReadOnlyList<BlameLine>> GetBlameAsync(string uri, CancellationToken ct = default)
    {
        var client = await _connectionManager.GetClientAsync(ct).ConfigureAwait(false);

        try
        {
            var sql = $@"
                SELECT line_number, commit_hash, author_name, author_date, message
                FROM git_blame('{EscapeSql(uri)}')
                ORDER BY line_number";

            var result = await client.ExecuteRawQueryAsync(sql, rowLimit: 5000, cancellationToken: ct).ConfigureAwait(false);

            // We need to get line content separately - blame doesn't include it
            var contentLines = await GetFileContentLinesAsync(client, uri, ct).ConfigureAwait(false);

            return result.Rows.Select(row =>
            {
                var lineNumber = GetInt(row, 0);
                var content = lineNumber > 0 && lineNumber <= contentLines.Count
                    ? contentLines[lineNumber - 1]
                    : "";

                return new BlameLine(
                    LineNumber: lineNumber,
                    Content: content,
                    CommitHash: GetString(row, 1),
                    Author: GetString(row, 2),
                    Date: GetDateTimeOrNull(row, 3),
                    Message: GetString(row, 4));
            }).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to get blame for {Uri}", uri);
            return [];
        }
    }

    private async Task<List<string>> GetFileContentLinesAsync(Protocol.IRepoQlClient client, string uri, CancellationToken ct)
    {
        try
        {
            var response = await client.ReadAsync(uri, 50000, ct).ConfigureAwait(false);
            if (response.Success && !string.IsNullOrEmpty(response.RenderedOutput))
            {
                return response.RenderedOutput.Split('\n').ToList();
            }
        }
        catch
        {
            // Ignore - we'll return empty content
        }
        return [];
    }

    /// <summary>
    /// Get commit history for a file.
    /// </summary>
    public async Task<IReadOnlyList<CommitInfo>> GetHistoryAsync(string uri, string? filter = null, int limit = 50, CancellationToken ct = default)
    {
        var client = await _connectionManager.GetClientAsync(ct).ConfigureAwait(false);

        try
        {
            var filterClause = string.IsNullOrWhiteSpace(filter)
                ? ""
                : $"WHERE message ILIKE '%{EscapeSql(filter)}%' OR author_name ILIKE '%{EscapeSql(filter)}%'";

            var sql = $@"
                SELECT hash, author_name, author_date, message, insertions, deletions
                FROM git_file_history('{EscapeSql(uri)}')
                {filterClause}
                ORDER BY author_date DESC
                LIMIT {limit}";

            var result = await client.ExecuteRawQueryAsync(sql, rowLimit: limit, cancellationToken: ct).ConfigureAwait(false);

            return result.Rows.Select(row => new CommitInfo(
                Hash: GetString(row, 0),
                Author: GetString(row, 1),
                Date: GetDateTimeOrNull(row, 2),
                Message: GetString(row, 3),
                Insertions: GetInt(row, 4),
                Deletions: GetInt(row, 5))).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to get history for {Uri}", uri);
            return [];
        }
    }

    /// <summary>
    /// Get commits related to a semantic query.
    /// </summary>
    public async Task<IReadOnlyList<RelatedCommit>> GetRelatedCommitsAsync(string keywords, int limit = 20, CancellationToken ct = default)
    {
        var client = await _connectionManager.GetClientAsync(ct).ConfigureAwait(false);

        try
        {
            var sql = $@"
                SELECT hash, author, date, message, files_changed, related_files
                FROM changes_related_to('{EscapeSql(keywords)}')
                ORDER BY related_files DESC
                LIMIT {limit}";

            var result = await client.ExecuteRawQueryAsync(sql, rowLimit: limit, cancellationToken: ct).ConfigureAwait(false);

            return result.Rows.Select(row => new RelatedCommit(
                Hash: GetString(row, 0),
                Author: GetString(row, 1),
                Date: GetString(row, 2),
                Message: GetString(row, 3),
                FilesChanged: GetInt(row, 4),
                RelatedFiles: GetInt(row, 5))).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to get related commits for {Keywords}", keywords);
            return [];
        }
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

    private static DateTime? GetDateTimeOrNull(Contracts.RowData row, int index)
    {
        if (index >= row.Values.Count) return null;
        var value = row.Values[index];
        if (value.KindCase == Google.Protobuf.WellKnownTypes.Value.KindOneofCase.StringValue)
        {
            if (DateTime.TryParse(value.StringValue, out var dt))
                return dt;
        }
        return null;
    }
}

/// <summary>Information about a file hotspot (high change frequency).</summary>
internal sealed record HotspotInfo(
    string Uri,
    int CommitCount,
    int AuthorCount,
    int Churn,
    int Insertions,
    int Deletions,
    DateTime? LastChanged);

/// <summary>A single line of blame information.</summary>
internal sealed record BlameLine(
    int LineNumber,
    string Content,
    string CommitHash,
    string Author,
    DateTime? Date,
    string Message);

/// <summary>Information about a commit.</summary>
internal sealed record CommitInfo(
    string Hash,
    string Author,
    DateTime? Date,
    string Message,
    int Insertions,
    int Deletions);

/// <summary>A commit related to a semantic query.</summary>
internal sealed record RelatedCommit(
    string Hash,
    string Author,
    string Date,
    string Message,
    int FilesChanged,
    int RelatedFiles);
