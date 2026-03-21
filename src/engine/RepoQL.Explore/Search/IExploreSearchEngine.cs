using Microsoft.Extensions.Logging;
using RepoQL.Contracts.Configuration;
using RepoQL.Contracts.Embeddings;

namespace RepoQL.Explore.Search;

public interface IExploreSearchEngine
{
    Task<SearchEngineResult> SearchAsync(
        SearchParameters parameters,
        IJitObjectSearchService? jitService,
        JitEmbeddingCache? jitCache,
        CancellationToken cancellationToken);
}

/// <summary>
/// Unified explore search engine. Single code path: _explore_candidates → optional JIT
/// enrichment → pattern boosts → rerank → results. No parallel JIT path.
///
/// Purpose: Orchestrate search retrieval, JIT enrichment, and result grouping.
/// Complexity: Candidate retrieval, JIT eligibility check, result grouping by document,
///   pattern boost/penalty with re-sort, document-level reranking.
/// </summary>
public sealed class ExploreSearchEngine : IExploreSearchEngine
{
    private readonly IExploreCandidateService _candidateService;
    private readonly IRerankProvider _rerankProvider;
    private readonly RepoQlConfig _config;
    private readonly ILogger? _logger;

    public ExploreSearchEngine(
        IExploreCandidateService candidateService,
        IRerankProvider rerankProvider,
        RepoQlConfig? config = null,
        ILogger<ExploreSearchEngine>? logger = null)
    {
        _candidateService = candidateService ?? throw new ArgumentNullException(nameof(candidateService));
        _rerankProvider = rerankProvider ?? throw new ArgumentNullException(nameof(rerankProvider));
        _config = config ?? new RepoQlConfig();
        _logger = logger;
    }

    public async Task<SearchEngineResult> SearchAsync(
        SearchParameters parameters,
        IJitObjectSearchService? jitService,
        JitEmbeddingCache? jitCache,
        CancellationToken cancellationToken)
    {
        // Phase 1: Retrieve candidates via _explore_candidates SQL macro
        // Candidate count adapts to budget and breadth.
        // Low breadth concentrates budget on fewer, deeper results.
        // High breadth spreads across more results with lighter representation.
        var targetPerFile = (int)(300.0 / Math.Max(parameters.Breadth, 1));
        var k = Math.Clamp(parameters.TokenBudget / targetPerFile, 5, 200);
        var candidateResult = await _candidateService.SearchAsync(
            parameters.Keywords,
            parameters.Scope,
            k,
            cancellationToken).ConfigureAwait(false);

        var candidates = candidateResult.Candidates;

        // Phase 2: JIT enrichment for uncertain object candidates
        if (ShouldEnrichWithJit(parameters, jitService) && jitCache is not null)
        {
            var config = GetJitSearchConfig(parameters.Breadth, parameters.TokenBudget);
            var enrichment = await jitService!.EnrichAsync(
                parameters.Keywords!,
                candidates,
                jitCache,
                config,
                cancellationToken).ConfigureAwait(false);

            if (enrichment.ScoresChanged)
            {
                candidates = enrichment.Candidates;
                candidates = ApplyDocumentPromotion(candidates);
            }
        }

        // Phase 3: Build grouped results
        var results = BuildStandardResults(candidates, parameters);

        // Phase 4: Pattern boosts/penalties with re-sort
        var boosted = false;

        var boostPatterns = PatternBooster.ParsePatterns(string.Join(",", parameters.Patterns));
        boosted |= PatternBooster.ApplyBoosts(results, boostPatterns);

        if (parameters.PenalizePatterns is { Count: > 0 })
        {
            var penalizePatterns = PatternBooster.ParsePatterns(string.Join(",", parameters.PenalizePatterns));
            boosted |= PatternBooster.ApplyPenalties(results, penalizePatterns);
        }

        if (boosted)
            results.Sort((a, b) => b.RawScore.CompareTo(a.RawScore));

        // Phase 5: Document-level reranking via Voyage AI
        // Runs last so it sees JIT-corrected scores and pattern boosts.
        // Applies relevance-based modifier: above neutral → boost, below → heavier penalty.
        // Uses Question (natural language) when available; falls back to keywords.
        if (ShouldRerank(parameters, results))
        {
            var rerankQuery = parameters.Question ?? parameters.Keywords!;
            await ApplyRerankAsync(rerankQuery, results, cancellationToken)
                .ConfigureAwait(false);
        }

        return new SearchEngineResult(
            results,
            TotalDocumentsMatched: candidateResult.TotalMatched,
            TotalObjectsMatched: candidateResult.Candidates.Count(candidate => IsObject(candidate.NodeScope)),
            TrustSignal: null
        );
    }

    // ---- Reranking ----

    private const int RerankMaxDocuments = 20;
    private const double RerankRelevanceCutoff = 0.15; // Don't send docs below 15% of top score
    private const double RerankNeutralRelevance = 0.40; // Relevance scores above this boost, below this penalize
    private const double RerankBoostScale = 1.0;        // Trust positive signal
    private const double RerankPenaltyScale = 2.5;      // Trust negative signal more heavily

    private bool ShouldRerank(SearchParameters parameters, List<SearchResult> results)
    {
        if (_config.Search.RerankEnabled == false)
            return false;
        if (!_rerankProvider.Enabled)
            return false;
        if (string.IsNullOrWhiteSpace(parameters.Question) && string.IsNullOrWhiteSpace(parameters.Keywords))
            return false;
        if (results.Count < 5)
            return false;
        if (parameters.Breadth >= 8)
            return false;
        return true;
    }

    /// <summary>
    /// Rerank document-level results and apply relevance-based modifier in-place.
    /// Uses the reranker's relevance score directly: above neutral → boost, below → heavier penalty.
    /// Sends headline + structure to the reranker for richer signal than the calling LLM sees.
    /// </summary>
    private async Task ApplyRerankAsync(
        string query,
        List<SearchResult> results,
        CancellationToken cancellationToken)
    {
        // Select documents eligible for reranking (above relevance cutoff)
        var topScore = results[0].RawScore;
        var cutoff = topScore * RerankRelevanceCutoff;

        var rerankCandidates = new List<(int ResultIndex, SearchResult Result)>();
        for (var i = 0; i < results.Count && rerankCandidates.Count < RerankMaxDocuments; i++)
        {
            if (results[i].RawScore >= cutoff)
                rerankCandidates.Add((i, results[i]));
        }

        if (rerankCandidates.Count < 3)
            return;

        // Build rerank documents: headline + structure for each
        var documents = rerankCandidates.Select((c, idx) =>
        {
            var text = BuildRerankText(c.Result);
            return new RerankDocument(idx, text);
        }).ToList();

        RerankResult rerankResult;
        try
        {
            rerankResult = await _rerankProvider.RerankAsync(
                query, documents, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            // Reranking is best-effort — failures don't break search
            return;
        }

        if (rerankResult.Results.Count == 0)
            return;

        // Build relevance map: original candidate index → relevance score
        var relevanceMap = new Dictionary<int, double>();
        for (var i = 0; i < rerankResult.Results.Count; i++)
        {
            relevanceMap[rerankResult.Results[i].Index] = rerankResult.Results[i].RelevanceScore;
        }

        // Diagnostic: log reranker output with computed modifiers
        if (_logger?.IsEnabled(LogLevel.Information) == true)
        {
            _logger.LogInformation("Rerank diagnostics for query: \"{Query}\" (neutral={Neutral}, boost={Boost}, penalty={Penalty})",
                query, RerankNeutralRelevance, RerankBoostScale, RerankPenaltyScale);
            for (var i = 0; i < rerankResult.Results.Count; i++)
            {
                var rr = rerankResult.Results[i];
                var origIdx = rr.Index;
                var candidate = rerankCandidates[origIdx];
                var uri = candidate.Result.Uri;
                var shortUri = uri.Contains('/') ? uri[(uri.LastIndexOf('/') + 1)..] : uri;
                if (shortUri.Length > 50) shortUri = shortUri[..50];
                var distance = rr.RelevanceScore - RerankNeutralRelevance;
                var scale = distance >= 0 ? RerankBoostScale : RerankPenaltyScale;
                var mod = Math.Max(1.0 + distance * scale, 0.1);
                _logger.LogInformation(
                    "  Rerank [{NewRank}] relevance={Relevance} modifier={Modifier} base={BaseScore} adjusted={Adjusted} uri={Uri}",
                    i.ToString().PadLeft(2),
                    rr.RelevanceScore.ToString("F4"), mod.ToString("F3"),
                    candidate.Result.RawScore.ToString("F4"),
                    (candidate.Result.RawScore * mod).ToString("F4"), shortUri);
            }
        }

        // Apply relevance-based modifier to scores
        // Above neutral → boost (scale 1.0). Below neutral → heavier penalty (scale 2.5).
        for (var originalRank = 0; originalRank < rerankCandidates.Count; originalRank++)
        {
            if (!relevanceMap.TryGetValue(originalRank, out var relevance))
                continue;

            var distance = relevance - RerankNeutralRelevance;
            var scale = distance >= 0 ? RerankBoostScale : RerankPenaltyScale;
            var modifier = Math.Max(1.0 + distance * scale, 0.1); // floor at 10%

            var (resultIndex, result) = rerankCandidates[originalRank];
            results[resultIndex] = result with { RawScore = result.RawScore * modifier };
        }

        // Re-sort after modifier application
        results.Sort((a, b) => b.RawScore.CompareTo(a.RawScore));
    }

    private static string BuildRerankText(SearchResult result)
    {
        var parts = new List<string>(3);

        if (!string.IsNullOrEmpty(result.Headline))
            parts.Add(result.Headline);

        if (!string.IsNullOrEmpty(result.Structure))
            parts.Add(result.Structure);

        // Include child object headlines for richer document representation
        if (result.ChildObjects is { Count: > 0 })
        {
            var childHeadlines = result.ChildObjects
                .Where(c => !string.IsNullOrEmpty(c.Headline))
                .Select(c => c.Headline!)
                .Take(10);
            var joined = string.Join("; ", childHeadlines);
            if (joined.Length > 0)
                parts.Add(joined);
        }

        return string.Join("\n", parts);
    }

    // ---- JIT enrichment ----

    private static bool ShouldEnrichWithJit(SearchParameters parameters, IJitObjectSearchService? jitService)
    {
        if (jitService is null)
            return false;

        if (string.IsNullOrWhiteSpace(parameters.Keywords))
            return false;

        return parameters.Breadth <= 7;
    }

    private static List<SearchResult> BuildStandardResults(
        IReadOnlyList<ExploreCandidate> candidates,
        SearchParameters parameters)
    {
        if (candidates.Count == 0)
            return [];

        var grouped = candidates
            .GroupBy(candidate => candidate.DocId)
            .ToList();
        var snippetLimit = FileGrouper.CalculateSnippetLimit(
            parameters.Breadth,
            parameters.TokenBudget,
            grouped.Count);

        var results = new List<SearchResult>(grouped.Count);
        foreach (var group in grouped)
        {
            var orderedCandidates = group
                .OrderByDescending(candidate => candidate.Score)
                .ThenBy(candidate => candidate.Uri.Length)
                .ToList();
            var documentCandidate = orderedCandidates.FirstOrDefault(candidate => IsDocument(candidate.NodeScope));
            var objectCandidates = orderedCandidates
                .Where(candidate => IsObject(candidate.NodeScope))
                .OrderByDescending(candidate => candidate.Score)
                .ThenBy(candidate => candidate.Uri.Length)
                .ToList();

            var childObjects = objectCandidates
                .Select((candidate, index) => ToChildResult(candidate, includeSnippet: index < snippetLimit))
                .ToList();

            if (documentCandidate is not null)
            {
                results.Add(new SearchResult(
                    Uri: documentCandidate.Uri,
                    Scope: SearchScope.Document,
                    Kind: null,
                    Symbol: null,
                    Headline: documentCandidate.Headline,
                    Structure: documentCandidate.Structure,
                    Snippet: documentCandidate.Snippet,
                    LineStart: null,
                    LineEnd: null,
                    Lang: documentCandidate.Lang,
                    SemanticType: documentCandidate.Mime,
                    RawScore: documentCandidate.Score,
                    Confidence: documentCandidate.Confidence,
                    ChildObjects: childObjects.Count > 0 ? childObjects : null,
                    Provenance: documentCandidate.SemProvenance));
                continue;
            }

            if (objectCandidates.Count == 0)
                continue;

            var bestObject = objectCandidates[0];
            results.Add(new SearchResult(
                Uri: GetDocumentUri(bestObject),
                Scope: SearchScope.Document,
                Kind: null,
                Symbol: null,
                Headline: bestObject.Path ?? bestObject.Headline,
                Structure: null,
                Snippet: null,
                LineStart: null,
                LineEnd: null,
                Lang: bestObject.Lang,
                SemanticType: bestObject.Mime,
                RawScore: bestObject.Score,
                Confidence: bestObject.Confidence,
                ChildObjects: childObjects,
                Provenance: bestObject.SemProvenance));
        }

        results.Sort((a, b) => b.RawScore.CompareTo(a.RawScore));
        return results;
    }

    private static SearchResult ToChildResult(ExploreCandidate candidate, bool includeSnippet)
    {
        return new SearchResult(
            Uri: candidate.Uri,
            Scope: SearchScope.Symbol,
            Kind: candidate.Kind,
            Symbol: candidate.Symbol,
            Headline: candidate.Headline,
            Structure: candidate.Structure,
            Snippet: includeSnippet ? candidate.Snippet : null,
            LineStart: candidate.LineStart,
            LineEnd: candidate.LineEnd,
            Lang: candidate.Lang,
            SemanticType: candidate.Mime,
            RawScore: candidate.Score,
            Confidence: candidate.Confidence,
            ChildObjects: null,
            Provenance: candidate.SemProvenance);
    }

    private static string GetDocumentUri(ExploreCandidate candidate)
    {
        if (!string.IsNullOrWhiteSpace(candidate.Path))
            return candidate.Path;

        var fragmentIndex = candidate.Uri.IndexOf('#', StringComparison.Ordinal);
        return fragmentIndex >= 0 ? candidate.Uri[..fragmentIndex] : candidate.Uri;
    }

    /// <summary>
    /// After JIT enrichment changes scores, re-apply document promotion:
    /// document.score = max(own_score, best_child_score * 0.9)
    /// This matches the logic in _explore_candidates.sql (line ~304).
    /// </summary>
    private static IReadOnlyList<ExploreCandidate> ApplyDocumentPromotion(IReadOnlyList<ExploreCandidate> candidates)
    {
        var result = candidates.ToList();
        var grouped = result.Select((c, i) => (Candidate: c, Index: i))
            .GroupBy(x => x.Candidate.DocId);

        foreach (var group in grouped)
        {
            var docEntry = group.FirstOrDefault(x =>
                string.Equals(x.Candidate.NodeScope, "document", StringComparison.OrdinalIgnoreCase));
            if (docEntry.Candidate is null) continue;

            var bestChildScore = group
                .Where(x => string.Equals(x.Candidate.NodeScope, "object", StringComparison.OrdinalIgnoreCase))
                .Select(x => x.Candidate.Score)
                .DefaultIfEmpty(0.0)
                .Max();

            var promotedScore = bestChildScore * 0.9;
            if (promotedScore > docEntry.Candidate.Score)
            {
                result[docEntry.Index] = docEntry.Candidate with { Score = promotedScore };
            }
        }

        return result;
    }

    private static bool IsDocument(string nodeScope)
        => string.Equals(nodeScope, "document", StringComparison.OrdinalIgnoreCase);

    private static bool IsObject(string nodeScope)
        => string.Equals(nodeScope, "object", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Compute provenance label from signal contributions.
    /// Returns the dominant signal, or "mixed" if top two are close.
    /// </summary>
    public static string? ComputeProvenance(double semantic, double name, double regex, double chunk)
    {
        var signals = new List<(string Label, double Score)>
        {
            ("semantic", Math.Max(0.0, semantic)),
            ("name", Math.Max(0.0, name)),
            ("lexical", Math.Max(0.0, regex)),
            ("content", Math.Max(0.0, chunk))
        };

        var total = signals.Sum(s => s.Score);
        if (total <= 0.0)
            return null;

        var normalized = signals
            .Select(s => (s.Label, Contribution: s.Score / total))
            .OrderByDescending(s => s.Contribution)
            .ToList();

        var top = normalized[0];
        var second = normalized[1];
        if (top.Contribution <= 0.0)
            return null;

        if (second.Contribution > 0.0 && second.Contribution >= top.Contribution * 0.8)
            return "mixed";

        return top.Label;
    }

    internal static ObjectSearchConfig GetJitSearchConfig(int breadth, int tokenBudget)
    {
        var budgetScale = Math.Clamp(tokenBudget / 1500.0, 0.2, 1.5);

        if (breadth <= 2)
        {
            return new ObjectSearchConfig
            {
                MaxJitEmbeddings = Math.Max(5, (int)(40 * budgetScale)),
                JitEmbeddingThreshold = 0.12,
            };
        }

        if (breadth <= 6)
        {
            return new ObjectSearchConfig
            {
                MaxJitEmbeddings = Math.Max(5, (int)(30 * budgetScale)),
                JitEmbeddingThreshold = 0.15,
            };
        }

        return new ObjectSearchConfig
        {
            MaxJitEmbeddings = Math.Max(3, (int)(15 * budgetScale)),
            JitEmbeddingThreshold = 0.20,
        };
    }
}
