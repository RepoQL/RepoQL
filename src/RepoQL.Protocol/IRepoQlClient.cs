using Grpc.Net.Client;

namespace RepoQL.Contracts;

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
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<RawQueryResponse> ExecuteRawQueryAsync(
        string sql,
        IEnumerable<object?>? parameters = null,
        int? rowLimit = null,
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
}
