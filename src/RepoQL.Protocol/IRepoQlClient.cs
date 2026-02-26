using Grpc.Net.Client;
using RepoQL.Contracts;
using PipelineStage = RepoQL.Contracts.PipelineStage;
using PipelineStatus = RepoQL.Contracts.PipelineStatus;

namespace RepoQL.Protocol;

/// <summary>
/// Typed client for the RepoQL gRPC service using a Unix domain socket transport.
/// </summary>
/// <remarks>
/// - Socket discovery mirrors the server: if ".repoql/socket.path" exists it is used; otherwise ".repoql/repoql.sock".
/// - All raw SQL parameters are positional and bound in order.
/// - Unary raw queries can be limited; the response sets <c>truncated</c> when results were cut by the limit.
/// </remarks>
public interface IRepoQlClient : IAsyncDisposable
{
    /// <summary>Underlying gRPC channel.</summary>
    GrpcChannel Channel { get; }

    /// <summary>
    /// Execute a raw SQL statement and return a tabular result in-memory.
    /// </summary>
    /// <param name="sql">SQL text. May reference positional parameters (e.g., '?').</param>
    /// <param name="parameters">Values to bind positionally to parameters in the SQL (0..n-1).</param>
    /// <param name="rowLimit">Optional maximum number of rows to return. <c>null</c> or 0 means no limit.</param>
    /// <param name="tokenBudget">Optional token budget. If exceeded and SQL contains a comment, server may LLM-summarize. 0 = unlimited.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<RawQueryResponse> ExecuteRawQueryAsync(
        string sql,
        IEnumerable<object?>? parameters = null,
        int? rowLimit = null,
        int tokenBudget = 0,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Execute a raw SQL statement and stream the result rows.
    /// </summary>
    /// <param name="sql">SQL text. May reference positional parameters (e.g., '?').</param>
    /// <param name="parameters">Values to bind positionally to parameters in the SQL (0..n-1).</param>
    /// <param name="rowLimit">Optional maximum number of rows to stream. <c>null</c> or 0 means no limit.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    IAsyncEnumerable<RawQueryRow> ExecuteRawQueryStreamAsync(
        string sql,
        IEnumerable<object?>? parameters = null,
        int? rowLimit = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetch document summaries for one or more repository URIs using annotation-backed summaries (e.g., 'outline').
    /// </summary>
    /// <param name="documentUris">One or more absolute repository URIs (container URIs) to summarize.</param>
    /// <param name="annotationKinds">Annotation kinds to include (case-insensitive). Defaults to ["outline"].</param>
    /// <param name="minimumSeverity">Minimum severity to include (hint|info|warning|error). <c>null</c> uses server default.</param>
    /// <param name="includeData">Whether to include structured <c>annotation.data</c> payloads.</param>
    /// <param name="includeMessage">Whether to include human-readable <c>annotation.message</c>.</param>
    /// <param name="includeResolvedTargetUri">Whether to include <c>resolved_target_uri</c> (when available).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<GetDocumentSummariesResponse> GetDocumentSummariesAsync(
        IEnumerable<string> documentUris,
        IEnumerable<string>? annotationKinds = null,
        string? minimumSeverity = null,
        bool includeData = false,
        bool includeMessage = true,
        bool includeResolvedTargetUri = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Wait for one or more pipeline stages to become idle and return the resulting status snapshot.
    /// </summary>
    /// <param name="stages">Stages to wait on. When <c>null</c> or empty, defaults to discovery, indexing, and analysis.</param>
    /// <param name="waitAll">When true, waits for all specified stages; otherwise waits for any one to become idle.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<PipelineStatus> WaitForPipelineAsync(
        IEnumerable<PipelineStage>? stages = null,
        bool waitAll = true,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Import an external source (e.g., github://owner/repo or sarif:///path/to/file.sarif).
    /// VFS imports may return immediately with an operation identifier for async progress tracking.
    /// </summary>
    /// <param name="uri">Repository URI understood by an importer.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Import result including progress and failure counts.</returns>
    Task<ImportResult> ImportRepositoryAsync(
        string uri,
        bool analyze = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieve the current pipeline status without blocking.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<PipelineStatus> GetPipelineStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs the hot-path pipeline for a single document without persisting results.
    /// </summary>
    /// <param name="uri">Repository URI (e.g., file:///docs/readme.md).</param>
    /// <param name="content">Optional file content to preview instead of reading from disk.</param>
    /// <param name="fileName">Optional file name override when uploading content.</param>
    /// <param name="mediaTypeHint">Optional semantic media type hint.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<PreviewDocumentResponse> PreviewDocumentAsync(
        string uri,
        byte[]? content = null,
        string? fileName = null,
        string? mediaTypeHint = null,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<ReindexProgress> ReindexAllAsync(bool clear = false, string? scope = null, TimeSpan? timeout = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Execute an explore query and return both structured results and pre-rendered output.
    /// </summary>
    /// <param name="tokenBudget">Maximum tokens to invest in the response.</param>
    /// <param name="intent">Search intent (zoom level).</param>
    /// <param name="scope">Optional scope filter (glob pattern or URI).</param>
    /// <param name="keywords">Optional search keywords for semantic search.</param>
    /// <param name="boost">Optional comma-separated regex patterns to boost matches.</param>
    /// <param name="penalize">Optional comma-separated regex patterns to de-rank matches.</param>
    /// <param name="limit">Optional max results to show (null = auto-calculate).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<ExploreResponse> ExploreAsync(
        int tokenBudget,
        ExploreIntent intent,
        string? scope = null,
        string? keywords = null,
        string? boost = null,
        string? penalize = null,
        int? limit = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Read repository content with token-budget-aware representation selection.
    /// </summary>
    /// <param name="uri">URI or glob pattern. Append ' // question' for LLM synthesis.</param>
    /// <param name="tokenBudget">Token budget - determines representation depth (full/structure/headline).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<ReadResponse> ReadAsync(
        string uri,
        int tokenBudget,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Stream real-time status events from the server. Replaces polling for live dashboard updates.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token to stop the stream.</param>
    IAsyncEnumerable<StatusEvent> WatchStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Request the host process to shut down gracefully.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The process ID of the host that was shut down.</returns>
    Task<int> ShutdownHostAsync(CancellationToken cancellationToken = default);
}
