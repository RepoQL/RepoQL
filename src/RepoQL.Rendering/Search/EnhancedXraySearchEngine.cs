namespace RepoQL.Rendering.Search;

/// <summary>
/// Interface for enhanced object search service.
/// </summary>
public interface IEnhancedObjectSearchService
{
    /// <summary>
    /// Execute enhanced object search with softmax document selection and JIT embeddings.
    /// </summary>
    /// <param name="question">Search query.</param>
    /// <param name="scope">Scope filter (glob pattern).</param>
    /// <param name="boostPattern">Regex patterns to boost matches (comma-separated).</param>
    /// <param name="penalizePattern">Regex patterns to de-rank matches (comma-separated).</param>
    /// <param name="config">Search configuration.</param>
    /// <param name="jitCache">JIT embedding cache.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<EnhancedObjectSearchResult> SearchAsync(
        string? question,
        string? scope,
        string? boostPattern,
        string? penalizePattern,
        ObjectSearchConfig config,
        JitEmbeddingCache jitCache,
        CancellationToken cancellationToken);
}

/// <summary>
/// Result from enhanced object search.
/// </summary>
public record EnhancedObjectSearchResult(
    IReadOnlyList<DocumentExpansionCandidate> SelectedDocuments,
    IReadOnlyList<ObjectCandidate> ScoredObjects,
    NormalizedQuerySignals QuerySignals
);

/// <summary>
/// Enhanced interface for xray search engine that supports enhanced object search.
/// </summary>
public interface IEnhancedXraySearchEngine : IXraySearchEngine
{
    /// <summary>
    /// Execute a search using the enhanced object search service when available.
    /// Falls back to standard search if enhanced service is not available.
    /// </summary>
    Task<SearchEngineResult> SearchEnhancedAsync(
        SearchParameters parameters,
        IEnhancedObjectSearchService? enhancedService,
        JitEmbeddingCache jitCache,
        CancellationToken cancellationToken);
}

/// <summary>
/// Extended xray search engine that can use enhanced object search.
/// Maintains session-level JIT embedding cache and falls back to standard search when enhanced service is unavailable.
/// </summary>
public sealed class EnhancedXraySearchEngine : IEnhancedXraySearchEngine
{
    private readonly IDocumentSearchService _documentSearch;
    private readonly IObjectSearchService _objectSearch;

    public EnhancedXraySearchEngine(
        IDocumentSearchService documentSearch,
        IObjectSearchService objectSearch)
    {
        _documentSearch = documentSearch ?? throw new ArgumentNullException(nameof(documentSearch));
        _objectSearch = objectSearch ?? throw new ArgumentNullException(nameof(objectSearch));
    }

    /// <inheritdoc/>
    public async Task<SearchEngineResult> SearchAsync(
        SearchParameters parameters,
        CancellationToken cancellationToken)
    {
        // Standard search path - delegates to original implementation logic
        return await SearchStandardAsync(parameters, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<SearchEngineResult> SearchEnhancedAsync(
        SearchParameters parameters,
        IEnhancedObjectSearchService? enhancedService,
        JitEmbeddingCache jitCache,
        CancellationToken cancellationToken)
    {
        // Decide whether to use enhanced search
        var useEnhanced = ShouldUseEnhancedSearch(parameters, enhancedService);

        if (useEnhanced)
        {
            return await SearchWithEnhancedServiceAsync(
                parameters,
                enhancedService!,
                jitCache,
                cancellationToken).ConfigureAwait(false);
        }

        // Fall back to standard search
        return await SearchStandardAsync(parameters, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Determine whether to use enhanced search based on parameters and availability.
    /// Enhanced search is used when:
    /// - Enhanced service is available
    /// - Intent is Find or Read
    /// - Question is provided (required for enhanced search)
    /// </summary>
    private static bool ShouldUseEnhancedSearch(
        SearchParameters parameters,
        IEnhancedObjectSearchService? enhancedService)
    {
        if (enhancedService is null)
            return false;

        if (string.IsNullOrWhiteSpace(parameters.Question))
            return false;

        // Enhanced search is most beneficial for Find/Read intents
        return parameters.Intent is Intent.Find or Intent.Examine;
    }

    /// <summary>
    /// Execute search using the enhanced object search service.
    /// Groups objects under their parent documents in hierarchical structure.
    /// </summary>
    private async Task<SearchEngineResult> SearchWithEnhancedServiceAsync(
        SearchParameters parameters,
        IEnhancedObjectSearchService enhancedService,
        JitEmbeddingCache jitCache,
        CancellationToken cancellationToken)
    {
        // Configure enhanced search based on intent
        var config = GetEnhancedSearchConfig(parameters.Intent);

        // Build pattern strings for hybrid_search
        var boostPattern = parameters.Patterns.Count > 0
            ? string.Join(",", parameters.Patterns)
            : null;
        var penalizePattern = parameters.PenalizePatterns is { Count: > 0 }
            ? string.Join(",", parameters.PenalizePatterns)
            : null;

        // Execute enhanced object search
        var enhancedResult = await enhancedService.SearchAsync(
            parameters.Question,
            parameters.Scope,
            boostPattern,
            penalizePattern,
            config,
            jitCache,
            cancellationToken).ConfigureAwait(false);

        // Convert enhanced results to standard format
        var results = ConvertEnhancedResults(enhancedResult, parameters);

        return new SearchEngineResult(
            results,
            TotalDocumentsMatched: enhancedResult.SelectedDocuments.Count,
            TotalObjectsMatched: enhancedResult.ScoredObjects.Count,
            IndexerStatus: null // Populated by caller (XrayTool) with timing info
        );
    }

    /// <summary>
    /// Standard search implementation (original logic from XraySearchEngine).
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

        // 7. Apply pattern boosts
        var patterns = PatternBooster.ParsePatterns(string.Join(",", parameters.Patterns));
        PatternBooster.ApplyBoosts(results, patterns);

        // 8. Normalize confidence
        ConfidenceNormalizer.NormalizeInPlace(results);

        return new SearchEngineResult(
            results,
            TotalDocumentsMatched: docResult.Documents.Count,
            TotalObjectsMatched: objects.Count,
            IndexerStatus: null // Populated by caller (XrayTool) with timing info
        );
    }

    /// <summary>
    /// Convert enhanced search results to standard SearchResult format with hierarchical structure.
    /// Groups objects under their parent documents.
    /// </summary>
    private static IReadOnlyList<SearchResult> ConvertEnhancedResults(
        EnhancedObjectSearchResult enhancedResult,
        SearchParameters parameters)
    {
        var results = new List<SearchResult>();

        // Group objects by document
        var objectsByDoc = enhancedResult.ScoredObjects
            .GroupBy(o => o.DocumentUri)
            .ToDictionary(g => g.Key, g => g.ToList());

        // Create document results with child objects
        var processedDocs = new HashSet<string>();
        foreach (var doc in enhancedResult.SelectedDocuments)
        {
            processedDocs.Add(doc.DocumentUri);

            if (objectsByDoc.TryGetValue(doc.DocumentUri, out var docObjects) && docObjects.Count > 0)
            {
                // Document has objects - create hierarchical structure
                var childObjects = new List<SearchResult>();

                // Limit snippet objects per file
                var snippetObjects = docObjects.Take(FileGrouper.MaxSnippetsPerFile).ToList();
                var headlineObjects = docObjects.Skip(FileGrouper.MaxSnippetsPerFile).ToList();

                // Add snippet objects (with body/snippet)
                foreach (var obj in snippetObjects)
                {
                    childObjects.Add(new SearchResult(
                        Uri: obj.Uri,
                        Scope: SearchScope.Symbol,
                        Kind: obj.Kind,
                        Symbol: obj.Symbol,
                        Headline: obj.Headline,
                        Structure: obj.Structure,
                        Snippet: obj.Body, // Use body as snippet
                        LineStart: obj.LineStart,
                        LineEnd: obj.LineEnd,
                        Lang: obj.Lang,
                        SemanticType: obj.SemanticType,
                        RawScore: obj.FinalScore,
                        Confidence: obj.Confidence,
                        ChildObjects: null
                    ));
                }

                // Add headline objects (without snippet)
                foreach (var obj in headlineObjects)
                {
                    childObjects.Add(new SearchResult(
                        Uri: obj.Uri,
                        Scope: SearchScope.Symbol,
                        Kind: obj.Kind,
                        Symbol: obj.Symbol,
                        Headline: obj.Headline,
                        Structure: obj.Structure,
                        Snippet: null, // No snippet for headline-only
                        LineStart: obj.LineStart,
                        LineEnd: obj.LineEnd,
                        Lang: obj.Lang,
                        SemanticType: obj.SemanticType,
                        RawScore: obj.FinalScore,
                        Confidence: obj.Confidence,
                        ChildObjects: null
                    ));
                }

                // Add document with child objects
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
                    Confidence: CalculateDocumentConfidence(doc, enhancedResult.SelectedDocuments),
                    ChildObjects: childObjects
                ));
            }
            else
            {
                // Document with no objects - show as standalone
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
                    Confidence: CalculateDocumentConfidence(doc, enhancedResult.SelectedDocuments),
                    ChildObjects: null
                ));
            }
        }

        // Handle orphaned objects (objects whose documents weren't in selected documents)
        foreach (var (docUri, docObjects) in objectsByDoc)
        {
            if (processedDocs.Contains(docUri))
                continue;

            // Show orphaned objects without parent document
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

        // Apply pattern boosts if patterns provided
        if (parameters.Patterns.Count > 0)
        {
            var patterns = PatternBooster.ParsePatterns(string.Join(",", parameters.Patterns));
            PatternBooster.ApplyBoosts(results, patterns);
        }

        // Apply pattern penalties if penalize patterns provided
        if (parameters.PenalizePatterns is { Count: > 0 })
        {
            var penalizePatterns = PatternBooster.ParsePatterns(string.Join(",", parameters.PenalizePatterns));
            PatternBooster.ApplyPenalties(results, penalizePatterns);
        }

        return results;
    }

    /// <summary>
    /// Calculate confidence score for a document based on its position in selected documents.
    /// </summary>
    private static int CalculateDocumentConfidence(
        DocumentExpansionCandidate doc,
        IReadOnlyList<DocumentExpansionCandidate> allDocs)
    {
        if (allDocs.Count == 0)
            return 50;

        var maxScore = allDocs.Max(d => d.DocumentScore);
        var minScore = allDocs.Min(d => d.DocumentScore);
        var range = maxScore - minScore;

        if (range <= 0)
            return 50;

        // Scale to 10-100 based on relative score
        return (int)(10 + 90 * (doc.DocumentScore - minScore) / range);
    }

    /// <summary>
    /// Get enhanced search configuration based on intent.
    /// </summary>
    private static ObjectSearchConfig GetEnhancedSearchConfig(Intent intent)
    {
        return intent switch
        {
            Intent.Find => new ObjectSearchConfig
            {
                MinProbabilityMass = 0.85,
                MaxDocumentsToExpand = 15,
                MinDocumentsToExpand = 3,
                MaxJitEmbeddings = 30,
                JitEmbeddingThreshold = 0.15,
                MaxObjectsPerDocument = 50
            },
            Intent.Examine => new ObjectSearchConfig
            {
                MinProbabilityMass = 0.90,
                MaxDocumentsToExpand = 10,
                MinDocumentsToExpand = 2,
                MaxJitEmbeddings = 40,
                JitEmbeddingThreshold = 0.12,
                MaxObjectsPerDocument = 60
            },
            Intent.Explore => new ObjectSearchConfig
            {
                MinProbabilityMass = 0.75,
                MaxDocumentsToExpand = 20,
                MinDocumentsToExpand = 5,
                MaxJitEmbeddings = 20,
                JitEmbeddingThreshold = 0.20,
                MaxObjectsPerDocument = 40
            },
            _ => new ObjectSearchConfig() // Use defaults
        };
    }
}
