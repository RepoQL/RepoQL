using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using RepoQL.Contracts.Configuration;
using RepoQL.Data.DuckDB;

namespace RepoQL.ConsoleApp.Host;

/// <summary>
/// Purpose: Shared adaptive semantic narrowing used by find-style workflows.
/// Complexity: Runs bounded candidate rounds over precomputed chunks, then optional
/// zoom refinement for line-level evidence and resilient lexical fallback.
/// </summary>
internal sealed class FindRefinementEngine(DuckDbDataStore db)
{
    private readonly DuckDbDataStore _db = db ?? throw new ArgumentNullException(nameof(db));

    public IReadOnlyList<string> PreselectDocumentUris(
        string keywords,
        IReadOnlyList<string> documentUris,
        int maxDocuments,
        int timeoutMs,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(keywords) || documentUris.Count == 0 || maxDocuments <= 0)
            return [];

        try
        {
            var escapedKeywords = EscapeSqlLiteral(keywords);
            var escapedUriGlob = EscapeSqlLiteral(BuildDocumentUriGlob(documentUris));
            var k = Math.Max(maxDocuments * 3, 40);

            var sql = $"""
                SELECT uri
                FROM _search_candidates(
                    '{escapedKeywords}',
                    k := {k},
                    uri_glob := '{escapedUriGlob}'
                )
                WHERE scope = 'document'
                ORDER BY score DESC
                LIMIT {maxDocuments}
                """;

            var rows = QueryWithTimeout(sql, timeoutMs, ct);
            var uris = new List<string>(rows.Count);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var row in rows)
            {
                ct.ThrowIfCancellationRequested();
                var uri = ReadString(row, "uri");
                if (string.IsNullOrWhiteSpace(uri))
                    continue;

                var documentUri = ToDocumentUri(uri!);
                if (seen.Add(documentUri))
                    uris.Add(documentUri);
            }

            return uris;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return [];
        }
    }

    public FindSemanticSearchOutcome ExecuteAdaptiveSearch(
        string keywords,
        IReadOnlyList<string> documentUris,
        FindRuntimeSettings settings,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keywords);

        var allResults = new Dictionary<string, FindSemanticMatch>(StringComparer.OrdinalIgnoreCase);
        var refinedChunkKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var candidateLimit = settings.InitialCandidateLimit;
        var rounds = 0;
        var widenings = 0;
        var fallbackUsed = false;
        var timedOut = false;
        string? degradedReason = null;

        var totalTimer = Stopwatch.StartNew();

        while (rounds < settings.MaxWideningRounds)
        {
            ct.ThrowIfCancellationRequested();
            rounds++;

            var remainingMs = settings.TotalTimeoutMs - (int)totalTimer.ElapsedMilliseconds;
            if (remainingMs <= 0)
            {
                timedOut = true;
                degradedReason ??= "adaptive time budget exceeded";
                break;
            }

            var roundTimeoutMs = Math.Clamp(remainingMs, 250, settings.RoundTimeoutMs);

            IReadOnlyList<FindCandidateChunk> roundCandidates;
            try
            {
                roundCandidates = ExecuteCandidateSearchRound(
                    keywords,
                    documentUris,
                    candidateLimit,
                    settings,
                    roundTimeoutMs,
                    ct);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                timedOut = true;
                degradedReason ??= "candidate search timed out";
                break;
            }
            catch (Exception ex) when (IsEmbeddingUnavailable(ex))
            {
                fallbackUsed = true;
                degradedReason ??= "embeddings unavailable";
                break;
            }
            catch (Exception)
            {
                fallbackUsed = true;
                degradedReason ??= "candidate search failed";
                break;
            }

            if (roundCandidates.Count == 0)
            {
                if (allResults.Count == 0)
                {
                    fallbackUsed = true;
                    degradedReason ??= "no semantic candidates";
                }
                break;
            }

            var newCandidates = new List<FindCandidateChunk>(roundCandidates.Count);
            foreach (var candidate in roundCandidates)
            {
                if (refinedChunkKeys.Add(BuildChunkKey(candidate)))
                    newCandidates.Add(candidate);
            }

            if (newCandidates.Count == 0)
            {
                if (candidateLimit >= settings.MaxCandidateLimit)
                    break;

                candidateLimit = NextCandidateLimit(candidateLimit, settings);
                widenings++;
                continue;
            }

            var roundInputs = newCandidates
                .OrderByDescending(c => c.Score)
                .Take(settings.MaxZoomInputsPerRound)
                .ToList();

            IReadOnlyList<FindSemanticMatch> refined;
            try
            {
                refined = ExecuteZoomRound(
                    keywords,
                    roundInputs,
                    settings,
                    roundTimeoutMs,
                    ct);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                timedOut = true;
                degradedReason ??= "zoom refinement timed out";
                break;
            }
            catch (Exception ex) when (IsEmbeddingUnavailable(ex))
            {
                fallbackUsed = true;
                degradedReason ??= "zoom refinement unavailable";
                break;
            }
            catch (Exception)
            {
                fallbackUsed = true;
                degradedReason ??= "zoom refinement failed";
                break;
            }

            MergeResults(allResults, refined);

            var qualified = allResults.Values
                .Where(r => r.Score >= settings.MinScoreThreshold)
                .OrderByDescending(r => r.Score)
                .ToList();

            if (!ShouldWiden(qualified, candidateLimit, settings))
                break;

            if (candidateLimit >= settings.MaxCandidateLimit)
                break;

            candidateLimit = NextCandidateLimit(candidateLimit, settings);
            widenings++;
        }

        if (fallbackUsed || allResults.Count == 0)
        {
            var fallback = ExecuteSearchFallback(keywords, documentUris, settings, ct);
            MergeResults(allResults, fallback);
        }

        var sorted = allResults.Values
            .OrderByDescending(r => r.Score)
            .ThenBy(r => r.Uri, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new FindSemanticSearchOutcome(
            Results: sorted,
            Rounds: rounds,
            Widenings: widenings,
            FinalCandidateLimit: candidateLimit,
            FallbackUsed: fallbackUsed,
            TimedOut: timedOut,
            DegradedReason: degradedReason);
    }

    private IReadOnlyList<FindCandidateChunk> ExecuteCandidateSearchRound(
        string keywords,
        IReadOnlyList<string> documentUris,
        int candidateLimit,
        FindRuntimeSettings settings,
        int roundTimeoutMs,
        CancellationToken ct)
    {
        if (documentUris.Count == 0)
            return [];

        var escapedKeywords = EscapeSqlLiteral(keywords);
        var escapedUriJson = EscapeSqlLiteral(JsonSerializer.Serialize(documentUris));

        var sql = $"""
            SELECT
                uri,
                chunk_index,
                start_byte,
                end_byte,
                sem_score
            FROM _find_candidates(
                '{escapedKeywords}',
                uri_json := '{escapedUriJson}',
                max_chunks := {candidateLimit},
                per_doc_limit := {settings.PerDocumentChunkLimit}
            )
            ORDER BY sem_score DESC
            LIMIT {candidateLimit}
            """;

        var rows = QueryWithTimeout(sql, roundTimeoutMs, ct);
        var results = new List<FindCandidateChunk>(rows.Count);

        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();

            var uri = ReadString(row, "uri");
            if (string.IsNullOrWhiteSpace(uri))
                continue;

            results.Add(new FindCandidateChunk(
                Uri: uri!,
                ChunkIndex: ReadInt(row, "chunk_index") ?? 0,
                StartByte: ReadLong(row, "start_byte"),
                EndByte: ReadLong(row, "end_byte"),
                Score: ReadDouble(row, "sem_score")));
        }

        return results;
    }

    private IReadOnlyList<FindSemanticMatch> ExecuteZoomRound(
        string keywords,
        IReadOnlyList<FindCandidateChunk> candidates,
        FindRuntimeSettings settings,
        int roundTimeoutMs,
        CancellationToken ct)
    {
        if (candidates.Count == 0)
            return [];

        var payload = candidates.Select(c => new
        {
            uri = c.Uri,
            start_byte = c.StartByte,
            end_byte = c.EndByte,
            score = c.Score
        }).ToArray();

        var escapedPayload = EscapeSqlLiteral(JsonSerializer.Serialize(payload));
        var escapedKeywords = EscapeSqlLiteral(keywords);

        var sql = $"""
            WITH refined AS (
                SELECT * FROM zoom_and_enhance(
                    '{escapedPayload}',
                    '{escapedKeywords}',
                    min_lines := {settings.ZoomMinLines},
                    max_depth := {settings.ZoomMaxDepth},
                    threshold := {settings.ZoomThreshold.ToString(CultureInfo.InvariantCulture)}
                )
            )
            SELECT
                r.uri,
                r.start_line AS line_start,
                r.end_line AS line_end,
                r.score,
                a.headline,
                (SELECT string_agg(s.text, E'\n' ORDER BY s.line_number)
                 FROM snippet(r.uri || '#line=' || r.start_line || ',' || r.end_line, {settings.ContextLines}) s
                ) AS snippet
            FROM refined r
            JOIN node n ON n.uri = r.uri AND n.kind = 'document'
            JOIN artifact a ON a.id = n.artifact_id
            ORDER BY r.score DESC
            LIMIT {Math.Max(settings.MaxResults * 8, candidates.Count * 2)}
            """;

        var rows = QueryWithTimeout(sql, roundTimeoutMs, ct);
        var results = new List<FindSemanticMatch>(rows.Count);

        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();

            var uri = ReadString(row, "uri");
            if (string.IsNullOrWhiteSpace(uri))
                continue;

            var score = ReadDouble(row, "score");
            if (double.IsNaN(score) || double.IsInfinity(score))
                continue;

            results.Add(new FindSemanticMatch(
                Uri: uri!,
                Headline: ReadString(row, "headline"),
                Snippet: ReadString(row, "snippet"),
                LineStart: ReadInt(row, "line_start"),
                LineEnd: ReadInt(row, "line_end"),
                Score: score,
                SemanticScore: score,
                Lang: null));
        }

        return results;
    }

    private IReadOnlyList<FindSemanticMatch> ExecuteSearchFallback(
        string keywords,
        IReadOnlyList<string> documentUris,
        FindRuntimeSettings settings,
        CancellationToken ct)
    {
        var results = new List<FindSemanticMatch>();

        try
        {
            var escapedKeywords = EscapeSqlLiteral(keywords);
            var escapedUriGlob = EscapeSqlLiteral(BuildDocumentUriGlob(documentUris));

            var sql = $"""
                SELECT
                    uri,
                    scope,
                    headline,
                    snippet,
                    line_start,
                    line_end,
                    score,
                    dense_score,
                    bm25_score
                FROM _search_candidates(
                    '{escapedKeywords}',
                    k := {Math.Max(settings.MaxResults * 4, 40)},
                    uri_glob := '{escapedUriGlob}'
                )
                WHERE scope = 'document'
                ORDER BY score DESC
                LIMIT {Math.Max(settings.MaxResults * 2, 40)}
                """;

            var rows = _db.Query(sql, ct);

            foreach (var row in rows)
            {
                ct.ThrowIfCancellationRequested();

                var uri = ReadString(row, "uri");
                if (string.IsNullOrWhiteSpace(uri))
                    continue;

                results.Add(new FindSemanticMatch(
                    Uri: uri!,
                    Headline: ReadString(row, "headline"),
                    Snippet: ReadString(row, "snippet"),
                    LineStart: ReadInt(row, "line_start"),
                    LineEnd: ReadInt(row, "line_end"),
                    Score: ReadDouble(row, "score"),
                    SemanticScore: ReadDouble(row, "dense_score"),
                    Lang: ReadString(row, "lang")));
            }
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // Search fallback failed completely.
        }

        return results;
    }

    private IReadOnlyList<IReadOnlyDictionary<string, object?>> QueryWithTimeout(
        string sql,
        int timeoutMs,
        CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(50, timeoutMs)));

        try
        {
            return _db.Query(sql, timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw;
        }
    }

    private static bool ShouldWiden(
        IReadOnlyList<FindSemanticMatch> qualified,
        int candidateLimit,
        FindRuntimeSettings settings)
    {
        if (candidateLimit >= settings.MaxCandidateLimit)
            return false;

        if (qualified.Count < settings.TargetQualifiedMatches)
            return true;

        if (qualified.Count < 2)
            return true;

        var pivotIndex = Math.Min(settings.TargetQualifiedMatches - 1, qualified.Count - 1);
        var margin = qualified[0].Score - qualified[pivotIndex].Score;
        return margin < settings.ConfidenceMargin;
    }

    private static int NextCandidateLimit(int current, FindRuntimeSettings settings)
    {
        var widened = (int)Math.Ceiling(current * settings.GrowthMultiplier);
        if (widened <= current)
            widened = current + 1;

        return Math.Min(settings.MaxCandidateLimit, widened);
    }

    private static void MergeResults(
        IDictionary<string, FindSemanticMatch> destination,
        IReadOnlyList<FindSemanticMatch> incoming)
    {
        foreach (var result in incoming)
        {
            var key = BuildResultKey(result);
            if (!destination.TryGetValue(key, out var existing) || result.Score > existing.Score)
                destination[key] = result;
        }
    }

    private static string EscapeSqlLiteral(string value)
        => value.Replace("'", "''", StringComparison.Ordinal);

    private static string BuildDocumentUriGlob(IReadOnlyList<string> documentUris)
        => string.Join(";", documentUris);

    private static bool IsEmbeddingUnavailable(Exception ex)
    {
        var message = ex.Message;
        return message.Contains("embeddings", StringComparison.OrdinalIgnoreCase)
               || message.Contains("embedding", StringComparison.OrdinalIgnoreCase)
               || message.Contains("not ready", StringComparison.OrdinalIgnoreCase)
               || message.Contains("zoom_and_enhance", StringComparison.OrdinalIgnoreCase)
               || message.Contains("embed_query", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildChunkKey(FindCandidateChunk chunk)
        => $"{chunk.Uri}|{chunk.ChunkIndex}|{chunk.StartByte}|{chunk.EndByte}";

    private static string BuildResultKey(FindSemanticMatch result)
        => $"{result.Uri}|{result.LineStart}|{result.LineEnd}";

    private static string ToDocumentUri(string uri)
    {
        var hash = uri.IndexOf('#', StringComparison.Ordinal);
        return hash >= 0 ? uri[..hash] : uri;
    }

    private static string? ReadString(IReadOnlyDictionary<string, object?> row, string key)
        => row.TryGetValue(key, out var value) ? value?.ToString() : null;

    private static int? ReadInt(IReadOnlyDictionary<string, object?> row, string key)
    {
        if (!row.TryGetValue(key, out var value) || value is null)
            return null;

        return Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    private static long? ReadLong(IReadOnlyDictionary<string, object?> row, string key)
    {
        if (!row.TryGetValue(key, out var value) || value is null)
            return null;

        return Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static double ReadDouble(IReadOnlyDictionary<string, object?> row, string key)
    {
        if (!row.TryGetValue(key, out var value) || value is null)
            return 0.0;

        return Convert.ToDouble(value, CultureInfo.InvariantCulture);
    }

    private sealed record FindCandidateChunk(
        string Uri,
        int ChunkIndex,
        long? StartByte,
        long? EndByte,
        double Score);
}

internal sealed record FindSemanticSearchOutcome(
    IReadOnlyList<FindSemanticMatch> Results,
    int Rounds,
    int Widenings,
    int FinalCandidateLimit,
    bool FallbackUsed,
    bool TimedOut,
    string? DegradedReason);

internal sealed record FindSemanticMatch(
    string Uri,
    string? Headline,
    string? Snippet,
    int? LineStart,
    int? LineEnd,
    double Score,
    double SemanticScore,
    string? Lang);

internal sealed record FindRuntimeSettings(
    int MaxResults,
    double MinScoreThreshold,
    int InitialCandidateLimit,
    int MaxCandidateLimit,
    double GrowthMultiplier,
    int MaxWideningRounds,
    int TargetQualifiedMatches,
    double ConfidenceMargin,
    int PerDocumentChunkLimit,
    int MaxZoomInputsPerRound,
    int ZoomMinLines,
    int ZoomMaxDepth,
    double ZoomThreshold,
    int ContextLines,
    int RoundTimeoutMs,
    int TotalTimeoutMs)
{
    private const int DefaultMaxResults = 20;
    private const double DefaultMinScoreThreshold = 0.10;
    private const int DefaultInitialCandidateLimit = 96;
    private const int DefaultMaxCandidateLimit = 768;
    private const double DefaultGrowthMultiplier = 2.0;
    private const int DefaultMaxWideningRounds = 4;
    private const int DefaultTargetQualifiedMatches = 24;
    private const double DefaultConfidenceMargin = 0.05;
    private const int DefaultPerDocumentChunkLimit = 3;
    private const int DefaultMaxZoomInputsPerRound = 192;
    private const int DefaultZoomMinLines = 8;
    private const int DefaultZoomMaxDepth = 3;
    private const double DefaultZoomThreshold = 0.20;
    private const int DefaultContextLines = 2;
    private const int DefaultRoundTimeoutMs = 20_000;
    private const int DefaultTotalTimeoutMs = 90_000;

    public static FindRuntimeSettings From(
        RepoQlConfig.FindSettings? settings,
        int? roundTimeoutOverrideMs = null,
        int? totalTimeoutOverrideMs = null)
    {
        var initialCandidateLimit = Math.Clamp(
            settings?.InitialCandidateLimit ?? DefaultInitialCandidateLimit,
            16,
            4_096);

        var maxCandidateLimit = Math.Clamp(
            settings?.MaxCandidateLimit ?? DefaultMaxCandidateLimit,
            initialCandidateLimit,
            20_000);

        var growthPercent = Math.Clamp(settings?.GrowthPercent ?? (int)(DefaultGrowthMultiplier * 100), 110, 500);
        var growthMultiplier = growthPercent / 100.0;

        var maxWideningRounds = Math.Clamp(settings?.MaxWideningRounds ?? DefaultMaxWideningRounds, 1, 12);
        var maxResults = Math.Clamp(settings?.MaxResults ?? DefaultMaxResults, 1, 200);
        var minScoreThreshold = Math.Clamp(settings?.MinScoreThreshold ?? DefaultMinScoreThreshold, 0.0, 1.0);
        var targetQualifiedMatches = Math.Clamp(settings?.TargetQualifiedMatches ?? DefaultTargetQualifiedMatches, 1, maxCandidateLimit);
        var confidenceMargin = Math.Clamp(settings?.ConfidenceMargin ?? DefaultConfidenceMargin, 0.0, 1.0);
        var perDocChunkLimit = Math.Clamp(settings?.PerDocumentChunkLimit ?? DefaultPerDocumentChunkLimit, 1, 24);
        var maxZoomInputs = Math.Clamp(settings?.MaxZoomInputsPerRound ?? DefaultMaxZoomInputsPerRound, 8, 2_048);
        var zoomMinLines = Math.Clamp(settings?.ZoomMinLines ?? DefaultZoomMinLines, 2, 500);
        var zoomMaxDepth = Math.Clamp(settings?.ZoomMaxDepth ?? DefaultZoomMaxDepth, 0, 8);
        var zoomThreshold = Math.Clamp(settings?.ZoomThreshold ?? DefaultZoomThreshold, 0.0, 1.0);
        var contextLines = Math.Clamp(settings?.ContextLines ?? DefaultContextLines, 0, 20);

        var configuredRound = settings?.RoundTimeoutMs ?? DefaultRoundTimeoutMs;
        var configuredTotal = settings?.TotalTimeoutMs ?? DefaultTotalTimeoutMs;
        var roundTimeoutMs = Math.Clamp(roundTimeoutOverrideMs ?? configuredRound, 1_000, 300_000);
        var totalTimeoutMs = Math.Clamp(totalTimeoutOverrideMs ?? configuredTotal, roundTimeoutMs, 900_000);

        return new FindRuntimeSettings(
            MaxResults: maxResults,
            MinScoreThreshold: minScoreThreshold,
            InitialCandidateLimit: initialCandidateLimit,
            MaxCandidateLimit: maxCandidateLimit,
            GrowthMultiplier: growthMultiplier,
            MaxWideningRounds: maxWideningRounds,
            TargetQualifiedMatches: targetQualifiedMatches,
            ConfidenceMargin: confidenceMargin,
            PerDocumentChunkLimit: perDocChunkLimit,
            MaxZoomInputsPerRound: maxZoomInputs,
            ZoomMinLines: zoomMinLines,
            ZoomMaxDepth: zoomMaxDepth,
            ZoomThreshold: zoomThreshold,
            ContextLines: contextLines,
            RoundTimeoutMs: roundTimeoutMs,
            TotalTimeoutMs: totalTimeoutMs);
    }
}
