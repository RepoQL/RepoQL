using RepoQL.Contracts;

namespace RepoQL.Web.Services;

/// <summary>
/// Service for testing explore functionality with full score breakdown visibility.
/// Combines explore results with search() macro data to show why results ranked
/// the way they did.
///
/// <para><b>Purpose:</b> Enable developers to test explore parameters and understand
/// ranking decisions through visible score components.</para>
///
/// <para><b>Complexity:</b> Runs explore and search() in parallel for performance.
/// Merges results by URI. Handles cases where score query fails gracefully.</para>
/// </summary>
internal sealed class SearchService
{
    private readonly RepoQlConnectionManager _connectionManager;
    private readonly ILogger<SearchService> _logger;

    public SearchService(RepoQlConnectionManager connectionManager, ILogger<SearchService> logger)
    {
        _connectionManager = connectionManager;
        _logger = logger;
    }

    /// <summary>
    /// Check index readiness for a given scope before searching.
    /// </summary>
    public async Task<ReadinessResult> CheckReadinessAsync(string? scope, CancellationToken ct = default)
    {
        var client = await _connectionManager.GetClientAsync(ct).ConfigureAwait(false);

        try
        {
            // Query scope_readiness if available, otherwise use a simpler check
            var sql = string.IsNullOrWhiteSpace(scope)
                ? "SELECT COUNT(*) as total, SUM(CASE WHEN has_embeddings THEN 1 ELSE 0 END) as embedded FROM Files"
                : $@"SELECT COUNT(*) as total, SUM(CASE WHEN has_embeddings THEN 1 ELSE 0 END) as embedded
                     FROM Files WHERE uri LIKE '{EscapeSql(ConvertScopeToLike(scope))}'";

            var result = await client.ExecuteRawQueryAsync(sql, rowLimit: 1, cancellationToken: ct).ConfigureAwait(false);

            if (result.Rows.Count == 0)
                return new ReadinessResult(true, 0, 0, 0);

            var row = result.Rows[0];
            var total = GetInt(row, 0);
            var embedded = GetInt(row, 1);
            var pending = total - embedded;

            return new ReadinessResult(
                IsReady: pending == 0,
                TotalFiles: total,
                EmbeddedCount: embedded,
                PendingCount: pending);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to check readiness for scope {Scope}", scope);
            return new ReadinessResult(true, 0, 0, 0); // Assume ready on failure
        }
    }

    /// <summary>
    /// Execute a search with full score breakdown.
    /// </summary>
    public async Task<SearchResult> SearchAsync(SearchParams @params, CancellationToken ct = default)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var client = await _connectionManager.GetClientAsync(ct).ConfigureAwait(false);

        // Run readiness check, explore, and score query in parallel
        var readinessTask = CheckReadinessAsync(@params.Scope, ct);
        var exploreTask = ExecuteExploreAsync(client, @params, ct);
        var scoresTask = GetScoreBreakdownAsync(client, @params, ct);

        await Task.WhenAll(readinessTask, exploreTask, scoresTask).ConfigureAwait(false);

        var readiness = await readinessTask;
        var exploreResult = await exploreTask;
        var scores = await scoresTask;

        stopwatch.Stop();

        if (!exploreResult.Success)
        {
            return new SearchResult(
                Hits: [],
                Readiness: readiness,
                Duration: stopwatch.Elapsed,
                Error: exploreResult.Error);
        }

        // Merge explore results with score breakdown
        var hits = MergeResults(exploreResult.Results, scores, @params.Boost, @params.Penalize);

        return new SearchResult(
            Hits: hits,
            Readiness: readiness,
            Duration: stopwatch.Elapsed,
            Error: null);
    }

    private async Task<ExploreQueryResult> ExecuteExploreAsync(
        Protocol.IRepoQlClient client,
        SearchParams @params,
        CancellationToken ct)
    {
        try
        {
            var response = await client.ExploreAsync(
                @params.TokenBudget,
                @params.Breadth,
                @params.Scope,
                @params.Keywords,
                @params.Boost,
                @params.Penalize,
                @params.Limit,
                ct).ConfigureAwait(false);

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

            var results = response.Results.Select(r => new ExploreResultDto(
                Uri: r.Uri,
                Confidence: r.Confidence,
                Kind: string.IsNullOrEmpty(r.Kind) ? null : r.Kind,
                Headline: string.IsNullOrEmpty(r.Headline) ? null : r.Headline,
                Structure: null,
                Snippet: null,
                Lang: null,
                SemanticType: null,
                Children: null)).ToList();

            return new ExploreQueryResult(
                Success: true,
                Error: null,
                RenderedOutput: response.RenderedOutput,
                Results: results,
                Truncated: response.Truncated,
                Status: response.Status is not null ? new ExploreStatusInfo(
                    IndexTotal: response.Status.IndexTotal,
                    IndexPending: response.Status.IndexPending,
                    IndexFailed: response.Status.IndexFailed,
                    IndexStale: response.Status.IndexStale,
                    SemanticReady: response.Status.SemanticReady,
                    SemanticPercent: response.Status.SemanticPercent,
                    Ready: response.Status.Ready,
                    ElapsedMs: response.Status.ElapsedMs) : null);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Explore failed");
            return new ExploreQueryResult(
                Success: false,
                Error: ex.Message,
                RenderedOutput: null,
                Results: [],
                Truncated: false,
                Status: null);
        }
    }

    private async Task<Dictionary<string, ScoreInfo>> GetScoreBreakdownAsync(
        Protocol.IRepoQlClient client,
        SearchParams @params,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(@params.Keywords))
            return new Dictionary<string, ScoreInfo>();

        try
        {
            var scopeClause = string.IsNullOrWhiteSpace(@params.Scope)
                ? ""
                : $", scope := '{EscapeSql(ConvertScopeToLike(@params.Scope))}'";

            var boostClause = string.IsNullOrWhiteSpace(@params.Boost)
                ? ""
                : $", boost_pattern := '{EscapeSql(@params.Boost)}'";

            var penalizeClause = string.IsNullOrWhiteSpace(@params.Penalize)
                ? ""
                : $", negative_pattern := '{EscapeSql(@params.Penalize)}'";

            var limitValue = @params.Limit ?? 50;

            var sql = $@"
                SELECT
                    uri,
                    COALESCE(sem_score, 0) as sem_score,
                    COALESCE(bm25_score, 0) as bm25_score,
                    COALESCE(score, 0) as score,
                    COALESCE(deranked, false) as deranked,
                    COALESCE(source, '') as source
                FROM search('{EscapeSql(@params.Keywords)}'{scopeClause}{boostClause}{penalizeClause}, k := {limitValue})";

            var result = await client.ExecuteRawQueryAsync(sql, rowLimit: limitValue, cancellationToken: ct).ConfigureAwait(false);

            var scores = new Dictionary<string, ScoreInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in result.Rows)
            {
                var uri = GetString(row, 0);
                if (!string.IsNullOrEmpty(uri))
                {
                    scores[uri] = new ScoreInfo(
                        SemanticScore: GetFloat(row, 1),
                        Bm25Score: GetFloat(row, 2),
                        CombinedScore: GetFloat(row, 3),
                        Deranked: GetBool(row, 4),
                        Source: GetString(row, 5));
                }
            }

            return scores;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to get score breakdown");
            return new Dictionary<string, ScoreInfo>();
        }
    }

    private static List<SearchHit> MergeResults(
        IReadOnlyList<ExploreResultDto> exploreResults,
        Dictionary<string, ScoreInfo> scores,
        string? boostPattern,
        string? penalizePattern)
    {
        var hits = new List<SearchHit>();

        foreach (var result in exploreResults)
        {
            scores.TryGetValue(result.Uri, out var scoreInfo);

            var boosted = scoreInfo?.Source == "semantic" && !string.IsNullOrWhiteSpace(boostPattern);
            var penalized = scoreInfo?.Deranked ?? false;

            hits.Add(new SearchHit(
                Uri: result.Uri,
                Headline: result.Headline ?? "",
                Score: scoreInfo?.CombinedScore ?? (result.Confidence / 100f),
                SemanticScore: scoreInfo?.SemanticScore ?? 0,
                Bm25Score: scoreInfo?.Bm25Score ?? 0,
                Boosted: boosted,
                Penalized: penalized,
                BoostReason: boosted ? boostPattern : null,
                PenalizeReason: penalized ? penalizePattern : null));
        }

        return hits;
    }

    private static string ConvertScopeToLike(string scope)
    {
        // Convert glob-style patterns to SQL LIKE patterns
        // file:///src/** -> file:///src/%
        // file:///src/*.cs -> file:///src/%.cs
        return scope
            .Replace("**", "%", StringComparison.Ordinal)
            .Replace("*", "%", StringComparison.Ordinal);
    }

    private static string EscapeSql(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    private static string GetString(Contracts.RowData row, int index)
    {
        if (index >= row.Values.Count) return "";
        var value = row.Values[index];
        return value.KindCase == Google.Protobuf.WellKnownTypes.Value.KindOneofCase.StringValue
            ? value.StringValue
            : "";
    }

    private static int GetInt(Contracts.RowData row, int index)
    {
        if (index >= row.Values.Count) return 0;
        var value = row.Values[index];
        return value.KindCase == Google.Protobuf.WellKnownTypes.Value.KindOneofCase.NumberValue
            ? (int)value.NumberValue
            : 0;
    }

    private static float GetFloat(Contracts.RowData row, int index)
    {
        if (index >= row.Values.Count) return 0;
        var value = row.Values[index];
        return value.KindCase == Google.Protobuf.WellKnownTypes.Value.KindOneofCase.NumberValue
            ? (float)value.NumberValue
            : 0;
    }

    private static bool GetBool(Contracts.RowData row, int index)
    {
        if (index >= row.Values.Count) return false;
        var value = row.Values[index];
        return value.KindCase == Google.Protobuf.WellKnownTypes.Value.KindOneofCase.BoolValue && value.BoolValue;
    }

    private sealed record ScoreInfo(
        float SemanticScore,
        float Bm25Score,
        float CombinedScore,
        bool Deranked,
        string Source);
}

/// <summary>Parameters for a search operation.</summary>
internal sealed record SearchParams(
    string Keywords,
    int Breadth,
    int TokenBudget,
    string? Scope = null,
    string? Boost = null,
    string? Penalize = null,
    int? Limit = null);

/// <summary>Result of a search operation including hits, readiness, and timing.</summary>
internal sealed record SearchResult(
    IReadOnlyList<SearchHit> Hits,
    ReadinessResult Readiness,
    TimeSpan Duration,
    string? Error);

/// <summary>A single search result with full score breakdown.</summary>
internal sealed record SearchHit(
    string Uri,
    string Headline,
    float Score,
    float SemanticScore,
    float Bm25Score,
    bool Boosted,
    bool Penalized,
    string? BoostReason,
    string? PenalizeReason);

/// <summary>Index readiness status for a scope.</summary>
internal sealed record ReadinessResult(
    bool IsReady,
    int TotalFiles,
    int EmbeddedCount,
    int PendingCount);
