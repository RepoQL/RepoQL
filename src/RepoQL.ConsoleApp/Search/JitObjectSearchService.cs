using System.Data;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RepoQL.Contracts.Embeddings;
using RepoQL.Data.DuckDB;
using RepoQL.Explore.Search;

namespace RepoQL.ConsoleApp.Search;

/// <summary>
/// JIT (Just-In-Time) object search service implementing the full algorithm:
/// 1. Query normalization (compile regexes, detect intent, precompute query embedding)
/// 2. Softmax document selection with intent-dependent temperature
/// 3. Cheap candidate scoring (name hits, regex hits, chunk overlap, type priors)
/// 4. JIT embedding planning based on expected value
/// 5. Final scoring with intent-dependent weights
///
/// Uses local ONNX to compute both query and object embeddings at search time,
/// ensuring self-consistent similarity comparisons.
/// </summary>
internal sealed class JitObjectSearchService : IJitObjectSearchService
{
    private readonly DuckDbDataStore _store;
    private readonly IEmbeddingProvider? _embeddingProvider;
    private readonly ILogger<JitObjectSearchService> _logger;

    public JitObjectSearchService(
        DuckDbDataStore store,
        [FromKeyedServices("local")] IEmbeddingProvider? embeddingProvider = null,
        ILogger<JitObjectSearchService>? logger = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _embeddingProvider = embeddingProvider;
        _logger = logger ?? NullLogger<JitObjectSearchService>.Instance;
    }

    /// <inheritdoc/>
    public async Task<JitObjectSearchResult> SearchAsync(
        string? question,
        string? scope,
        string? boostPattern,
        string? penalizePattern,
        ObjectSearchConfig config,
        JitEmbeddingCache jitCache,
        CancellationToken cancellationToken)
    {
        // Step 0: Normalize query - compile regexes, precompute query embedding, detect intent
        var signals = await NormalizeQueryAsync(question, cancellationToken).ConfigureAwait(false);

        // Step 1: Document selection via search
        var documentCandidates = GetDocumentCandidates(
            signals, scope, boostPattern, penalizePattern, config);

        if (documentCandidates.Count == 0)
        {
            _logger.LogDebug("No document candidates found for query: {Query}", question);
            return new JitObjectSearchResult([], [], signals);
        }

        // When document expansion is disabled, return document-level results only
        if (config.MaxDocumentsToExpand == 0)
        {
            _logger.LogDebug("Document expansion disabled, returning {Count} document candidates", documentCandidates.Count);
            return new JitObjectSearchResult(documentCandidates, [], signals);
        }

        // Apply softmax selection to get documents to expand
        var selectedDocs = SelectDocumentsViaSoftmax(documentCandidates, signals, config);

        _logger.LogDebug("Selected {Count} documents for expansion from {Total} candidates",
            selectedDocs.Count, documentCandidates.Count);

        // Step 1.5: Get chunk scores for selected documents
        var chunksByDoc = GetChunkScores(selectedDocs.Select(d => d.DocumentUri), signals);

        // Update documents with their high-scoring chunks
        selectedDocs = selectedDocs.Select(d => d with
        {
            HighScoringChunks = chunksByDoc.TryGetValue(d.DocumentUri, out var chunks)
                ? chunks
                : []
        }).ToList();

        _logger.LogDebug("Found {ChunkCount} high-scoring chunks across {DocCount} documents",
            chunksByDoc.Values.Sum(c => c.Count), chunksByDoc.Count);

        // Step 2: Get object candidates - prioritize those near high-scoring chunks
        var docUris = selectedDocs.Select(d => d.DocumentUri).ToList();
        var candidates = GetObjectCandidatesNearChunks(docUris, chunksByDoc, signals, config);

        if (candidates.Count == 0)
        {
            _logger.LogDebug("No object candidates found in selected documents");
            return new JitObjectSearchResult(selectedDocs, [], signals);
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

        // Populate actual source code snippets for top results
        // FileGrouper.MaxSnippetsPerFile * MaxDocumentsToExpand gives rough upper bound
        PopulateSnippets(sortedCandidates, config.MaxDocumentsToExpand * 5);

        return new JitObjectSearchResult(selectedDocs, sortedCandidates, signals);
    }

    /// <summary>
    /// Normalize query: compile boost/negative regexes, detect intent, precompute query embedding.
    /// </summary>
    private async Task<NormalizedQuerySignals> NormalizeQueryAsync(string? question, CancellationToken cancellationToken)
    {
        var rawQuery = question?.Trim() ?? "";
        var intent = DetectQueryIntent(rawQuery);

        // Precompute query embedding if we have an embedding provider
        // Use EmbedQueryAsync for search queries (E5 models prepend "query: " prefix)
        float[]? queryEmbedding = null;
        if (_embeddingProvider?.Enabled == true && !string.IsNullOrWhiteSpace(rawQuery))
        {
            queryEmbedding = await _embeddingProvider.EmbedQueryAsync(rawQuery, cancellationToken).ConfigureAwait(false);
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
    private List<DocumentExpansionCandidate> GetDocumentCandidates(
        NormalizedQuerySignals signals,
        string? scope,
        string? boostPattern,
        string? penalizePattern,
        ObjectSearchConfig config)
    {
        // Build scope LIKE pattern for search
        var scopeLike = !string.IsNullOrWhiteSpace(scope) && scope != "%"
            ? ConvertGlobToLike(scope)
            : null;

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
                {EscapeSqlString(signals.RawQuery)},
                scope := {(scopeLike != null ? EscapeSqlString(scopeLike) : "NULL")},
                boost_pattern := {(!string.IsNullOrWhiteSpace(boostPattern) ? EscapeSqlString(boostPattern) : "NULL")},
                negative_pattern := {(!string.IsNullOrWhiteSpace(penalizePattern) ? EscapeSqlString(penalizePattern) : "NULL")},
                k := {config.MaxDocumentsToExpand * 3}
            )
            ORDER BY score DESC
            LIMIT {config.MaxDocumentsToExpand * 2}
            """;

        try
        {
            return _store.Read(sql, r => new DocumentExpansionCandidate(
                DocumentUri: r.GetString(0),
                DocumentScore: r.IsDBNull(9) ? 0.0 : r.GetDouble(9),
                SoftmaxProbability: 0.0, // Computed later
                CumulativeProbability: 0.0, // Computed later
                Headline: r.IsDBNull(1) ? null : r.GetString(1),
                Structure: r.IsDBNull(2) ? null : r.GetString(2),
                Lang: null, // Not returned by search
                SemanticType: null, // Not returned by search
                Source: r.IsDBNull(3) ? "unknown" : r.GetString(3),
                SemanticScore: r.IsDBNull(4) ? 0.0 : r.GetDouble(4),
                Bm25Score: r.IsDBNull(5) ? 0.0 : r.GetDouble(5),
                StructMentions: r.IsDBNull(6) ? 0 : r.GetInt32(6),
                BodyMentions: r.IsDBNull(7) ? 0 : r.GetInt32(7),
                HighScoringChunks: [] // Filled later if needed
            )).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "search query failed, falling back to empty results");
            return [];
        }
    }

    /// <summary>
    /// Escape a string for SQL (single quotes).
    /// </summary>
    private static string EscapeSqlString(string value)
        => $"'{value.Replace("'", "''")}'";

    /// <summary>
    /// Get chunk-level scores for proximity-based object selection.
    /// Queries document_embedding to find line ranges of high-scoring chunks.
    /// </summary>
    private Dictionary<string, List<ChunkScore>> GetChunkScores(
        IEnumerable<string> documentUris,
        NormalizedQuerySignals signals)
    {
        var uriList = documentUris.ToList();
        if (uriList.Count == 0 || string.IsNullOrWhiteSpace(signals.RawQuery))
            return new Dictionary<string, List<ChunkScore>>();

        // Build URI list for SQL
        var uriListSql = string.Join(",", uriList.Select(u => $"'{EscapeSql(u)}'"));
        var escapedQuery = EscapeSql(signals.RawQuery);

        // Query chunks with their semantic scores against the query
        // Note: Chunks have byte ranges but no spans with line numbers,
        // so we estimate line numbers from byte positions using document stats
        // Use embed_text() UDF to ensure query embedding matches document embedding dimension
        var sql = $"""
            WITH query_vec AS (
                SELECT embed_text('{escapedQuery}')::FLOAT[] AS vec
            ),
            doc_info AS (
                SELECT
                    n.id AS doc_id,
                    n.uri AS doc_uri,
                    LENGTH(a.text_content) AS total_bytes,
                    ARRAY_LENGTH(STRING_SPLIT(a.text_content, CHR(10))) AS line_count
                FROM node n
                JOIN artifact a ON a.id = n.artifact_id
                WHERE n.uri IN ({uriListSql})
            ),
            chunk_scores AS (
                SELECT
                    di.doc_uri,
                    de.chunk_index,
                    -- Estimate start/end lines from byte positions
                    GREATEST(1, CAST(de.start_byte * di.line_count / NULLIF(di.total_bytes, 0) AS INTEGER)) AS start_line,
                    GREATEST(1, CAST(de.end_byte * di.line_count / NULLIF(di.total_bytes, 0) AS INTEGER)) AS end_line,
                    list_cosine_similarity(de.embedding, q.vec) AS chunk_score
                FROM document_embedding de
                JOIN doc_info di ON di.doc_id = de.doc_id
                CROSS JOIN query_vec q
                WHERE de.scope = 'document'
                  AND de.start_byte IS NOT NULL
            )
            SELECT doc_uri, chunk_index, start_line, end_line, chunk_score
            FROM chunk_scores
            WHERE chunk_score > 0.3
            ORDER BY doc_uri, chunk_score DESC
            """;

        try
        {
            var result = new Dictionary<string, List<ChunkScore>>();
            var rows = _store.Query(sql);

            foreach (var row in rows)
            {
                var uri = row.GetValueOrDefault("doc_uri")?.ToString();
                if (string.IsNullOrWhiteSpace(uri)) continue;

                var startLine = Convert.ToInt32(row.GetValueOrDefault("start_line") ?? 1);
                var endLine = Convert.ToInt32(row.GetValueOrDefault("end_line") ?? startLine + 50);
                var score = Convert.ToDouble(row.GetValueOrDefault("chunk_score") ?? 0.5);

                if (!result.TryGetValue(uri, out var chunks))
                {
                    chunks = [];
                    result[uri] = chunks;
                }

                chunks.Add(new ChunkScore(startLine, endLine, score));
            }

            _logger.LogDebug("Retrieved chunk scores for {DocCount} documents, {TotalChunks} chunks",
                result.Count, result.Values.Sum(c => c.Count));

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get chunk scores, falling back to position-based selection");
            return new Dictionary<string, List<ChunkScore>>();
        }
    }

    /// <summary>
    /// Format a float array as a DuckDB array literal.
    /// Uses FLOAT[] without explicit size for compatibility with list_cosine_similarity.
    /// </summary>
    private static string FormatVector(float[] vector)
    {
        var values = string.Join(",", vector.Select(v => v.ToString("G9", CultureInfo.InvariantCulture)));
        return $"[{values}]::FLOAT[]";
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
    private List<ObjectCandidate> GetObjectCandidates(
        IReadOnlyList<string> documentUris,
        NormalizedQuerySignals signals,
        ObjectSearchConfig config)
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
                substr(COALESCE(headline || E'\n\n' || structure, headline, structure, ''), 1, 640) as body,
                line_start,
                line_end,
                lang,
                semantic_type,
                name_hit_score,
                regex_mentions
            FROM hybrid_object_candidates(
                {uriArray}::VARCHAR[],
                keywords := {EscapeSqlString(signals.RawQuery)},
                max_per_doc := {config.MaxObjectsPerDocument}
            )
            """;

        try
        {
            return _store.Read(sql, r =>
            {
                // node_id is a UUID/GUID, convert to string
                var nodeId = r.IsDBNull(0) ? null : r.GetGuid(0).ToString();
                var uri = r.IsDBNull(1) ? null : r.GetString(1);
                var docUri = r.IsDBNull(2) ? null : r.GetString(2);
                var kind = r.IsDBNull(3) ? null : r.GetString(3);

                if (string.IsNullOrWhiteSpace(nodeId) || string.IsNullOrWhiteSpace(uri) ||
                    string.IsNullOrWhiteSpace(docUri) || string.IsNullOrWhiteSpace(kind))
                    return null;

                var candidate = new ObjectCandidate
                {
                    NodeId = nodeId,
                    Uri = uri,
                    DocumentUri = docUri,
                    Kind = kind,
                    Symbol = r.IsDBNull(4) ? null : r.GetString(4),
                    Headline = r.IsDBNull(5) ? null : r.GetString(5),
                    Structure = r.IsDBNull(6) ? null : r.GetString(6),
                    Body = r.IsDBNull(7) ? null : r.GetString(7),
                    LineStart = r.IsDBNull(8) ? 1 : r.GetInt32(8),
                    LineEnd = r.IsDBNull(9) ? 1 : r.GetInt32(9),
                    StartByte = null, // Not returned by macro
                    EndByte = null, // Not returned by macro
                    Lang = r.IsDBNull(10) ? null : r.GetString(10),
                    SemanticType = r.IsDBNull(11) ? null : r.GetString(11),
                    NameHitScore = r.IsDBNull(12) ? 0.0 : Convert.ToDouble(r.GetValue(12)),
                    RegexHitScore = (r.IsDBNull(13) ? 0 : Convert.ToInt32(r.GetValue(13))) * 0.1 // Normalize regex mentions
                };

                // Apply type prior
                candidate.TypePriorScore = config.TypePriors.TryGetValue(kind, out var prior)
                    ? prior
                    : config.DefaultTypePrior;

                return candidate;
            }).Where(c => c != null).Cast<ObjectCandidate>().ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "hybrid_object_candidates query failed");
            return [];
        }
    }

    /// <summary>
    /// Get object candidates prioritizing those near high-scoring chunks.
    /// Instead of position-based selection, we get more objects and filter by chunk proximity.
    /// </summary>
    private List<ObjectCandidate> GetObjectCandidatesNearChunks(
        IReadOnlyList<string> documentUris,
        Dictionary<string, List<ChunkScore>> chunksByDoc,
        NormalizedQuerySignals signals,
        ObjectSearchConfig config)
    {
        if (documentUris.Count == 0)
            return [];

        // If we have no chunks, fall back to standard object selection
        if (chunksByDoc.Count == 0)
        {
            _logger.LogDebug("No chunks available, falling back to position-based object selection");
            return GetObjectCandidates(documentUris, signals, config);
        }

        // Get MORE objects from SQL (3x the normal limit) to ensure we capture chunk-proximate ones
        var expandedLimit = config.MaxObjectsPerDocument * 3;

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
                substr(COALESCE(headline || E'\n\n' || structure, headline, structure, ''), 1, 640) as body,
                line_start,
                line_end,
                lang,
                semantic_type,
                name_hit_score,
                regex_mentions
            FROM hybrid_object_candidates(
                {uriArray}::VARCHAR[],
                keywords := {EscapeSqlString(signals.RawQuery)},
                max_per_doc := {expandedLimit}
            )
            """;

        List<ObjectCandidate> allCandidates;
        try
        {
            allCandidates = _store.Read(sql, r =>
            {
                var nodeId = r.IsDBNull(0) ? null : r.GetGuid(0).ToString();
                var uri = r.IsDBNull(1) ? null : r.GetString(1);
                var docUri = r.IsDBNull(2) ? null : r.GetString(2);
                var kind = r.IsDBNull(3) ? null : r.GetString(3);

                if (string.IsNullOrWhiteSpace(nodeId) || string.IsNullOrWhiteSpace(uri) ||
                    string.IsNullOrWhiteSpace(docUri) || string.IsNullOrWhiteSpace(kind))
                    return null;

                var candidate = new ObjectCandidate
                {
                    NodeId = nodeId,
                    Uri = uri,
                    DocumentUri = docUri,
                    Kind = kind,
                    Symbol = r.IsDBNull(4) ? null : r.GetString(4),
                    Headline = r.IsDBNull(5) ? null : r.GetString(5),
                    Structure = r.IsDBNull(6) ? null : r.GetString(6),
                    Body = r.IsDBNull(7) ? null : r.GetString(7),
                    LineStart = r.IsDBNull(8) ? 1 : r.GetInt32(8),
                    LineEnd = r.IsDBNull(9) ? 1 : r.GetInt32(9),
                    StartByte = null,
                    EndByte = null,
                    Lang = r.IsDBNull(10) ? null : r.GetString(10),
                    SemanticType = r.IsDBNull(11) ? null : r.GetString(11),
                    NameHitScore = r.IsDBNull(12) ? 0.0 : Convert.ToDouble(r.GetValue(12)),
                    RegexHitScore = (r.IsDBNull(13) ? 0 : Convert.ToInt32(r.GetValue(13))) * 0.1
                };

                candidate.TypePriorScore = config.TypePriors.TryGetValue(kind, out var prior)
                    ? prior
                    : config.DefaultTypePrior;

                return candidate;
            }).Where(c => c != null).Cast<ObjectCandidate>().ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Chunk-based object query failed");
            return [];
        }

        if (allCandidates.Count == 0)
            return [];

        // Score each candidate by chunk overlap
        foreach (var candidate in allCandidates)
        {
            if (chunksByDoc.TryGetValue(candidate.DocumentUri, out var chunks) && chunks.Count > 0)
            {
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
            else
            {
                candidate.ChunkOverlapScore = 0.0;
            }
        }

        // Sort by combined score (chunk overlap + name hit) and take top N per document
        var selectedCandidates = new List<ObjectCandidate>();
        var candidatesByDoc = allCandidates.GroupBy(c => c.DocumentUri);

        foreach (var group in candidatesByDoc)
        {
            var sorted = group
                .OrderByDescending(c => c.ChunkOverlapScore * 2.0 + c.NameHitScore + c.RegexHitScore * 0.5)
                .Take(config.MaxObjectsPerDocument)
                .ToList();
            selectedCandidates.AddRange(sorted);
        }

        _logger.LogDebug("Selected {Count} objects from {Total} candidates based on chunk proximity",
            selectedCandidates.Count, allCandidates.Count);

        return selectedCandidates;
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
    /// First checks persistent storage, then session cache, then computes missing.
    /// Newly computed embeddings are persisted for future searches.
    /// Adds telemetry tags to Activity.Current for observability.
    /// </summary>
    private async Task ComputeJitEmbeddingsAsync(
        List<ObjectCandidate> candidates,
        NormalizedQuerySignals signals,
        JitEmbeddingCache cache,
        CancellationToken cancellationToken)
    {
        var activity = System.Diagnostics.Activity.Current;
        activity?.SetTag("jit.objects_selected", candidates.Count);

        if (_embeddingProvider?.Enabled != true || signals.QueryEmbedding is null)
            return;

        var model = _embeddingProvider.Model;
        var dim = _embeddingProvider.Dimension;

        // Step 1: Check persistent storage for existing embeddings
        var loadSw = System.Diagnostics.Stopwatch.StartNew();
        var persistedEmbeddings = LoadPersistedObjectEmbeddings(
            candidates.Select(c => c.NodeId).ToList(), model, dim);
        loadSw.Stop();

        // Apply persisted embeddings
        var needsComputation = new List<ObjectCandidate>();
        foreach (var candidate in candidates)
        {
            if (persistedEmbeddings.TryGetValue(candidate.NodeId, out var embedding))
            {
                candidate.JitEmbedding = embedding;
                candidate.SemanticScore = CosineSimilarity(signals.QueryEmbedding, embedding);
                _logger.LogTrace("Using persisted embedding for {NodeId}", candidate.NodeId);
            }
            else
            {
                needsComputation.Add(candidate);
            }
        }

        var loadedFromStorage = persistedEmbeddings.Count;
        activity?.SetTag("jit.loaded_from_storage", loadedFromStorage);
        activity?.SetTag("jit.load_time_ms", loadSw.ElapsedMilliseconds);

        if (needsComputation.Count == 0)
        {
            _logger.LogDebug("All {Count} embeddings found in persistent storage", candidates.Count);
            activity?.SetTag("jit.computed_fresh", 0);
            return;
        }

        _logger.LogDebug("{Persisted} embeddings from storage, {Remaining} need computation",
            loadedFromStorage, needsComputation.Count);

        // Step 2: Compute missing embeddings (session cache handles deduplication)
        // Use passage embedding for object content (E5 models prepend "passage: " prefix)
        var computeSw = System.Diagnostics.Stopwatch.StartNew();
        var texts = needsComputation.Select(c =>
            $"{c.Headline ?? ""} {c.Structure ?? ""}".Trim()).ToList();

        var embeddings = cache.GetOrComputeBatch(
            texts,
            batch => _embeddingProvider.EmbedPassageBatchAsync(batch, cancellationToken).GetAwaiter().GetResult());
        computeSw.Stop();

        // Apply computed embeddings and collect for persistence
        var toPersist = new List<(ObjectCandidate Candidate, float[] Embedding)>();
        for (var i = 0; i < needsComputation.Count; i++)
        {
            var candidate = needsComputation[i];
            candidate.JitEmbedding = embeddings[i];

            if (embeddings[i] is not null)
            {
                candidate.SemanticScore = CosineSimilarity(signals.QueryEmbedding, embeddings[i]!);
                toPersist.Add((candidate, embeddings[i]!));
            }
        }

        activity?.SetTag("jit.computed_fresh", toPersist.Count);
        activity?.SetTag("jit.compute_time_ms", computeSw.ElapsedMilliseconds);

        // Step 3: Persist newly computed embeddings (fire-and-forget, don't block search)
        if (toPersist.Count > 0)
        {
            var toPersistCount = toPersist.Count;
            activity?.SetTag("jit.persisted_to_storage", toPersistCount);
            _ = Task.Run(() =>
            {
                try
                {
                    PersistObjectEmbeddings(toPersist, model, dim);
                    _logger.LogDebug("Persisted {Count} object embeddings", toPersistCount);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to persist object embeddings");
                }
            }, cancellationToken);
        }
    }

    /// <summary>
    /// Load persisted object embeddings from database.
    /// </summary>
    private Dictionary<string, float[]> LoadPersistedObjectEmbeddings(
        IReadOnlyList<string> nodeIds,
        string model,
        int dim)
    {
        if (nodeIds.Count == 0)
            return new Dictionary<string, float[]>();

        try
        {
            // Build IN clause with proper escaping
            var nodeIdList = string.Join(",", nodeIds.Select(id => $"'{EscapeSql(id)}'"));

            var sql = $"""
                SELECT node_id::TEXT, embedding
                FROM document_embedding
                WHERE scope = 'object'
                  AND node_id::TEXT IN ({nodeIdList})
                  AND model = {EscapeSqlString(model)}
                  AND dim = {dim}
                """;

            var result = new Dictionary<string, float[]>();
            var rows = _store.Read(sql, r =>
            {
                var nodeId = r.IsDBNull(0) ? null : r.GetString(0);
                if (string.IsNullOrWhiteSpace(nodeId)) return (null, null);

                // Read embedding as array
                var embeddingObj = r.GetValue(1);
                if (embeddingObj is IList<object> list)
                {
                    var embedding = new float[list.Count];
                    for (var i = 0; i < list.Count; i++)
                    {
                        embedding[i] = Convert.ToSingle(list[i]);
                    }
                    return (nodeId, embedding);
                }
                return (nodeId, null);
            });

            foreach (var (nodeId, embedding) in rows)
            {
                if (nodeId != null && embedding != null)
                    result[nodeId] = embedding;
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load persisted object embeddings");
            return new Dictionary<string, float[]>();
        }
    }

    /// <summary>
    /// Persist newly computed object embeddings to database.
    /// </summary>
    private void PersistObjectEmbeddings(
        IReadOnlyList<(ObjectCandidate Candidate, float[] Embedding)> items,
        string model,
        int dim)
    {
        if (items.Count == 0)
            return;

        try
        {
            // Use INSERT ... ON CONFLICT to handle duplicates gracefully
            // Build a VALUES clause for batch insert
            var valuesClauses = new List<string>();
            var now = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");

            foreach (var (candidate, embedding) in items)
            {
                var embeddingJson = "[" + string.Join(",", embedding.Select(f => f.ToString(CultureInfo.InvariantCulture))) + "]";

                valuesClauses.Add($"""
                    (
                        (SELECT n.id FROM node n WHERE n.uri = '{EscapeSql(candidate.DocumentUri)}' LIMIT 1),
                        '{EscapeSql(candidate.NodeId)}'::UUID,
                        0,
                        'structure',
                        '{EscapeSql(candidate.Uri)}',
                        'object',
                        '{EscapeSql(model)}',
                        {dim},
                        {embeddingJson}::FLOAT[],
                        {candidate.StartByte?.ToString() ?? "NULL"},
                        {candidate.EndByte?.ToString() ?? "NULL"},
                        '{now}'::TIMESTAMP
                    )
                    """);
            }

            var sql = $"""
                INSERT INTO document_embedding
                    (doc_id, node_id, chunk_index, embedding_type, uri, scope, model, dim, embedding, start_byte, end_byte, updated_at)
                VALUES {string.Join(",\n", valuesClauses)}
                ON CONFLICT (doc_id, node_id, chunk_index, embedding_type)
                DO UPDATE SET
                    embedding = EXCLUDED.embedding,
                    updated_at = EXCLUDED.updated_at
                """;

            _store.Read<int>(sql, _ => 0); // Execute as query (returns no rows)
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist {Count} object embeddings", items.Count);
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

    /// <summary>
    /// Fetch actual source code snippets for the top-scoring objects.
    /// Uses the snippet() macro to extract lines from the file content.
    /// </summary>
    private void PopulateSnippets(List<ObjectCandidate> candidates, int maxSnippets)
    {
        if (candidates.Count == 0) return;

        // Only fetch snippets for top N candidates (they'll be shown with code)
        var topCandidates = candidates.Take(maxSnippets).ToList();
        if (topCandidates.Count == 0) return;

        try
        {
            // Group by document to batch queries
            var byDocument = topCandidates.GroupBy(c => c.DocumentUri).ToList();

            foreach (var docGroup in byDocument)
            {
                var docUri = docGroup.Key;
                foreach (var candidate in docGroup)
                {
                    try
                    {
                        // Build a clean URI with ONLY line fragment - avoid symbol fragments
                        // which cause the snippet macro to use symbol lookup (often fails)
                        if (candidate.LineStart <= 0)
                            continue;

                        var lineFragment = candidate.LineEnd > candidate.LineStart
                            ? $"#line={candidate.LineStart},{candidate.LineEnd}"
                            : $"#line={candidate.LineStart}";
                        var snippetUri = candidate.DocumentUri + lineFragment;

                        var sql = $"SELECT string_agg(text, chr(10) ORDER BY line_number) FROM snippet('{EscapeSql(snippetUri)}', 0)";
                        var snippet = _store.Read(sql, r => r.IsDBNull(0) ? null : r.GetString(0)).FirstOrDefault();

                        if (!string.IsNullOrWhiteSpace(snippet))
                        {
                            candidate.Snippet = snippet;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Failed to fetch snippet for {Uri}", candidate.Uri);
                        // Leave Snippet as null, will fall back to Body
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to populate snippets");
        }
    }
}

// IJitObjectSearchService and JitObjectSearchResult are defined in RepoQL.Explore.Search
