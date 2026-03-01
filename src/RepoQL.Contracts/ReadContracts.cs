namespace RepoQL.Contracts;

/// <summary>
/// Document data for read operations.
/// </summary>
public sealed record ReadDocument(
    string Uri,
    string? TextContent,
    string? MediaType,
    string? Headline,
    string? Summary,
    string? Structure);

/// <summary>
/// Token costs for different representation levels of a document.
/// Used to inform users what budget is needed for higher-fidelity representations.
/// </summary>
public sealed record RepresentationCosts(
    int? FullTokens,       // Cost for full content (null if not available)
    int? StructureTokens,  // Cost for headline + structure (null if not available)
    int? HeadlineTokens    // Cost for headline only (null if not available)
);

/// <summary>
/// Interface for fetching document content for read operations.
/// </summary>
public interface IReadContentProvider
{
    /// <summary>
    /// Fetch documents matching a URI pattern. Uses matches_glob internally, so handles:
    /// - Exact URIs: file:///path/file.cs
    /// - Glob patterns: file:///path/**/*.cs
    /// - Fragment patterns: file:///path/file.cs#symbol=Method
    /// </summary>
    Task<IReadOnlyList<ReadDocument>> FetchGlobAsync(string uriPattern, CancellationToken cancellationToken);

    /// <summary>
    /// Get ASCII tree of repository structure for a scope, fitted to a token budget.
    /// Uses progressive fallback: headlines → files → folders → null.
    /// </summary>
    /// <param name="scope">Optional scope glob pattern (e.g., "file:///src/**"). Null for full repo.</param>
    /// <param name="tokenBudget">Maximum tokens for the tree output.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<string?> GetRepoTreeAsync(string? scope, int tokenBudget, CancellationToken cancellationToken) => Task.FromResult<string?>(null);

    /// <summary>
    /// Format a list of URIs as an ASCII tree. Returns null if not supported.
    /// </summary>
    /// <param name="uris">List of URIs to format.</param>
    /// <param name="foldersOnly">If true, shows only folders with file type counts.</param>
    /// <param name="includeHeadlines">If true, supplies headlines so tree can append them to file nodes.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<string?> FormatAsTreeAsync(IReadOnlyList<string> uris, bool foldersOnly, bool includeHeadlines, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
}
