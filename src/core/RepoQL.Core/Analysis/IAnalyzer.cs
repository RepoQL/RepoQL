using RepoQL.Contracts;
using RepoQL.Contracts.Analysis;
using RepoQL.Contracts.Models;

namespace RepoQL.Core.Analysis;

/// <summary>
///     Contract for post-parse repository analyzers. Implementations examine persisted
///     graph data and emit analysis results (annotations, suggested fixes, etc.).
/// </summary>
public interface IAnalyzer
{
    /// <summary>
    ///     Returns true when this analyzer should run for the specified document artifact.
    /// </summary>
    /// <param name="media">Resolved semantic media descriptor for the document.</param>
    /// <param name="documentNode">The persisted document node.</param>
    bool Supports(SemanticMediaType media, Node documentNode);

    /// <summary>
    ///     Executes analysis for the provided document container URI. Implementations may
    ///     inspect the RepoQL database for additional context.
    /// </summary>
    /// <param name="containerUri">RepoQL container URI (no fragment) of the document.</param>
    /// <param name="context">Ambient analyzer context (helpers, config, services).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    IAsyncEnumerable<AnalysisResult> AnalyzeAsync(
        string containerUri,
        AnalyzerContext context,
        CancellationToken cancellationToken = default);
}
