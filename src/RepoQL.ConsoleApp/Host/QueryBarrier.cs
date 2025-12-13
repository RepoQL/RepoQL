using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RepoQL.Indexing.Hosting;

namespace RepoQL.ConsoleApp.Host;

/// <summary>
/// Provides intelligent query barriers that wait for appropriate indexing stages
/// based on query characteristics.
/// </summary>
/// <remarks>
/// <para><strong>Two-Tier Protection</strong></para>
/// <para>
/// All queries wait for the hot path to complete (Parsing + SingleFileAnalysis).
/// This ensures basic structural data (nodes, edges, spans) is consistent.
/// </para>
/// <para>
/// Semantic queries (those using file_search, object_search, search(), related(),
/// or document_embedding) additionally wait for vector refresh to complete.
/// This ensures embedding data is available for similarity scoring.
/// </para>
/// <para><strong>Detection Heuristics</strong></para>
/// <para>
/// SQL is analyzed for semantic search indicators using case-insensitive pattern matching.
/// The detection is intentionally broad to avoid false negatives that would cause
/// queries to return incomplete semantic results.
/// </para>
/// </remarks>
public sealed partial class QueryBarrier : IQueryBarrier
{
    private readonly IIndexingCoordinator _coordinator;
    private readonly IInitialIndexingBarrier _initialBarrier;
    private readonly ILogger<QueryBarrier> _logger;

    // Patterns that indicate semantic search usage
    private static readonly string[] SemanticIndicators =
    [
        "file_search",
        "object_search",
        "search(",
        "related(",
        "document_embedding",
        "embed_text",
        "cosine_similarity"
    ];

    public QueryBarrier(
        IIndexingCoordinator coordinator,
        IInitialIndexingBarrier initialBarrier,
        ILogger<QueryBarrier>? logger = null)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _initialBarrier = initialBarrier ?? throw new ArgumentNullException(nameof(initialBarrier));
        _logger = logger ?? NullLogger<QueryBarrier>.Instance;
    }

    public async Task WaitForQueryReadyAsync(string sql, CancellationToken cancellationToken)
    {
        // Always wait for initial scan first
        await _initialBarrier.InitialScanCompleted.WaitAsync(cancellationToken).ConfigureAwait(false);

        // Check current pipeline status
        var status = _coordinator.GetPipelineStatus();
        var anyBusy = status.Stages.Any(s => s.Busy || s.InProgress > 0);

        if (!anyBusy && !status.WriterPending)
        {
            // Nothing in progress, safe to proceed immediately
            return;
        }

        var usesSemantic = IsSemanticQuery(sql);

        if (usesSemantic)
        {
            LogWaitingForSemanticReady(_logger, sql.Length);
            // Semantic queries need vector refresh to complete (happens before MultiFileAnalysis)
            await _coordinator.WaitForPipelineAsync(
                [CoordinatorPipelineStage.Discovery, CoordinatorPipelineStage.Parsing, CoordinatorPipelineStage.Analysis],
                waitAll: true,
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            LogWaitingForHotPathReady(_logger, sql.Length);
            // Non-semantic queries only need hot path (parsing + single-file analysis)
            await _coordinator.WaitForPipelineAsync(
                [CoordinatorPipelineStage.Discovery, CoordinatorPipelineStage.Parsing],
                waitAll: true,
                cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Determines if the SQL query uses semantic search features.
    /// </summary>
    internal static bool IsSemanticQuery(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
            return false;

        var sqlLower = sql.ToLowerInvariant();

        foreach (var indicator in SemanticIndicators)
        {
            if (sqlLower.Contains(indicator, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    [LoggerMessage(LogLevel.Debug, "Waiting for hot path to complete before executing query (sql length: {SqlLength})")]
    private static partial void LogWaitingForHotPathReady(ILogger logger, int sqlLength);

    [LoggerMessage(LogLevel.Debug, "Waiting for semantic index to be ready before executing query (sql length: {SqlLength})")]
    private static partial void LogWaitingForSemanticReady(ILogger logger, int sqlLength);
}
