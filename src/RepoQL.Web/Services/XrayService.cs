using RepoQL.Contracts;

namespace RepoQL.Web.Services;

/// <summary>
/// Service for executing xray queries from the web UI.
/// </summary>
internal sealed class XrayService
{
    private readonly RepoQlConnectionManager _connectionManager;
    private readonly ILogger<XrayService> _logger;

    public XrayService(RepoQlConnectionManager connectionManager, ILogger<XrayService> logger)
    {
        _connectionManager = connectionManager;
        _logger = logger;
    }

    public async Task<XrayQueryResult> ExecuteAsync(
        int tokenBudget,
        XrayIntent intent,
        string? scope,
        string? keywords,
        string? boost,
        string? penalize,
        int? limit,
        CancellationToken cancellationToken = default)
    {
        var client = await _connectionManager.GetClientAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogDebug("Executing xray query (budget={Budget}, intent={Intent})", tokenBudget, intent);

        var response = await client.XrayAsync(
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
            return new XrayQueryResult(
                Success: false,
                Error: response.Error,
                RenderedOutput: null,
                Results: [],
                Truncated: false,
                Status: null);
        }

        var results = response.Results.Select(MapResult).ToList();

        return new XrayQueryResult(
            Success: true,
            Error: null,
            RenderedOutput: response.RenderedOutput,
            Results: results,
            Truncated: response.Truncated,
            Status: new XrayStatusInfo(
                IndexPending: response.Status?.IndexPending ?? 0,
                SemanticReady: response.Status?.SemanticReady ?? false,
                Ready: response.Status?.Ready ?? false,
                ElapsedMs: response.Status?.ElapsedMs ?? 0));
    }

    private static XrayResultDto MapResult(XrayResultItem item)
    {
        var children = item.Children?.Select(MapResult).ToList();

        return new XrayResultDto(
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

/// <summary>Result of an xray query.</summary>
internal sealed record XrayQueryResult(
    bool Success,
    string? Error,
    string? RenderedOutput,
    IReadOnlyList<XrayResultDto> Results,
    bool Truncated,
    XrayStatusInfo? Status);

/// <summary>A single xray result item.</summary>
internal sealed record XrayResultDto(
    string Uri,
    int Confidence,
    string? Kind,
    string? Headline,
    string? Structure,
    string? Snippet,
    string? Lang,
    string? SemanticType,
    IReadOnlyList<XrayResultDto>? Children);

/// <summary>Indexer status information.</summary>
internal sealed record XrayStatusInfo(
    int IndexPending,
    bool SemanticReady,
    bool Ready,
    long ElapsedMs);
