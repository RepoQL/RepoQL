using System.Globalization;
using System.Text.RegularExpressions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RepoQL.ConsoleApp.Helpers;
using RepoQL.Contracts.Embeddings;
using RepoQL.Protocol;
using RepoQL.Xray.Search;

namespace RepoQL.ConsoleApp.Search;

/// <summary>
/// Enhanced object search service implementing the full algorithm:
/// 1. Query normalization (compile regexes, detect intent, precompute query embedding)
/// 2. Softmax document selection with intent-dependent temperature
/// 3. Cheap candidate scoring (name hits, regex hits, chunk overlap, type priors)
/// 4. JIT embedding planning based on expected value
/// 5. Final scoring with intent-dependent weights
/// </summary>
internal sealed class EnhancedObjectSearchService : IEnhancedObjectSearchService
{
    private readonly RepoQlClientProvider _clientProvider;
    private readonly IEmbeddingProvider? _embeddingProvider;
    private readonly ILogger<EnhancedObjectSearchService> _logger;

    public EnhancedObjectSearchService(
        RepoQlClientProvider clientProvider,
        IEmbeddingProvider? embeddingProvider = null,
        ILogger<EnhancedObjectSearchService>? logger = null)
    {
        _clientProvider = clientProvider ?? throw new ArgumentNullException(nameof(clientProvider));
        _embeddingProvider = embeddingProvider;
        _logger = logger ?? NullLogger<EnhancedObjectSearchService>.Instance;
    }

    /// <inheritdoc/>
    public async Task<EnhancedObjectSearchResult> SearchAsync(
        string? question,
        string? scope,
        string? boostPattern,
        string? penalizePattern,
        ObjectSearchConfig config,
        JitEmbeddingCache jitCache,
        CancellationToken cancellationToken)
    {
        var client = await _clientProvider.GetClientAsync(cancellationToken).ConfigureAwait(false);

        // Step 0: Normalize query - compile regexes, precompute query embedding, detect intent
        var signals = await NormalizeQueryAsync(question, cancellationToken).ConfigureAwait(false);

        // Step 1: Document selection via search
        var documentCandidates = await GetDocumentCandidatesAsync(
            client, signals, scope, boostPattern, penalizePattern, config, cancellationToken).ConfigureAwait(false);

        if (documentCandidates.Count == 0)
        {
            _logger.LogDebug("No document candidates found for query: {Query}", question);
            return new EnhancedObjectSearchResult([], [], signals);
        }

        // Apply softmax selection to get documents to expand
        var selectedDocs = SelectDocumentsViaSoftmax(documentCandidates, signals, config);

        _logger.LogDebug("Selected {Count} documents for expansion from {Total} candidates",
            selectedDocs.Count, documentCandidates.Count);

        // Step 2: Get cheap object candidates from selected documents
        var docUris = selectedDocs.Select(d => d.DocumentUri).ToList();
        var candidates = await GetObjectCandidatesAsync(
            client, docUris, signals, config, cancellationToken).ConfigureAwait(false);

        if (candidates.Count == 0)
        {
            _logger.LogDebug("No object candidates found in selected documents");
            return new EnhancedObjectSearchResult(selectedDocs, [], signals);
        }

        // Apply chunk overlap scores from document search
        ApplyChunkOverlapScores(candidates, selectedDocs);

        // Compute cheap aggregate scores
        ComputeCheapAggregateScores(candidates, config);

        // Step 3: JIT embedding planning
        var jitCandidates = PlanJitEmbeddings(candidates, config);

        _logger.LogDebug("Selected {Count} candidates for JIT embedding from {Total}",
            jitCandidates.Count, candidates.Count);

        // Step 4: Compute JIT embeddings
        if (jitCandidates.Count > 0 && signals.QueryEmbedding is not null)
        {
            await ComputeJitEmbeddingsAsync(jitCandidates, signals, jitCache, cancellationToken).ConfigureAwait(false);
        }

        // Step 5: Final scoring
        ComputeFinalScores(candidates, signals, config);

        // Sort by final score and assign confidence
        var sortedCandidates = candidates
            .OrderByDescending(c => c.FinalScore)
            .ToList();

        AssignConfidenceScores(sortedCandidates);

        return new EnhancedObjectSearchResult(selectedDocs, sortedCandidates, signals);
    }

    /// <summary>
    /// Normalize query: compile boost/negative regexes, detect intent, precompute query embedding.
    /// </summary>
    private async Task<NormalizedQuerySignals> NormalizeQueryAsync(string? question, CancellationToken cancellationToken)
    {
        var rawQuery = question?.Trim() ?? "";
        var intent = DetectQueryIntent(rawQuery);

        // Precompute query embedding if we have an embedding provider
        float[]? queryEmbedding = null;
        if (_embeddingProvider?.Enabled == true && !string.IsNullOrWhiteSpace(rawQuery))
        {
            queryEmbedding = await _embeddingProvider.EmbedAsync(rawQuery, cancellationToken).ConfigureAwait(false);
        }

        // Extract tokens for name matching
        var tokens = ExtractQueryTokens(rawQuery);

        // Build boost regex from tokens
        var boostRegex = tokens.Count > 0
            ? string.Join("|", tokens.Select(Regex.Escape))
            : "";

        // Compile boost patterns (individual token patterns for scoring)
        var boostPatterns = tokens
            .Select(t =>
            {
                try { return new Regex(Regex.Escape(t), RegexOptions.IgnoreCase | RegexOptions.Compiled); }
                catch { return null; }
            })
            .Where(r => r is not null)
            .Cast<Regex>()
            .ToList();

        // Temperature based on intent
        var temperature = ObjectSearchConfig.GetTemperature(intent);

        return new NormalizedQuerySignals
        {
            RawQuery = rawQuery,
            QueryEmbedding = queryEmbedding,
            BoostPatterns = boostPatterns,
            NegativePattern = null, // Can be extended to support negative patterns
            QueryTokensLower = tokens,
            BoostRegex = boostRegex,
            DetectedIntent = intent,
            SoftmaxTemperature = temperature
        };
    }

    /// <summary>
    /// Detect query intent: Semantic (natural language), Symbol (code identifier), or Hybrid.
    /// </summary>
    private static QueryIntent DetectQueryIntent(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return QueryIntent.Hybrid;

        // Symbol indicators: PascalCase, camelCase, snake_case, dots
        var hasSymbolIndicators =
            Regex.IsMatch(query, @"[A-Z][a-z]+[A-Z]") || // PascalCase
            Regex.IsMatch(query, @"[a-z]+[A-Z]") ||       // camelCase
            query.Contains('_') ||                         // snake_case
            query.Contains('.') ||                         // dotted path
            query.Contains("::");                          // namespace separator

        // Semantic indicators: question words, spaces, common words
        var hasSemanticIndicators =
            Regex.IsMatch(query, @"^(how|what|where|when|why|which|who|is|are|does|do|can)\b", RegexOptions.IgnoreCase) ||
            query.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length >= 3;

        if (hasSymbolIndicators && !hasSemanticIndicators)
            return QueryIntent.Symbol;
        if (hasSemanticIndicators && !hasSymbolIndicators)
            return QueryIntent.Semantic;

        return QueryIntent.Hybrid;
    }

    /// <summary>
    /// Extract meaningful tokens from query for name matching.
    /// </summary>
    private static IReadOnlySet<string> ExtractQueryTokens(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return new HashSet<string>();

        // Split on whitespace and punctuation, keep meaningful tokens
        var tokens = Regex.Split(query, @"[\s\.\,\;\:\?\!\(\)\[\]\{\}]+")
            .Where(t => t.Length >= 2)
            .Select(t => t.ToLowerInvariant())
            .ToHashSet();

        // Also split PascalCase/camelCase into component words
        var additionalTokens = new HashSet<string>();
        foreach (var token in tokens.ToList())
        {
            var parts = Regex.Split(token, @"(?<!^)(?=[A-Z])|_")
                .Where(p => p.Length >= 2)
                .Select(p => p.ToLowerInvariant());
            foreach (var part in parts)
                additionalTokens.Add(part);
        }

        foreach (var t in additionalTokens)
            tokens.Add(t);

        return tokens;
    }

    /// <summary>
    /// Get document candidates using search macro.
    /// </summary>
    private async Task<List<DocumentExpansionCandidate>> GetDocumentCandidatesAsync(
        IRepoQlClient client,
        NormalizedQuerySignals signals,
        string? scope,
        string? boostPattern,
        string? penalizePattern,
        ObjectSearchConfig config,
        CancellationToken cancellationToken)
    {
        // Build scope LIKE pattern for search
        var scopeLike = !string.IsNullOrWhiteSpace(scope) && scope != "%"
            ? ConvertGlobToLike(scope)
            : null;

        // Build parameter placeholders for patterns
        var paramIndex = 1;
        var keywordsParam = $"${paramIndex++}";
        var scopeParam = scopeLike != null ? $"${paramIndex++}" : "NULL";
        var boostParam = !string.IsNullOrWhiteSpace(boostPattern) ? $"${paramIndex++}" : "NULL";
        var penalizeParam = !string.IsNullOrWhiteSpace(penalizePattern) ? $"${paramIndex++}" : "NULL";

        // Use search for document selection with boost/penalize patterns
        var sql = $"""
            SELECT
                uri,
                headline,
                structure,
                source,
                sem_score,
                bm25_score,
                struct_mentions,
                body_mentions,
                deranked,
                score
            FROM search(
                {keywordsParam},
                scope := {scopeParam},
                boost_pattern := {boostParam},
                negative_pattern := {penalizeParam},
                k := {config.MaxDocumentsToExpand * 3}
            )
            ORDER BY score DESC
            LIMIT {config.MaxDocumentsToExpand * 2}
            """;

        var parameters = new List<object?> { signals.RawQuery };
        if (scopeLike != null)
            parameters.Add(scopeLike);
        if (!string.IsNullOrWhiteSpace(boostPattern))
            parameters.Add(boostPattern);
        if (!string.IsNullOrWhiteSpace(penalizePattern))
            parameters.Add(penalizePattern);

        try
        {
            var response = await client.ExecuteRawQueryAsync(
                sql, parameters.ToArray(), null, cancellationToken).ConfigureAwait(false);

            var candidates = new List<DocumentExpansionCandidate>();
            foreach (var row in response.Rows)
            {
                var values = row.Values;
                if (values.Count < 10) continue;

                var uri = ExtractString(values[0]);
                if (string.IsNullOrWhiteSpace(uri)) continue;

                candidates.Add(new DocumentExpansionCandidate(
                    DocumentUri: uri,
                    DocumentScore: ExtractDouble(values[9]) ?? 0.0,
                    SoftmaxProbability: 0.0, // Computed later
                    CumulativeProbability: 0.0, // Computed later
                    Headline: ExtractString(values[1]),
                    Structure: ExtractString(values[2]),
                    Lang: null, // Not returned by search
                    SemanticType: null, // Not returned by search
                    Source: ExtractString(values[3]) ?? "unknown",
                    SemanticScore: ExtractDouble(values[4]) ?? 0.0,
                    Bm25Score: ExtractDouble(values[5]) ?? 0.0,
                    StructMentions: ExtractInt(values[6]) ?? 0,
                    BodyMentions: ExtractInt(values[7]) ?? 0,
                    HighScoringChunks: [] // Filled later if needed
                ));
            }

            return candidates;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "search query failed, falling back to empty results");
            return [];
        }
    }

    /// <summary>
    /// Apply softmax selection to get documents to expand based on probability mass.
    /// </summary>
    private List<DocumentExpansionCandidate> SelectDocumentsViaSoftmax(
        List<DocumentExpansionCandidate> candidates,
        NormalizedQuerySignals signals,
        ObjectSearchConfig config)
    {
        if (candidates.Count == 0)
            return [];

        var temperature = signals.SoftmaxTemperature;

        // Compute softmax probabilities
        var maxScore = candidates.Max(c => c.DocumentScore);
        var expScores = candidates.Select(c => Math.Exp((c.DocumentScore - maxScore) / temperature)).ToList();
        var sumExp = expScores.Sum();

        var withProbabilities = new List<DocumentExpansionCandidate>();
        double cumulative = 0.0;

        for (var i = 0; i < candidates.Count; i++)
        {
            var prob = expScores[i] / sumExp;
            cumulative += prob;

            withProbabilities.Add(candidates[i] with
            {
                SoftmaxProbability = prob,
                CumulativeProbability = cumulative
            });
        }

        // Select until we hit probability mass threshold or max documents
        var selected = new List<DocumentExpansionCandidate>();
        foreach (var doc in withProbabilities)
        {
            selected.Add(doc);

            // Stop if we've captured enough probability mass and have minimum docs
            if (doc.CumulativeProbability >= config.MinProbabilityMass &&
                selected.Count >= config.MinDocumentsToExpand)
                break;

            // Hard cap on documents
            if (selected.Count >= config.MaxDocumentsToExpand)
                break;
        }

        // Ensure we have at least minimum documents if available
        while (selected.Count < config.MinDocumentsToExpand && selected.Count < withProbabilities.Count)
        {
            selected.Add(withProbabilities[selected.Count]);
        }

        return selected;
    }

    /// <summary>
    /// Get object candidates from selected documents using hybrid_object_candidates macro.
    /// </summary>
    private async Task<List<ObjectCandidate>> GetObjectCandidatesAsync(
        IRepoQlClient client,
        IReadOnlyList<string> documentUris,
        NormalizedQuerySignals signals,
        ObjectSearchConfig config,
        CancellationToken cancellationToken)
    {
        if (documentUris.Count == 0)
            return [];

        // Build URI array literal for DuckDB
        var uriArray = "[" + string.Join(",", documentUris.Select(u => $"'{EscapeSql(u)}'")) + "]";

        var sql = $"""
            SELECT
                node_id,
                uri,
                document_uri,
                kind,
                symbol,
                headline,
                structure,
                body,
                line_start,
                line_end,
                lang,
                semantic_type,
                name_hit_score,
                regex_mentions
            FROM hybrid_object_candidates(
                {uriArray}::VARCHAR[],
                keywords := $1,
                max_per_doc := {config.MaxObjectsPerDocument}
            )
            """;

        try
        {
            var response = await client.ExecuteRawQueryAsync(
                sql, [signals.RawQuery], null, cancellationToken).ConfigureAwait(false);

            var candidates = new List<ObjectCandidate>();
            foreach (var row in response.Rows)
            {
                var values = row.Values;
                if (values.Count < 14) continue;

                var nodeId = ExtractString(values[0]);
                var uri = ExtractString(values[1]);
                var docUri = ExtractString(values[2]);
                var kind = ExtractString(values[3]);

                if (string.IsNullOrWhiteSpace(nodeId) || string.IsNullOrWhiteSpace(uri) ||
                    string.IsNullOrWhiteSpace(docUri) || string.IsNullOrWhiteSpace(kind))
                    continue;

                var candidate = new ObjectCandidate
                {
                    NodeId = nodeId,
                    Uri = uri,
                    DocumentUri = docUri,
                    Kind = kind,
                    Symbol = ExtractString(values[4]),
                    Headline = ExtractString(values[5]),
                    Structure = ExtractString(values[6]),
                    Body = ExtractString(values[7]),
                    LineStart = ExtractInt(values[8]) ?? 1,
                    LineEnd = ExtractInt(values[9]) ?? 1,
                    StartByte = null, // Not returned by macro
                    EndByte = null, // Not returned by macro
                    Lang = ExtractString(values[10]),
                    SemanticType = ExtractString(values[11]),
                    NameHitScore = ExtractDouble(values[12]) ?? 0.0,
                    RegexHitScore = (ExtractInt(values[13]) ?? 0) * 0.1 // Normalize regex mentions
                };

                // Apply type prior
                candidate.TypePriorScore = config.TypePriors.TryGetValue(kind, out var prior)
                    ? prior
                    : config.DefaultTypePrior;

                candidates.Add(candidate);
            }

            return candidates;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "hybrid_object_candidates query failed");
            return [];
        }
    }

    /// <summary>
    /// Apply chunk overlap scores from document search results.
    /// </summary>
    private void ApplyChunkOverlapScores(
        List<ObjectCandidate> candidates,
        List<DocumentExpansionCandidate> documents)
    {
        // Build lookup for high-scoring chunks by document
        var chunksByDoc = documents
            .Where(d => d.HighScoringChunks.Count > 0)
            .ToDictionary(d => d.DocumentUri, d => d.HighScoringChunks);

        foreach (var candidate in candidates)
        {
            if (!chunksByDoc.TryGetValue(candidate.DocumentUri, out var chunks))
            {
                // No chunks - use document score as proxy
                var doc = documents.FirstOrDefault(d => d.DocumentUri == candidate.DocumentUri);
                candidate.ChunkOverlapScore = doc?.SemanticScore ?? 0.0;
                continue;
            }

            // Find best chunk overlap
            var bestOverlap = 0.0;
            foreach (var chunk in chunks)
            {
                var overlap = CalculateOverlap(candidate.LineStart, candidate.LineEnd, chunk.StartLine, chunk.EndLine);
                var weightedOverlap = overlap * chunk.Score;
                if (weightedOverlap > bestOverlap)
                    bestOverlap = weightedOverlap;
            }

            candidate.ChunkOverlapScore = bestOverlap;
        }
    }

    /// <summary>
    /// Calculate overlap ratio between object and chunk line ranges.
    /// </summary>
    private static double CalculateOverlap(int objStart, int objEnd, int chunkStart, int chunkEnd)
    {
        var overlapStart = Math.Max(objStart, chunkStart);
        var overlapEnd = Math.Min(objEnd, chunkEnd);

        if (overlapStart > overlapEnd)
            return 0.0;

        var overlapLength = overlapEnd - overlapStart + 1;
        var objLength = objEnd - objStart + 1;

        return Math.Min(1.0, (double)overlapLength / objLength);
    }

    /// <summary>
    /// Compute cheap aggregate scores for all candidates.
    /// </summary>
    private void ComputeCheapAggregateScores(List<ObjectCandidate> candidates, ObjectSearchConfig config)
    {
        var weights = config.CheapWeights;

        foreach (var candidate in candidates)
        {
            candidate.CheapAggregateScore =
                weights.NameHit * candidate.NameHitScore +
                weights.ChunkOverlap * candidate.ChunkOverlapScore +
                weights.RegexHit * Math.Min(1.0, candidate.RegexHitScore) +
                weights.TypePrior * (candidate.TypePriorScore - 1.0); // Center type prior around 0
        }
    }

    /// <summary>
    /// Plan JIT embeddings based on expected value.
    /// </summary>
    private List<ObjectCandidate> PlanJitEmbeddings(List<ObjectCandidate> candidates, ObjectSearchConfig config)
    {
        if (candidates.Count == 0 || _embeddingProvider?.Enabled != true)
            return [];

        // Sort by cheap score to get ranks
        var sorted = candidates.OrderByDescending(c => c.CheapAggregateScore).ToList();
        var maxCheapScore = sorted.First().CheapAggregateScore;
        if (maxCheapScore <= 0) maxCheapScore = 1.0; // Avoid division by zero

        // Compute expected value for each candidate
        for (var i = 0; i < sorted.Count; i++)
        {
            var candidate = sorted[i];
            var rank = i + 1;

            // Uncertainty: how unsure we are about this candidate's quality
            // Higher cheap score = lower uncertainty
            candidate.Uncertainty = 1.0 - (candidate.CheapAggregateScore / maxCheapScore);

            // Impact: how much would an embedding change the ranking?
            // Higher rank (lower position) = lower potential impact
            candidate.ExpectedImpact = 1.0 / Math.Sqrt(rank);

            // Bonus: candidates with high chunk overlap but low name hit are likely semantic matches
            var likelySemantic = candidate.ChunkOverlapScore > 0.5 && candidate.NameHitScore < 0.3;
            var bonus = likelySemantic ? 1.5 : 1.0;

            candidate.ExpectedValue = candidate.Uncertainty * candidate.ExpectedImpact * bonus;
        }

        // Select candidates above threshold, up to max
        var selected = sorted
            .Where(c => c.ExpectedValue >= config.JitEmbeddingThreshold)
            .OrderByDescending(c => c.ExpectedValue)
            .Take(config.MaxJitEmbeddings)
            .ToList();

        foreach (var c in selected)
            c.SelectedForJitEmbedding = true;

        return selected;
    }

    /// <summary>
    /// Compute JIT embeddings for selected candidates.
    /// </summary>
    private async Task ComputeJitEmbeddingsAsync(
        List<ObjectCandidate> candidates,
        NormalizedQuerySignals signals,
        JitEmbeddingCache cache,
        CancellationToken cancellationToken)
    {
        if (_embeddingProvider?.Enabled != true || signals.QueryEmbedding is null)
            return;

        // Prepare texts for embedding (use headline + structure for efficiency)
        var texts = candidates.Select(c =>
            $"{c.Headline ?? ""} {c.Structure ?? ""}".Trim()).ToList();

        // Use cache for batch embedding
        var embeddings = cache.GetOrComputeBatch(
            texts,
            batch => _embeddingProvider.EmbedBatchAsync(batch, cancellationToken).GetAwaiter().GetResult());

        // Compute semantic scores
        for (var i = 0; i < candidates.Count; i++)
        {
            candidates[i].JitEmbedding = embeddings[i];

            if (embeddings[i] is not null)
            {
                candidates[i].SemanticScore = CosineSimilarity(signals.QueryEmbedding, embeddings[i]!);
            }
        }
    }

    /// <summary>
    /// Compute final scores with intent-dependent weights.
    /// </summary>
    private void ComputeFinalScores(
        List<ObjectCandidate> candidates,
        NormalizedQuerySignals signals,
        ObjectSearchConfig config)
    {
        var weights = ObjectSearchConfig.GetFinalWeights(signals.DetectedIntent);

        foreach (var candidate in candidates)
        {
            // If we have a JIT embedding, use it; otherwise fall back to cheap aggregate
            var semanticComponent = candidate.SelectedForJitEmbedding && candidate.JitEmbedding is not null
                ? candidate.SemanticScore
                : candidate.ChunkOverlapScore; // Use chunk overlap as semantic proxy

            candidate.FinalScore =
                weights.Semantic * semanticComponent +
                weights.Name * candidate.NameHitScore +
                weights.Regex * Math.Min(1.0, candidate.RegexHitScore) +
                weights.Type * (candidate.TypePriorScore / config.DefaultTypePrior); // Normalize to ~1.0
        }
    }

    /// <summary>
    /// Assign confidence scores (0-100) based on relative ranking.
    /// </summary>
    private void AssignConfidenceScores(List<ObjectCandidate> sortedCandidates)
    {
        if (sortedCandidates.Count == 0)
            return;

        var maxScore = sortedCandidates.First().FinalScore;
        var minScore = sortedCandidates.Last().FinalScore;
        var range = maxScore - minScore;

        foreach (var candidate in sortedCandidates)
        {
            // Scale to 0-100, with minimum of 10
            candidate.Confidence = range > 0
                ? (int)(10 + 90 * (candidate.FinalScore - minScore) / range)
                : 50;
        }
    }

    /// <summary>
    /// Compute cosine similarity between two vectors.
    /// </summary>
    private static double CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0.0;

        double dot = 0, normA = 0, normB = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        var denom = Math.Sqrt(normA) * Math.Sqrt(normB);
        return denom > 0 ? dot / denom : 0.0;
    }

    /// <summary>
    /// Convert glob pattern to SQL LIKE pattern.
    /// </summary>
    private static string ConvertGlobToLike(string glob)
    {
        // Simple conversion: ** -> %, * -> %, ? -> _
        return glob
            .Replace("**", "%")
            .Replace("*", "%")
            .Replace("?", "_");
    }

    private static string EscapeSql(string value) => value.Replace("'", "''");

    private static string? ExtractString(Value value) =>
        value.KindCase switch
        {
            Value.KindOneofCase.StringValue => value.StringValue,
            Value.KindOneofCase.NumberValue => value.NumberValue.ToString(CultureInfo.InvariantCulture),
            Value.KindOneofCase.BoolValue => value.BoolValue ? "true" : "false",
            _ => null
        };

    private static int? ExtractInt(Value value)
    {
        var str = ExtractString(value);
        return !string.IsNullOrWhiteSpace(str) && int.TryParse(str, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static double? ExtractDouble(Value value)
    {
        if (value.KindCase == Value.KindOneofCase.NumberValue)
            return value.NumberValue;

        var str = ExtractString(value);
        return !string.IsNullOrWhiteSpace(str) && double.TryParse(str, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }
}

// IEnhancedObjectSearchService and EnhancedObjectSearchResult are defined in RepoQL.Xray.Search
