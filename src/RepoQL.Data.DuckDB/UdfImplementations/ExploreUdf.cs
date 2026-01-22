using RepoQL.Data.DuckDB.UdfFramework;
using RepoQL.Explore;

namespace RepoQL.Data.DuckDB.UdfImplementations;

/// <summary>
/// UDF class for explore search and exploration operations.
/// Method signatures define the contract - param names map to JSON keys (snake_case).
/// </summary>
[UdfClass]
public class ExploreUdf(ExploreOrchestrator orchestrator)
{
    /// <summary>
    /// Execute explore search and return TOON-formatted output.
    /// </summary>
    [ScalarUdf("_explore_internal", Description = "Execute explore search, returns formatted TOON output")]
    public string Execute(
        string keywords,
        [UdfDefault("'Find'")] string intent,
        [UdfDefault("1000")] int tokens,
        [UdfDefault("NULL")] string? scope,
        [UdfDefault("NULL")] string? boost,
        [UdfDefault("NULL")] string? penalize)
    {
        var query = new ExploreQuery(
            TokenBudget: tokens,
            Intent: ParseIntent(intent),
            Scope: scope,
            Keywords: keywords,
            Boost: boost,
            Penalize: penalize,
            Limit: null
        );

        var status = new IndexerStatus(0, true, true, 0);
        var result = orchestrator.ExecuteAsync(query, status, CancellationToken.None)
            .GetAwaiter().GetResult();

        return result.RenderedOutput ?? "";
    }

    /// <summary>
    /// Execute explore search and return structured results as rows.
    /// </summary>
    [StructuredUdf("_explore_structured_internal", Description = "Execute explore search, returns structured rows")]
    public IEnumerable<ExploreResultRow> ExecuteStructured(
        string keywords,
        [UdfDefault("'Find'")] string intent,
        [UdfDefault("1000")] int tokens,
        [UdfDefault("NULL")] string? scope,
        [UdfDefault("NULL")] string? boost,
        [UdfDefault("NULL")] string? penalize)
    {
        var query = new ExploreQuery(
            TokenBudget: tokens,
            Intent: ParseIntent(intent),
            Scope: scope,
            Keywords: keywords,
            Boost: boost,
            Penalize: penalize,
            Limit: null
        );

        var status = new IndexerStatus(0, true, true, 0);
        var result = orchestrator.ExecuteAsync(query, status, CancellationToken.None)
            .GetAwaiter().GetResult();

        return result.Results.SelectMany(r => FlattenResult(r, null, 0));
    }

    private static IEnumerable<ExploreResultRow> FlattenResult(ExploreResult r, string? parentUri, int depth)
    {
        yield return new ExploreResultRow(
            r.Uri, r.Confidence, r.Kind, r.Headline, r.Structure,
            r.Snippet, r.Lang, r.SemanticType, parentUri, depth
        );

        if (r.ChildObjects is { Count: > 0 })
        {
            foreach (var child in r.ChildObjects)
            foreach (var flattened in FlattenResult(child, r.Uri, depth + 1))
                yield return flattened;
        }
    }

    private static Intent ParseIntent(string? intent) => intent?.ToLowerInvariant() switch
    {
        "explore" => Intent.Inventory,
        "examine" => Intent.Inspect,
        _ => Intent.Locate
    };

    public record ExploreResultRow(
        string Uri, int Confidence, string? Kind, string? Headline, string? Structure,
        string? Snippet, string? Lang, string? SemanticType, string? ParentUri, int Depth
    );
}
