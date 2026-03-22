using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RepoQL.Contracts.Embeddings;
using RepoQL.Contracts.Search;
using RepoQL.Data.DuckDB;
using RepoQL.Explore.Search;

namespace RepoQL.ConsoleApp.Search;

/// <summary>
/// JIT enrichment service. Computes ONNX embeddings for uncertain object candidates
/// and blends semantic scores into existing rankings from _explore_candidates.
///
/// Purpose: Refine explore results where semantic evidence is uncertain (inherited or missing).
/// Complexity: Query embedding, provenance-based uncertainty selection, expected-value JIT planning,
///   ONNX batch embedding with persistent caching, score blending.
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
    public async Task<JitEnrichmentResult> EnrichAsync(
        string question,
        IReadOnlyList<ExploreCandidate> candidates,
        JitEmbeddingCache jitCache,
        ObjectSearchConfig config,
        CancellationToken cancellationToken)
    {
        if (_embeddingProvider?.Enabled != true || string.IsNullOrWhiteSpace(question))
            return new JitEnrichmentResult(candidates, false);

        // Step 1: Compute query embedding and detect intent
        var signals = await NormalizeQueryAsync(question, cancellationToken).ConfigureAwait(false);
        if (signals.QueryEmbedding is null)
            return new JitEnrichmentResult(candidates, false);

        // Step 2: Identify object candidates and their indices
        var objectIndices = new List<int>();
        for (var i = 0; i < candidates.Count; i++)
        {
            if (string.Equals(candidates[i].NodeScope, "object", StringComparison.OrdinalIgnoreCase))
                objectIndices.Add(i);
        }

        if (objectIndices.Count == 0)
            return new JitEnrichmentResult(candidates, false);

        // Convert to ObjectCandidate for the JIT pipeline
        var objectCandidates = objectIndices
            .Select(i => ToObjectCandidate(candidates[i]))
            .ToList();

        // Step 3: Plan JIT embeddings based on provenance uncertainty
        var jitCandidates = PlanJitEmbeddings(objectCandidates, config);
        if (jitCandidates.Count == 0)
            return new JitEnrichmentResult(candidates, false);

        _logger.LogDebug("Selected {Count} uncertain candidates for JIT enrichment from {Total} objects",
            jitCandidates.Count, objectCandidates.Count);

        // Step 4: Compute JIT embeddings (checks persistent storage, session cache, then ONNX)
        await ComputeJitEmbeddingsAsync(jitCandidates, signals, jitCache, cancellationToken)
            .ConfigureAwait(false);

        // Step 5: Blend JIT semantic scores into existing SQL-computed scores
        // SQL computes: score = combine(bm25_norm, fuzz_norm, sem_norm, ws := effective_sem_weight)
        // effective_sem_weight defaults to 0.70 in _explore_candidates. We use the same weight
        // for score adjustment so the semantic contribution scales match.
        // Note: JIT cosine similarity and SQL sem_norm are on approximately the same [0,1] scale
        // but not perfectly aligned (cosine vs rank-normalized), so we clamp the adjustment.
        const double effectiveSemWeight = 0.70;
        var result = candidates.ToList();
        var changed = false;

        for (var j = 0; j < objectCandidates.Count; j++)
        {
            var obj = objectCandidates[j];
            if (!obj.SelectedForJitEmbedding || obj.JitEmbedding is null)
                continue;

            var originalIndex = objectIndices[j];
            var original = candidates[originalIndex];

            // Replace the semantic contribution: (jitSem - sqlSem) * effectiveSemWeight
            // Clamped to prevent scale-mismatch from dominating the combined score
            var rawAdjustment = (obj.SemanticScore - original.SemScore) * effectiveSemWeight;
            var adjustment = Math.Clamp(rawAdjustment, -0.3, 0.3);
            if (Math.Abs(adjustment) > 0.001)
            {
                result[originalIndex] = original with
                {
                    Score = original.Score + adjustment,
                    SemScore = obj.SemanticScore,
                    SemProvenance = "direct" // now has direct semantic evidence
                };
                changed = true;
            }
        }

        if (changed)
        {
            _logger.LogDebug("JIT enrichment adjusted scores for {Count} candidates",
                result.Where((c, i) => !ReferenceEquals(c, candidates[i])).Count());

            // Recompute confidence for all candidates using min-max normalization.
            // Matches _explore_candidates SQL: 10 (lowest) to 100 (highest) across full result set.
            var allScores = result.Select(c => c.Score).ToList();
            if (allScores.Count > 0)
            {
                var minScore = allScores.Min();
                var range = allScores.Max() - minScore;

                for (var i = 0; i < result.Count; i++)
                {
                    var newConfidence = range > 0
                        ? (int)(10 + 90 * (result[i].Score - minScore) / range)
                        : 50;

                    if (newConfidence != result[i].Confidence)
                        result[i] = result[i] with { Confidence = newConfidence };
                }
            }
        }

        return new JitEnrichmentResult(result, changed);
    }

    /// <summary>
    /// Normalize query: precompute query embedding and detect intent.
    /// </summary>
    private async Task<NormalizedQuerySignals> NormalizeQueryAsync(string? question, CancellationToken cancellationToken)
    {
        var rawQuery = question?.Trim() ?? "";
        var intent = DetectQueryIntent(rawQuery);

        float[]? queryEmbedding = null;
        if (_embeddingProvider?.Enabled == true && !string.IsNullOrWhiteSpace(rawQuery))
        {
            queryEmbedding = await _embeddingProvider.EmbedQueryAsync(rawQuery, cancellationToken).ConfigureAwait(false);
        }

        return new NormalizedQuerySignals
        {
            RawQuery = rawQuery,
            QueryEmbedding = queryEmbedding,
            DetectedIntent = intent,
        };
    }

    /// <summary>
    /// Detect query intent: Semantic (natural language), Symbol (code identifier), or Hybrid.
    /// </summary>
    private static QueryIntent DetectQueryIntent(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return QueryIntent.Hybrid;

        var hasSymbolIndicators =
            Regex.IsMatch(query, @"[A-Z][a-z]+[A-Z]") ||
            Regex.IsMatch(query, @"[a-z]+[A-Z]") ||
            query.Contains('_') ||
            query.Contains('.') ||
            query.Contains("::");

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
    /// Plan JIT embeddings based on provenance-driven uncertainty and expected value.
    /// Objects with inherited or missing semantic evidence are prioritized.
    /// </summary>
    private List<ObjectCandidate> PlanJitEmbeddings(List<ObjectCandidate> candidates, ObjectSearchConfig config)
    {
        if (candidates.Count == 0 || _embeddingProvider?.Enabled != true)
            return [];

        // Sort by existing score (from _explore_candidates) to determine rank
        var sorted = candidates.OrderByDescending(c => c.CheapAggregateScore).ToList();

        for (var i = 0; i < sorted.Count; i++)
        {
            var candidate = sorted[i];
            var rank = i + 1;

            // Uncertainty from semantic provenance — objects with inherited or missing
            // semantic evidence benefit most from JIT embedding
            candidate.Uncertainty = candidate.SemProvenance switch
            {
                "direct" => 0.1,         // Has own semantic evidence — very certain
                "chunk_overlap" => 0.3,  // Overlaps high-scoring chunk — somewhat certain
                "inherited" => 0.7,      // Inherited parent's score at 0.5x — uncertain
                _ => 0.9                 // No semantic evidence — very uncertain
            };

            candidate.ExpectedImpact = 1.0 / Math.Sqrt(rank);
            candidate.ExpectedValue = candidate.Uncertainty * candidate.ExpectedImpact;
        }

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
    /// Checks persistent storage first, then session cache, then computes via ONNX.
    /// Newly computed embeddings are persisted for future searches.
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

        // Step 3: Persist newly computed embeddings (fire-and-forget)
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
    /// Convert ExploreCandidate to ObjectCandidate for the JIT embedding pipeline.
    /// </summary>
    private static ObjectCandidate ToObjectCandidate(ExploreCandidate candidate)
    {
        var fragmentIndex = candidate.Uri.IndexOf('#', StringComparison.Ordinal);
        var documentUri = candidate.Path
            ?? (fragmentIndex >= 0 ? candidate.Uri[..fragmentIndex] : candidate.Uri);

        return new ObjectCandidate
        {
            NodeId = candidate.NodeId.ToString("D"),
            Uri = candidate.Uri,
            DocumentUri = documentUri,
            Kind = candidate.Kind ?? "unknown",
            Symbol = candidate.Symbol,
            Headline = candidate.Headline,
            Structure = candidate.Structure,
            LineStart = candidate.LineStart ?? 0,
            LineEnd = candidate.LineEnd ?? 0,
            Lang = candidate.Lang,
            SemanticType = candidate.Mime,
            SemProvenance = candidate.SemProvenance,
            // Use SQL score as cheap aggregate (for ranking in PlanJitEmbeddings)
            CheapAggregateScore = candidate.Score,
            // Use SQL semantic score for chunk overlap proxy
            ChunkOverlapScore = candidate.SemScore,
        };
    }

    #region Persistent embedding storage

    /// <summary>
    /// Load persisted object embeddings from database.
    /// </summary>
    internal Dictionary<string, float[]> LoadPersistedObjectEmbeddings(
        IReadOnlyList<string> nodeIds,
        string model,
        int dim)
    {
        if (nodeIds.Count == 0)
            return new Dictionary<string, float[]>();

        var validNodeIds = ParseValidGuids(nodeIds);
        if (validNodeIds.Count == 0)
            return new Dictionary<string, float[]>();

        try
        {
            var nodeIdValues = string.Join(",\n                    ", validNodeIds.Select(id => $"('{id:D}'::UUID)"));

            var sql = $"""
                WITH filter_node_ids(node_id) AS (
                    SELECT node_id
                    FROM (VALUES
                        {nodeIdValues}
                    ) AS ids(node_id)
                )
                SELECT de.node_id, de.embedding
                FROM document_embedding de
                JOIN filter_node_ids f ON f.node_id = de.node_id
                WHERE de.scope = 'object'
                  AND de.model = {EscapeSqlString(model)}
                  AND de.dim = {dim}
                """;

            var result = new Dictionary<string, float[]>();
            var rows = _store.Read<(string? NodeId, float[]? Embedding)>(sql, r =>
            {
                var nodeId = r.IsDBNull(0) ? null : r.GetGuid(0).ToString("D");
                if (string.IsNullOrWhiteSpace(nodeId)) return ((string?)null, (float[]?)null);

                var embedding = ParseEmbeddingVector(r.GetValue(1));
                return (nodeId, embedding);
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

    private static List<Guid> ParseValidGuids(IReadOnlyList<string> ids)
    {
        var parsed = new List<Guid>(ids.Count);
        var seen = new HashSet<Guid>();

        foreach (var id in ids)
        {
            if (string.IsNullOrWhiteSpace(id))
                continue;

            if (!Guid.TryParse(id, out var guid))
                continue;

            if (seen.Add(guid))
                parsed.Add(guid);
        }

        return parsed;
    }

    private static float[]? ParseEmbeddingVector(object? value)
    {
        if (value is null or DBNull)
            return null;

        if (value is float[] floatArray)
            return floatArray;

        if (value is double[] doubleArray)
            return doubleArray.Select(static v => (float)v).ToArray();

        if (value is IList<object> list)
        {
            var embedding = new float[list.Count];
            for (var i = 0; i < list.Count; i++)
            {
                embedding[i] = Convert.ToSingle(list[i], CultureInfo.InvariantCulture);
            }
            return embedding;
        }

        if (value is System.Collections.IList nonGenericList)
        {
            var embedding = new float[nonGenericList.Count];
            for (var i = 0; i < nonGenericList.Count; i++)
            {
                embedding[i] = Convert.ToSingle(nonGenericList[i], CultureInfo.InvariantCulture);
            }
            return embedding;
        }

        return null;
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
                        NULL,
                        NULL,
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

            _store.Read<int>(sql, _ => 0);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist {Count} object embeddings", items.Count);
        }
    }

    #endregion

    private static string EscapeSqlString(string value)
        => $"'{value.Replace("'", "''")}'";

    private static string EscapeSql(string value) => value.Replace("'", "''");
}
