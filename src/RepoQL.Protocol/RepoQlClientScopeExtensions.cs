using System.Text;
using Google.Protobuf.WellKnownTypes;
using RepoQL.Contracts;

namespace RepoQL.Protocol;

/// <summary>
/// Extension methods for checking and waiting on scope readiness.
///
/// Purpose: Provides reusable methods for tools (ExploreTool, ReadTool, etc.)
/// to check whether files in a scope are ready for semantic search and optionally
/// wait for them to become ready.
///
/// Complexity: Queries the _scope_readiness_internal UDF via raw SQL and parses
/// the protobuf Value results. WaitForScopeAsync polls with exponential backoff.
/// </summary>
public static class RepoQlClientScopeExtensions
{
    /// <summary>
    /// Get scope readiness status for a glob pattern.
    /// </summary>
    /// <param name="client">The RepoQL client.</param>
    /// <param name="scope">Glob pattern to check (e.g., "file:///src/**"). Null checks all files.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Scope readiness information.</returns>
    public static async Task<ScopeReadinessInfo> GetScopeReadinessAsync(
        this IRepoQlClient client,
        string? scope,
        CancellationToken ct = default)
    {
        try
        {
            var escapedScope = scope?.Replace("'", "''") ?? "";
            var sql = string.IsNullOrEmpty(escapedScope)
                ? "SELECT * FROM _scope_readiness_internal(NULL)"
                : $"SELECT * FROM _scope_readiness_internal('{escapedScope}')";

            var result = await client.ExecuteRawQueryAsync(sql, cancellationToken: ct).ConfigureAwait(false);

            if (result.Rows.Count == 0)
                return ScopeReadinessInfo.Ready;

            var row = result.Rows[0];
            var columns = result.Columns;

            // Build column name to index mapping
            var colIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < columns.Count; i++)
                colIndex[columns[i].Name] = i;

            return new ScopeReadinessInfo(
                IsReady: GetBool(row, colIndex, "is_ready"),
                TotalFiles: GetInt(row, colIndex, "total_files"),
                IndexedCount: GetInt(row, colIndex, "indexed_count"),
                EmbeddedCount: GetInt(row, colIndex, "embedded_count"),
                PendingIndex: GetInt(row, colIndex, "pending_index"),
                PendingEmbedding: GetInt(row, colIndex, "pending_embedding"),
                FailedCount: GetInt(row, colIndex, "failed_count"),
                ReadyPercent: GetInt(row, colIndex, "ready_percent"),
                Summary: GetString(row, colIndex, "summary") ?? "");
        }
        catch
        {
            // On any error, assume ready to avoid blocking
            return ScopeReadinessInfo.Ready;
        }
    }

    /// <summary>
    /// Wait for a scope to become ready for semantic search.
    /// Polls with exponential backoff until all files in scope have structure embeddings.
    /// </summary>
    /// <param name="client">The RepoQL client.</param>
    /// <param name="scope">Glob pattern to wait on (e.g., "file:///src/**"). Null waits for all files.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Final scope readiness information when ready.</returns>
    public static async Task<ScopeReadinessInfo> WaitForScopeAsync(
        this IRepoQlClient client,
        string? scope,
        CancellationToken ct = default)
    {
        var delay = TimeSpan.FromMilliseconds(200);
        var maxDelay = TimeSpan.FromSeconds(2);

        while (!ct.IsCancellationRequested)
        {
            var status = await client.GetScopeReadinessAsync(scope, ct).ConfigureAwait(false);
            if (status.IsReady)
                return status;

            await Task.Delay(delay, ct).ConfigureAwait(false);
            delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, maxDelay.Ticks));
        }

        ct.ThrowIfCancellationRequested();
        return ScopeReadinessInfo.Ready; // unreachable
    }

    /// <summary>
    /// Format a user-friendly message when scope is not ready.
    /// </summary>
    /// <param name="status">The scope readiness status.</param>
    /// <param name="scope">The scope pattern that was checked.</param>
    /// <returns>Formatted message for display to user.</returns>
    public static string FormatScopeNotReadyMessage(ScopeReadinessInfo status, string? scope)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Scope not ready for semantic search");
        sb.AppendLine();
        sb.AppendLine($"Pattern: {scope ?? "(all files)"}");
        sb.AppendLine($"Progress: {status.EmbeddedCount}/{status.TotalFiles} files ready ({status.ReadyPercent}%)");

        if (status.PendingIndex > 0)
            sb.AppendLine($"  - {status.PendingIndex} pending index");
        if (status.PendingEmbedding > 0)
            sb.AppendLine($"  - {status.PendingEmbedding} pending embedding");
        if (status.FailedCount > 0)
            sb.AppendLine($"  - {status.FailedCount} failed");

        sb.AppendLine();
        sb.AppendLine("Structure embeddings are being generated. Call again with the same arguments to wait.");

        return sb.ToString();
    }

    #region Value Extraction Helpers

    private static bool GetBool(RowData row, Dictionary<string, int> colIndex, string columnName)
    {
        if (!colIndex.TryGetValue(columnName, out var idx) || idx >= row.Values.Count)
            return false;

        var value = row.Values[idx];
        return value.KindCase switch
        {
            Value.KindOneofCase.BoolValue => value.BoolValue,
            Value.KindOneofCase.StringValue => value.StringValue.Equals("true", StringComparison.OrdinalIgnoreCase),
            Value.KindOneofCase.NumberValue => value.NumberValue != 0,
            _ => false
        };
    }

    private static int GetInt(RowData row, Dictionary<string, int> colIndex, string columnName)
    {
        if (!colIndex.TryGetValue(columnName, out var idx) || idx >= row.Values.Count)
            return 0;

        var value = row.Values[idx];
        return value.KindCase switch
        {
            Value.KindOneofCase.NumberValue => (int)value.NumberValue,
            Value.KindOneofCase.StringValue => int.TryParse(value.StringValue, out var i) ? i : 0,
            _ => 0
        };
    }

    private static string? GetString(RowData row, Dictionary<string, int> colIndex, string columnName)
    {
        if (!colIndex.TryGetValue(columnName, out var idx) || idx >= row.Values.Count)
            return null;

        var value = row.Values[idx];
        return value.KindCase switch
        {
            Value.KindOneofCase.StringValue => value.StringValue,
            Value.KindOneofCase.NumberValue => value.NumberValue.ToString(),
            Value.KindOneofCase.BoolValue => value.BoolValue ? "true" : "false",
            _ => null
        };
    }

    #endregion
}
