namespace RepoQL.ConsoleApp.Host;

/// <summary>
/// Provides intelligent query barriers that wait for appropriate indexing stages
/// based on query characteristics.
/// </summary>
/// <remarks>
/// <para>
/// The barrier protects against partial reads by ensuring queries only execute
/// after relevant indexing stages complete:
/// </para>
/// <list type="bullet">
/// <item><description>All queries wait for hot path (Parsing + SingleFileAnalysis)</description></item>
/// <item><description>Semantic queries additionally wait for vector refresh (MultiFileAnalysis)</description></item>
/// </list>
/// </remarks>
public interface IQueryBarrier
{
    /// <summary>
    /// Waits for the appropriate indexing stages to complete based on query characteristics.
    /// </summary>
    /// <param name="sql">The SQL query to analyze for semantic search usage.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the query is safe to execute.</returns>
    Task WaitForQueryReadyAsync(string sql, CancellationToken cancellationToken);
}
