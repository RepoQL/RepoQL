namespace RepoQL.Explore.Search;

public interface IExploreSearchEngine
{
    Task<SearchEngineResult> SearchAsync(
        SearchParameters parameters,
        IJitObjectSearchService? jitService,
        JitEmbeddingCache? jitCache,
        CancellationToken cancellationToken);
}

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
        if (ShouldUseJitSearch(parameters, jitService) && jitCache is not null)
        {
            return await SearchWithJitAsync(parameters, jitService!, jitCache, cancellationToken)
                .ConfigureAwait(false);
        }

        return await SearchStandardAsync(parameters, cancellationToken).ConfigureAwait(false);
    }

    private static bool ShouldUseJitSearch(SearchParameters parameters, IJitObjectSearchService? jitService)
    {
        if (jitService is null)
            return false;

        if (string.IsNullOrWhiteSpace(parameters.Question))
            return false;

        return parameters.Breadth <= 7;
    }

    private async Task<SearchEngineResult> SearchWithJitAsync(
        SearchParameters parameters,
        IJitObjectSearchService jitService,
        JitEmbeddingCache jitCache,
        CancellationToken cancellationToken)
    {
        var config = GetJitSearchConfig(parameters.Breadth, parameters.TokenBudget);

        var boostPattern = parameters.Patterns.Count > 0
            ? string.Join(",", parameters.Patterns)
            : null;
        var penalizePattern = parameters.PenalizePatterns is { Count: > 0 }
            ? string.Join(",", parameters.PenalizePatterns)
            : null;

        var jitResult = await jitService.SearchAsync(
            parameters.Question,
            parameters.Scope,
            boostPattern,
            penalizePattern,
            config,
            jitCache,
            cancellationToken).ConfigureAwait(false);

        var results = ConvertJitResults(jitResult, parameters);

        return new SearchEngineResult(
            results,
            TotalDocumentsMatched: jitResult.SelectedDocuments.Count,
            TotalObjectsMatched: jitResult.ScoredObjects.Count,
            TrustSignal: null
        );
    }

    private async Task<SearchEngineResult> SearchStandardAsync(
        SearchParameters parameters,
        CancellationToken cancellationToken)
    {
        var k = Math.Min(200, (parameters.Breadth * 15) + 20);
        var candidateResult = await _candidateService.SearchAsync(
            parameters.Question,
            parameters.Scope,
            k,
            cancellationToken).ConfigureAwait(false);

        var results = BuildStandardResults(candidateResult.Candidates, parameters);
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

    private static bool IsDocument(string nodeScope)
        => string.Equals(nodeScope, "document", StringComparison.OrdinalIgnoreCase);

    private static bool IsObject(string nodeScope)
        => string.Equals(nodeScope, "object", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<SearchResult> ConvertJitResults(
        JitObjectSearchResult jitResult,
        SearchParameters parameters)
    {
        var results = new List<SearchResult>();

        var objectsByDoc = jitResult.ScoredObjects
            .GroupBy(o => o.DocumentUri)
            .ToDictionary(g => g.Key, g => g.ToList());
        var snippetLimit = FileGrouper.CalculateSnippetLimit(
            parameters.Breadth,
            parameters.TokenBudget,
            Math.Max(jitResult.SelectedDocuments.Count, objectsByDoc.Count));
        var documentScores = jitResult.SelectedDocuments.Select(doc => doc.DocumentScore).ToList();
        var minDocumentScore = documentScores.Count > 0 ? documentScores.Min() : 0.0;
        var documentScoreRange = (documentScores.Count > 0 ? documentScores.Max() : 0.0) - minDocumentScore;

        var processedDocs = new HashSet<string>();
        foreach (var doc in jitResult.SelectedDocuments)
        {
            processedDocs.Add(doc.DocumentUri);

            if (objectsByDoc.TryGetValue(doc.DocumentUri, out var docObjects) && docObjects.Count > 0)
            {
                var childObjects = new List<SearchResult>();
                var snippetObjects = docObjects.Take(snippetLimit).ToList();
                var headlineObjects = docObjects.Skip(snippetLimit).ToList();

                foreach (var obj in snippetObjects)
                {
                    childObjects.Add(new SearchResult(
                        Uri: obj.Uri,
                        Scope: SearchScope.Symbol,
                        Kind: obj.Kind,
                        Symbol: obj.Symbol,
                        Headline: obj.Headline,
                        Structure: obj.Structure,
                        Snippet: obj.Snippet ?? obj.Body,
                        LineStart: obj.LineStart,
                        LineEnd: obj.LineEnd,
                        Lang: obj.Lang,
                        SemanticType: obj.SemanticType,
                        RawScore: obj.FinalScore,
                        Confidence: obj.Confidence,
                        ChildObjects: null,
                        Provenance: ComputeProvenance(
                            semantic: obj.SemanticScore,
                            name: obj.NameHitScore,
                            regex: obj.RegexHitScore,
                            chunk: obj.ChunkOverlapScore)
                    ));
                }

                foreach (var obj in headlineObjects)
                {
                    childObjects.Add(new SearchResult(
                        Uri: obj.Uri,
                        Scope: SearchScope.Symbol,
                        Kind: obj.Kind,
                        Symbol: obj.Symbol,
                        Headline: obj.Headline,
                        Structure: obj.Structure,
                        Snippet: null,
                        LineStart: obj.LineStart,
                        LineEnd: obj.LineEnd,
                        Lang: obj.Lang,
                        SemanticType: obj.SemanticType,
                        RawScore: obj.FinalScore,
                        Confidence: obj.Confidence,
                        ChildObjects: null,
                        Provenance: ComputeProvenance(
                            semantic: obj.SemanticScore,
                            name: obj.NameHitScore,
                            regex: obj.RegexHitScore,
                            chunk: obj.ChunkOverlapScore)
                    ));
                }

                results.Add(new SearchResult(
                    Uri: doc.DocumentUri,
                    Scope: SearchScope.Document,
                    Kind: null,
                    Symbol: null,
                    Headline: doc.Headline,
                    Structure: doc.Structure,
                    Snippet: null,
                    LineStart: null,
                    LineEnd: null,
                    Lang: doc.Lang,
                    SemanticType: doc.SemanticType,
                    RawScore: doc.DocumentScore,
                    Confidence: documentScoreRange <= 0
                        ? 50
                        : (int)(10 + (90 * (doc.DocumentScore - minDocumentScore) / documentScoreRange)),
                    ChildObjects: childObjects,
                    Provenance: ComputeDocumentProvenance(doc)
                ));
            }
            else
            {
                results.Add(new SearchResult(
                    Uri: doc.DocumentUri,
                    Scope: SearchScope.Document,
                    Kind: null,
                    Symbol: null,
                    Headline: doc.Headline,
                    Structure: doc.Structure,
                    Snippet: null,
                    LineStart: null,
                    LineEnd: null,
                    Lang: doc.Lang,
                    SemanticType: doc.SemanticType,
                    RawScore: doc.DocumentScore,
                    Confidence: documentScoreRange <= 0
                        ? 50
                        : (int)(10 + (90 * (doc.DocumentScore - minDocumentScore) / documentScoreRange)),
                    ChildObjects: null,
                    Provenance: ComputeDocumentProvenance(doc)
                ));
            }
        }

        foreach (var (docUri, docObjects) in objectsByDoc)
        {
            if (processedDocs.Contains(docUri))
                continue;

            var snippetObjects = docObjects.Take(snippetLimit).ToList();
            var headlineObjects = docObjects.Skip(snippetLimit).ToList();

            foreach (var obj in snippetObjects)
            {
                results.Add(new SearchResult(
                    Uri: obj.Uri,
                    Scope: SearchScope.Symbol,
                    Kind: obj.Kind,
                    Symbol: obj.Symbol,
                    Headline: obj.Headline,
                    Structure: obj.Structure,
                    Snippet: obj.Snippet ?? obj.Body,
                    LineStart: obj.LineStart,
                    LineEnd: obj.LineEnd,
                    Lang: obj.Lang,
                    SemanticType: obj.SemanticType,
                    RawScore: obj.FinalScore,
                    Confidence: obj.Confidence,
                    ChildObjects: null,
                    Provenance: ComputeProvenance(
                        semantic: obj.SemanticScore,
                        name: obj.NameHitScore,
                        regex: obj.RegexHitScore,
                        chunk: obj.ChunkOverlapScore)
                ));
            }

            foreach (var obj in headlineObjects)
            {
                results.Add(new SearchResult(
                    Uri: obj.Uri,
                    Scope: SearchScope.Symbol,
                    Kind: obj.Kind,
                    Symbol: obj.Symbol,
                    Headline: obj.Headline,
                    Structure: obj.Structure,
                    Snippet: null,
                    LineStart: obj.LineStart,
                    LineEnd: obj.LineEnd,
                    Lang: obj.Lang,
                    SemanticType: obj.SemanticType,
                    RawScore: obj.FinalScore,
                    Confidence: obj.Confidence,
                    ChildObjects: null,
                    Provenance: ComputeProvenance(
                        semantic: obj.SemanticScore,
                        name: obj.NameHitScore,
                        regex: obj.RegexHitScore,
                        chunk: obj.ChunkOverlapScore)
                ));
            }
        }

        if (parameters.Patterns.Count > 0)
        {
            var patterns = PatternBooster.ParsePatterns(string.Join(",", parameters.Patterns));
            PatternBooster.ApplyBoosts(results, patterns);
        }

        if (parameters.PenalizePatterns is { Count: > 0 })
        {
            var penalizePatterns = PatternBooster.ParsePatterns(string.Join(",", parameters.PenalizePatterns));
            PatternBooster.ApplyPenalties(results, penalizePatterns);
        }

        return results;
    }

    private static string? ComputeDocumentProvenance(DocumentExpansionCandidate doc)
    {
        var lexical = (doc.StructMentions + doc.BodyMentions) * 0.1;
        return ComputeProvenance(
            semantic: doc.SemanticScore,
            name: 0.0,
            regex: lexical,
            chunk: doc.Bm25Score);
    }

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

    private static ObjectSearchConfig GetJitSearchConfig(int breadth, int tokenBudget)
    {
        var budgetScale = Math.Clamp(tokenBudget / 1500.0, 0.2, 1.5);

        if (breadth <= 2)
        {
            return new ObjectSearchConfig
            {
                MinProbabilityMass = 0.90,
                MaxDocumentsToExpand = Math.Max(2, (int)(10 * budgetScale)),
                MinDocumentsToExpand = 2,
                MaxJitEmbeddings = Math.Max(5, (int)(40 * budgetScale)),
                JitEmbeddingThreshold = 0.12,
                MaxObjectsPerDocument = 60
            };
        }

        if (breadth <= 6)
        {
            return new ObjectSearchConfig
            {
                MinProbabilityMass = 0.85,
                MaxDocumentsToExpand = Math.Max(3, (int)(15 * budgetScale)),
                MinDocumentsToExpand = 3,
                MaxJitEmbeddings = Math.Max(5, (int)(30 * budgetScale)),
                JitEmbeddingThreshold = 0.15,
                MaxObjectsPerDocument = 50
            };
        }

        if (breadth <= 8)
        {
            return new ObjectSearchConfig
            {
                MinProbabilityMass = 0.80,
                MaxDocumentsToExpand = Math.Max(2, (int)(8 * budgetScale)),
                MinDocumentsToExpand = 2,
                MaxJitEmbeddings = Math.Max(3, (int)(15 * budgetScale)),
                JitEmbeddingThreshold = 0.20,
                MaxObjectsPerDocument = 30
            };
        }

        return new ObjectSearchConfig
        {
            MinProbabilityMass = 0.75,
            MaxDocumentsToExpand = 0,
            MinDocumentsToExpand = 0,
            MaxJitEmbeddings = 0,
            JitEmbeddingThreshold = 1.0,
            MaxObjectsPerDocument = 0
        };
    }
}
