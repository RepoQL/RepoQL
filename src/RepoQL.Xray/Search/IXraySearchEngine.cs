namespace RepoQL.Xray.Search;

/// <summary>
/// Engine for executing xray searches.
/// </summary>
public interface IXraySearchEngine
{
    /// <summary>
    /// Execute a search and return grouped, scored results.
    /// </summary>
    /// <param name="parameters">Search parameters.</param>
    /// <param name="jitService">Optional JIT object search service for enhanced object scoring.</param>
    /// <param name="jitCache">Session-level JIT embedding cache.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<SearchEngineResult> SearchAsync(
        SearchParameters parameters,
        IJitObjectSearchService? jitService,
        JitEmbeddingCache? jitCache,
        CancellationToken cancellationToken);
}

/// <summary>
/// Default implementation of the xray search engine.
/// Supports optional JIT object search for enhanced semantic object scoring.
/// </summary>
public sealed class XraySearchEngine : IXraySearchEngine
{
    private readonly IDocumentSearchService _documentSearch;
    private readonly IObjectSearchService _objectSearch;

    public XraySearchEngine(
        IDocumentSearchService documentSearch,
        IObjectSearchService objectSearch)
    {
        _documentSearch = documentSearch ?? throw new ArgumentNullException(nameof(documentSearch));
        _objectSearch = objectSearch ?? throw new ArgumentNullException(nameof(objectSearch));
    }

    public async Task<SearchEngineResult> SearchAsync(
        SearchParameters parameters,
        IJitObjectSearchService? jitService,
        JitEmbeddingCache? jitCache,
        CancellationToken cancellationToken)
    {
        // Check if we should use JIT object search
        if (ShouldUseJitSearch(parameters, jitService) && jitCache is not null)
        {
            return await SearchWithJitAsync(parameters, jitService!, jitCache, cancellationToken)
                .ConfigureAwait(false);
        }

        // Standard search path
        return await SearchStandardAsync(parameters, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Determine whether to use JIT object search based on parameters and availability.
    /// JIT search is used when: service is available, question is provided, and intent is Find/Examine/Understand.
    /// </summary>
    private static bool ShouldUseJitSearch(SearchParameters parameters, IJitObjectSearchService? jitService)
    {
        if (jitService is null)
            return false;

        if (string.IsNullOrWhiteSpace(parameters.Question))
            return false;

        // JIT search is most beneficial for Find/Examine/Understand intents
        return parameters.Intent is Intent.Find or Intent.Examine or Intent.Understand;
    }

    /// <summary>
    /// Execute search using JIT object search service.
    /// </summary>
    private async Task<SearchEngineResult> SearchWithJitAsync(
        SearchParameters parameters,
        IJitObjectSearchService jitService,
        JitEmbeddingCache jitCache,
        CancellationToken cancellationToken)
    {
        var config = GetJitSearchConfig(parameters.Intent, parameters.TokenBudget);

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
            IndexerStatus: null
        );
    }

    /// <summary>
    /// Standard search implementation (no JIT embeddings).
    /// </summary>
    private async Task<SearchEngineResult> SearchStandardAsync(
        SearchParameters parameters,
        CancellationToken cancellationToken)
    {
        // 1. Plan query strategy
        var plan = QueryStrategy.Plan(parameters);

        // 2. Execute document search
        var docResult = await _documentSearch.SearchAsync(
            parameters.Scope,
            parameters.Question,
            plan.DocumentLimit * 2, // Fetch extra to account for filtering
            cancellationToken).ConfigureAwait(false);

        var documents = docResult.Documents.Take(plan.DocumentLimit).ToList();

        // 3. Execute object search (if planned)
        IReadOnlyList<ObjectMatch> objects;
        if (plan.FetchObjects && documents.Count > 0)
        {
            var docUris = documents.Select(d => d.Uri).ToList();
            objects = await _objectSearch.SearchInDocumentsAsync(
                docUris,
                parameters.Question,
                plan.ObjectsPerDocument,
                cancellationToken).ConfigureAwait(false);

            // 4. Apply chunk proximity boosts to objects
            ChunkProximityBooster.ApplyBoosts((IList<ObjectMatch>)objects, docResult.ChunkScores);
        }
        else
        {
            objects = [];
        }

        // 5. Group by file (3 snippets + rest as headlines)
        var groups = FileGrouper.Group(documents, objects);

        // 6. Flatten to results
        var results = FileGrouper.Flatten(groups).ToList();

        // 7. Apply pattern boosts and penalties
        var boostPatterns = PatternBooster.ParsePatterns(string.Join(",", parameters.Patterns));
        PatternBooster.ApplyBoosts(results, boostPatterns);

        if (parameters.PenalizePatterns is { Count: > 0 })
        {
            var penalizePatterns = PatternBooster.ParsePatterns(string.Join(",", parameters.PenalizePatterns));
            PatternBooster.ApplyPenalties(results, penalizePatterns);
        }

        // 8. Normalize confidence
        ConfidenceNormalizer.NormalizeInPlace(results);

        return new SearchEngineResult(
            results,
            TotalDocumentsMatched: docResult.Documents.Count,
            TotalObjectsMatched: objects.Count,
            IndexerStatus: null
        );
    }

    /// <summary>
    /// Convert JIT search results to standard SearchResult format.
    /// </summary>
    private static IReadOnlyList<SearchResult> ConvertJitResults(
        JitObjectSearchResult jitResult,
        SearchParameters parameters)
    {
        var results = new List<SearchResult>();

        var objectsByDoc = jitResult.ScoredObjects
            .GroupBy(o => o.DocumentUri)
            .ToDictionary(g => g.Key, g => g.ToList());

        var processedDocs = new HashSet<string>();
        foreach (var doc in jitResult.SelectedDocuments)
        {
            processedDocs.Add(doc.DocumentUri);

            if (objectsByDoc.TryGetValue(doc.DocumentUri, out var docObjects) && docObjects.Count > 0)
            {
                var childObjects = new List<SearchResult>();
                var snippetObjects = docObjects.Take(FileGrouper.MaxSnippetsPerFile).ToList();
                var headlineObjects = docObjects.Skip(FileGrouper.MaxSnippetsPerFile).ToList();

                foreach (var obj in snippetObjects)
                {
                    childObjects.Add(new SearchResult(
                        Uri: obj.Uri,
                        Scope: SearchScope.Symbol,
                        Kind: obj.Kind,
                        Symbol: obj.Symbol,
                        Headline: obj.Headline,
                        Structure: obj.Structure,
                        Snippet: obj.Body,
                        LineStart: obj.LineStart,
                        LineEnd: obj.LineEnd,
                        Lang: obj.Lang,
                        SemanticType: obj.SemanticType,
                        RawScore: obj.FinalScore,
                        Confidence: obj.Confidence,
                        ChildObjects: null
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
                        ChildObjects: null
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
                    Confidence: CalculateDocumentConfidence(doc, jitResult.SelectedDocuments),
                    ChildObjects: childObjects
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
                    Confidence: CalculateDocumentConfidence(doc, jitResult.SelectedDocuments),
                    ChildObjects: null
                ));
            }
        }

        // Handle orphaned objects
        foreach (var (docUri, docObjects) in objectsByDoc)
        {
            if (processedDocs.Contains(docUri))
                continue;

            var snippetObjects = docObjects.Take(FileGrouper.MaxSnippetsPerFile).ToList();
            var headlineObjects = docObjects.Skip(FileGrouper.MaxSnippetsPerFile).ToList();

            foreach (var obj in snippetObjects)
            {
                results.Add(new SearchResult(
                    Uri: obj.Uri,
                    Scope: SearchScope.Symbol,
                    Kind: obj.Kind,
                    Symbol: obj.Symbol,
                    Headline: obj.Headline,
                    Structure: obj.Structure,
                    Snippet: obj.Body,
                    LineStart: obj.LineStart,
                    LineEnd: obj.LineEnd,
                    Lang: obj.Lang,
                    SemanticType: obj.SemanticType,
                    RawScore: obj.FinalScore,
                    Confidence: obj.Confidence,
                    ChildObjects: null
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
                    ChildObjects: null
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

    private static int CalculateDocumentConfidence(
        DocumentExpansionCandidate doc,
        IReadOnlyList<DocumentExpansionCandidate> allDocs)
    {
        if (allDocs.Count == 0)
            return 50;

        var maxScore = allDocs.Max(d => d.DocumentScore);
        var minScore = allDocs.Min(d => d.DocumentScore);
        var range = maxScore - minScore;

        return range <= 0 ? 50 : (int)(10 + 90 * (doc.DocumentScore - minScore) / range);
    }

    private static ObjectSearchConfig GetJitSearchConfig(Intent intent, int tokenBudget)
    {
        // Scale JIT embeddings based on token budget
        // Rough heuristic: ~50 tokens per object displayed, so budget/50 gives approximate object count
        // But we also want to search a bit more than we display for ranking quality
        var budgetScale = Math.Clamp(tokenBudget / 1500.0, 0.2, 1.5);

        return intent switch
        {
            Intent.Find => new ObjectSearchConfig
            {
                MinProbabilityMass = 0.85,
                MaxDocumentsToExpand = Math.Max(3, (int)(15 * budgetScale)),
                MinDocumentsToExpand = 3,
                MaxJitEmbeddings = Math.Max(5, (int)(30 * budgetScale)),
                JitEmbeddingThreshold = 0.15,
                MaxObjectsPerDocument = 50
            },
            Intent.Examine => new ObjectSearchConfig
            {
                MinProbabilityMass = 0.90,
                MaxDocumentsToExpand = Math.Max(2, (int)(10 * budgetScale)),
                MinDocumentsToExpand = 2,
                MaxJitEmbeddings = Math.Max(5, (int)(40 * budgetScale)),
                JitEmbeddingThreshold = 0.12,
                MaxObjectsPerDocument = 60
            },
            Intent.Explore => new ObjectSearchConfig
            {
                MinProbabilityMass = 0.75,
                MaxDocumentsToExpand = 0,
                MinDocumentsToExpand = 0,
                MaxJitEmbeddings = 0,
                JitEmbeddingThreshold = 1.0,
                MaxObjectsPerDocument = 0
            },
            // Understand uses Find-like behavior but with broader coverage for LLM synthesis
            Intent.Understand => new ObjectSearchConfig
            {
                MinProbabilityMass = 0.85,
                MaxDocumentsToExpand = Math.Max(3, (int)(12 * budgetScale)),
                MinDocumentsToExpand = 3,
                MaxJitEmbeddings = Math.Max(5, (int)(35 * budgetScale)),
                JitEmbeddingThreshold = 0.15,
                MaxObjectsPerDocument = 50
            },
            _ => new ObjectSearchConfig()
        };
    }
}
