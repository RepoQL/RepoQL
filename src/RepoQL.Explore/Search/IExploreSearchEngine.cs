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
/// enrichment → pattern boosts → results. No parallel JIT path.
///
/// Purpose: Orchestrate search retrieval, JIT enrichment, and result grouping.
/// Complexity: Candidate retrieval, JIT eligibility check, result grouping by document,
///   pattern boost/penalty with re-sort.
/// </summary>
public sealed class ExploreSearchEngine : IExploreSearchEngine
{
    private readonly IExploreCandidateService _candidateService;

    public ExploreSearchEngine(IExploreCandidateService candidateService)
    {
        _candidateService = candidateService ?? throw new ArgumentNullException(nameof(candidateService));
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
            parameters.Question,
            parameters.Scope,
            k,
            cancellationToken).ConfigureAwait(false);

        var candidates = candidateResult.Candidates;

        // Phase 2: JIT enrichment for uncertain object candidates
        if (ShouldEnrichWithJit(parameters, jitService) && jitCache is not null)
        {
            var config = GetJitSearchConfig(parameters.Breadth, parameters.TokenBudget);
            var enrichment = await jitService!.EnrichAsync(
                parameters.Question!,
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

        return new SearchEngineResult(
            results,
            TotalDocumentsMatched: candidateResult.TotalMatched,
            TotalObjectsMatched: candidateResult.Candidates.Count(candidate => IsObject(candidate.NodeScope)),
            TrustSignal: null
        );
    }

    private static bool ShouldEnrichWithJit(SearchParameters parameters, IJitObjectSearchService? jitService)
    {
        if (jitService is null)
            return false;

        if (string.IsNullOrWhiteSpace(parameters.Question))
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
