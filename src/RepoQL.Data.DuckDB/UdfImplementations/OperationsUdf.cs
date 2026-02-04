using RepoQL.Contracts;
using RepoQL.Data.DuckDB.UdfFramework;

namespace RepoQL.Data.DuckDB.UdfImplementations;

/// <summary>
/// UDFs for querying operations from SQL.
///
/// Purpose: Expose operation status and logs to SQL queries for observability,
/// enabling agents and dashboards to track indexing progress.
///
/// Complexity: Delegates to IOperationManager. Returns empty results if manager
/// is not available (graceful degradation).
/// </summary>
[UdfClass]
public class OperationsUdf
{
    private readonly IOperationManager? _operationManager;

    public OperationsUdf(IOperationManager? operationManager = null)
    {
        _operationManager = operationManager;
    }

    /// <summary>
    /// Returns all operations (active and completed).
    /// </summary>
    [StructuredUdf("_operations_internal", Description = "Returns all operations")]
    public IEnumerable<OperationRow> Operations([UdfDefault("''")] string? _dummy)
    {
        if (_operationManager is null)
            yield break;

        foreach (var op in _operationManager.Operations)
        {
            yield return new OperationRow(
                op.Id,
                op.Description,
                op.State.ToString(),
                op.Progress.TotalFiles,
                op.Progress.IndexedCount,
                op.Progress.EmbeddedCount,
                op.Progress.FailedCount,
                op.Progress.ReadyPercent,
                op.CreatedAt.ToString("O"),
                op.CompletedAt?.ToString("O")
            );
        }
    }

    /// <summary>
    /// Returns a single operation by ID.
    /// </summary>
    [StructuredUdf("_operation_internal", Description = "Returns a single operation by ID")]
    public IEnumerable<OperationRow> Operation(string? id)
    {
        if (_operationManager is null || string.IsNullOrWhiteSpace(id))
            yield break;

        var op = _operationManager.GetOperation(id);
        if (op is null)
            yield break;

        yield return new OperationRow(
            op.Id,
            op.Description,
            op.State.ToString(),
            op.Progress.TotalFiles,
            op.Progress.IndexedCount,
            op.Progress.EmbeddedCount,
            op.Progress.FailedCount,
            op.Progress.ReadyPercent,
            op.CreatedAt.ToString("O"),
            op.CompletedAt?.ToString("O")
        );
    }

    /// <summary>
    /// Returns log entries for an operation.
    /// </summary>
    [StructuredUdf("_operation_log_internal", Description = "Returns log entries for an operation")]
    public IEnumerable<OperationLogRow> OperationLog(string? id)
    {
        if (_operationManager is null || string.IsNullOrWhiteSpace(id))
            yield break;

        var op = _operationManager.GetOperation(id);
        if (op is null)
            yield break;

        foreach (var entry in op.Log.OrderBy(e => e.Timestamp))
        {
            yield return new OperationLogRow(
                entry.Timestamp.ToString("O"),
                entry.Type,
                entry.Message,
                entry.Uri?.AbsoluteUri
            );
        }
    }

    public record OperationRow(
        string Id,
        string Description,
        string State,
        int TotalFiles,
        int IndexedCount,
        int EmbeddedCount,
        int FailedCount,
        int ReadyPercent,
        string CreatedAt,
        string? CompletedAt);

    public record OperationLogRow(
        string Timestamp,
        string Type,
        string? Message,
        string? Uri);
}
