using RepoQL.Contracts;

namespace RepoQL.Web.Services;

/// <summary>
/// Service for executing explore queries from the web UI.
/// </summary>
internal sealed class ExploreService
{
    private readonly RepoQlConnectionManager _connectionManager;
    private readonly ILogger<ExploreService> _logger;

    public ExploreService(RepoQlConnectionManager connectionManager, ILogger<ExploreService> logger)
    {
        _connectionManager = connectionManager;
        _logger = logger;
    }

    public async Task<ExploreQueryResult> ExecuteAsync(
        int tokenBudget,
        ExploreIntent intent,
        string? scope,
        string? keywords,
        string? boost,
        string? penalize,
        int? limit,
        CancellationToken cancellationToken = default)
    {
        var client = await _connectionManager.GetClientAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogDebug("Executing explore query (budget={Budget}, intent={Intent})", tokenBudget, intent);

        var response = await client.ExploreAsync(
            tokenBudget,
            intent,
            scope,
            keywords,
            boost,
            penalize,
            limit,
            cancellationToken).ConfigureAwait(false);

        if (!response.Success)
        {
            return new ExploreQueryResult(
                Success: false,
                Error: response.Error,
                RenderedOutput: null,
                Results: [],
                Truncated: false,
                Status: null);
        }

        var results = response.Results.Select(MapResult).ToList();

        return new ExploreQueryResult(
            Success: true,
            Error: null,
            RenderedOutput: response.RenderedOutput,
            Results: results,
            Truncated: response.Truncated,
            Status: new ExploreStatusInfo(
                IndexTotal: response.Status?.IndexTotal ?? 0,
                IndexPending: response.Status?.IndexPending ?? 0,
                IndexFailed: response.Status?.IndexFailed ?? 0,
                IndexStale: response.Status?.IndexStale ?? 0,
                SemanticReady: response.Status?.SemanticReady ?? false,
                SemanticPercent: response.Status?.SemanticPercent ?? 0,
                Ready: response.Status?.Ready ?? false,
                ElapsedMs: response.Status?.ElapsedMs ?? 0));
    }

    private static ExploreResultDto MapResult(ExploreResultItem item)
    {
        var children = item.Children?.Select(MapResult).ToList();

        return new ExploreResultDto(
            Uri: item.Uri,
            Confidence: item.Confidence,
            Kind: string.IsNullOrEmpty(item.Kind) ? null : item.Kind,
            Headline: string.IsNullOrEmpty(item.Headline) ? null : item.Headline,
            Structure: string.IsNullOrEmpty(item.Structure) ? null : item.Structure,
            Snippet: string.IsNullOrEmpty(item.Snippet) ? null : item.Snippet,
            Lang: string.IsNullOrEmpty(item.Lang) ? null : item.Lang,
            SemanticType: string.IsNullOrEmpty(item.SemanticType) ? null : item.SemanticType,
            Children: children?.Count > 0 ? children : null);
    }
}

/// <summary>Result of an explore query.</summary>
internal sealed record ExploreQueryResult(
    bool Success,
    string? Error,
    string? RenderedOutput,
    IReadOnlyList<ExploreResultDto> Results,
    bool Truncated,
    ExploreStatusInfo? Status);

/// <summary>A single explore result item.</summary>
internal sealed record ExploreResultDto(
    string Uri,
    int Confidence,
    string? Kind,
    string? Headline,
    string? Structure,
    string? Snippet,
    string? Lang,
    string? SemanticType,
    IReadOnlyList<ExploreResultDto>? Children);

/// <summary>Trust signal information from explore/read responses.</summary>
internal sealed record ExploreStatusInfo(
    int IndexTotal,
    int IndexPending,
    int IndexFailed,
    int IndexStale,
    bool SemanticReady,
    int SemanticPercent,
    bool Ready,
    long ElapsedMs);
