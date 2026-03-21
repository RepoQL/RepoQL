using System.Data;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RepoQL.Data.DuckDB.UdfFramework;

namespace RepoQL.Data.DuckDB.UdfImplementations;

/// <summary>
/// Purpose: Run the search candidate pipeline from C# via re-entrant SQL reads.
/// Complexity: Fuses document scores, maps evidence onto child objects, enriches nodes,
/// and emits the typed search result contract expected by SQL callers.
/// </summary>
[UdfClass]
public sealed class SearchPipelineUdf(
    IReentrantReader? reader = null,
    ILogger<SearchPipelineUdf>? logger = null)
{
    private readonly IReentrantReader? _reader = reader;
    private readonly ILogger<SearchPipelineUdf> _logger = logger ?? NullLogger<SearchPipelineUdf>.Instance;

    [StructuredUdf("_search_pipeline_internal",
        MacroName = "search_pipeline",
        Description = "Two-pass search: doc scoring via SQL, object scoring in C#")]
    public IEnumerable<SearchResultRow> Execute(
        string query,
        [UdfDefault("NULL")] string? scope,
        [UdfDefault("50")] int k,
        [UdfDefault("200")] int top_doc_limit,
        [UdfDefault("20")] int per_doc_cap)
    {
        var activeReader = ResolveReader();
        if (activeReader is null)
        {
            _logger.LogWarning("search_pipeline: no reentrant reader available.");
            return [];
        }

        var queryClean = query?.Trim() ?? string.Empty;
        var scopeParam = string.IsNullOrWhiteSpace(scope) ? "NULL" : $"'{EscapeSql(scope.Trim())}'";
        var kParam = Math.Max(1, k);
        var topDocLimit = Math.Max(1, Math.Min(top_doc_limit, 500));
        var perDocCap = Math.Max(1, per_doc_cap);
        var queryLower = queryClean.ToLowerInvariant();
        var routeMode = SearchClassifyQuery(queryClean);

        try
        {
            var lexDocs = TryReadLexical(activeReader, queryClean, scopeParam);
            var semDocs = TryReadSemantic(activeReader, queryClean, scopeParam);
            var (docScores, docRanked) = MergeDocScores(lexDocs, semDocs);

            var topDocIds = docRanked
                .Take(topDocLimit)
                .Select(static d => d.DocId)
                .ToHashSet();

            IReadOnlyList<ScoredObject> scoredObjects = topDocIds.Count == 0
                ? []
                : ScoreObjects(activeReader, topDocIds, queryLower, semDocs, perDocCap);

            IReadOnlyList<Guid> fallbackNodes = docRanked.Count == 0 && scoredObjects.Count == 0
                ? ReadFallbackNodes(activeReader, scopeParam, kParam)
                : [];

            var docRows = docRanked.Take(kParam).ToList();
            var allNodeIds = docRows.Select(static d => d.NodeId)
                .Concat(scoredObjects.Select(static o => o.NodeId))
                .Concat(fallbackNodes)
                .Distinct()
                .ToHashSet();

            if (allNodeIds.Count == 0)
                return [];

            var enrichedNodes = ReadEnrichedNodes(activeReader, allNodeIds);
            if (enrichedNodes.Count == 0)
                return [];

            var enrichedByNodeId = enrichedNodes.ToDictionary(static n => n.NodeId);
            // Use JsonObject instead of anonymous type — anonymous types produce {} in IL-trimmed Release builds.
            var explainJson = new JsonObject
            {
                ["route"] = routeMode,
                ["lex_candidates"] = lexDocs.Count,
                ["dense_candidates"] = semDocs.Count,
                ["object_candidates"] = scoredObjects.Count,
                ["requested_mode"] = "auto"
            }.ToJsonString();

            var results = new List<SearchResultRow>(docRows.Count + scoredObjects.Count + fallbackNodes.Count);

            foreach (var doc in docRows)
            {
                if (!enrichedByNodeId.TryGetValue(doc.NodeId, out var node))
                    continue;

                results.Add(BuildDocumentRow(node, doc, explainJson));
            }

            // Build doc rank lookup for doc-rank boost in object scoring
            var docRankLookup = new Dictionary<Guid, int>();
            for (var i = 0; i < docRanked.Count; i++)
                docRankLookup[docRanked[i].DocId] = i + 1; // 1-based rank
            var totalDocCount = docRanked.Count;

            foreach (var obj in scoredObjects)
            {
                if (!enrichedByNodeId.TryGetValue(obj.NodeId, out var node))
                    continue;

                if (!docScores.TryGetValue(obj.DocId, out var doc))
                    continue;

                docRankLookup.TryGetValue(obj.DocId, out var docRank);
                results.Add(BuildObjectRow(node, doc, obj, explainJson, docRank, totalDocCount));
            }

            var addedNodeIds = results.Select(static r => r.NodeId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var fallbackNodeId in fallbackNodes)
            {
                if (!enrichedByNodeId.TryGetValue(fallbackNodeId, out var node))
                    continue;

                if (!addedNodeIds.Add(node.NodeIdText))
                    continue;

                results.Add(BuildFallbackRow(node, explainJson));
            }

            return results
                .OrderBy(r => RankBias(r, queryLower, string.IsNullOrWhiteSpace(scope)))
                .ThenByDescending(static r => r.Score)
                .ThenBy(static r => r.Uri?.Length ?? int.MaxValue)
                .Take(kParam)
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "search_pipeline failed for query '{Query}'.", queryClean);
            return [];
        }
    }

    private IReadOnlyList<LexDocScore> TryReadLexical(IReentrantReader reader, string queryClean, string scopeParam)
    {
        if (string.IsNullOrWhiteSpace(queryClean))
            return [];

        try
        {
            return reader.Read(
                $"""
                SELECT node_id, doc_id, bm25_score, fuzzy_score, bm25_norm, fuzz_norm, lex_rank, rrf_lex
                FROM _search_lexical(
                    q := '{EscapeSql(queryClean)}',
                    uri_glob := {scopeParam},
                    max_cand := 5000
                )
                """,
                r => new LexDocScore(
                    r.GetGuid(0),
                    r.GetGuid(1),
                    GetDoubleOrZero(r, 2),
                    GetDoubleOrZero(r, 3),
                    GetDoubleOrZero(r, 4),
                    GetDoubleOrZero(r, 5),
                    GetLongOrZero(r, 6),
                    GetDoubleOrZero(r, 7)));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "search_pipeline: lexical stage failed.");
            return [];
        }
    }

    private IReadOnlyList<SemDocScore> TryReadSemantic(IReentrantReader reader, string queryClean, string scopeParam)
    {
        if (string.IsNullOrWhiteSpace(queryClean))
            return [];

        try
        {
            return reader.Read(
                $"""
                SELECT
                    node_id,
                    doc_id,
                    sem_score,
                    sem_norm,
                    sem_rank,
                    rrf_sem,
                    search_source,
                    structure_score,
                    fulltext_score,
                    best_chunk_index,
                    best_chunk_start,
                    best_chunk_end
                FROM _search_semantic(
                    q := '{EscapeSql(queryClean)}',
                    uri_glob := {scopeParam},
                    max_cand := 5000
                )
                """,
                r => new SemDocScore(
                    r.GetGuid(0),
                    r.GetGuid(1),
                    GetDoubleOrZero(r, 2),
                    GetDoubleOrZero(r, 3),
                    GetLongOrZero(r, 4),
                    GetDoubleOrZero(r, 5),
                    GetStringOrEmpty(r, 6),
                    GetDoubleOrNull(r, 7),
                    GetDoubleOrNull(r, 8),
                    GetIntOrNull(r, 9),
                    GetLongOrNull(r, 10),
                    GetLongOrNull(r, 11)));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "search_pipeline: semantic stage failed.");
            return [];
        }
    }

    /// <summary>
    /// Merge lexical and semantic doc scores. Returns a dictionary for lookup
    /// and a separate ordered list for ranked iteration (Dictionary iteration order
    /// is not guaranteed in .NET).
    /// </summary>
    private (Dictionary<Guid, MergedDocScore> ByDocId, List<MergedDocScore> Ranked) MergeDocScores(
        IReadOnlyList<LexDocScore> lexDocs,
        IReadOnlyList<SemDocScore> semDocs)
    {
        var merged = new Dictionary<Guid, MergedDocScore>();

        foreach (var lex in lexDocs)
        {
            merged[lex.DocId] = new MergedDocScore(
                lex.NodeId,
                lex.DocId,
                lex.Bm25Score,
                lex.FuzzyScore,
                lex.Bm25Norm,
                lex.FuzzNorm,
                0.0,
                0.0,
                lex.RrfLex,
                0.0,
                string.Empty,
                null,
                null,
                null,
                null,
                null);
        }

        foreach (var sem in semDocs)
        {
            if (merged.TryGetValue(sem.DocId, out var existing))
            {
                merged[sem.DocId] = existing with
                {
                    NodeId = existing.NodeId == Guid.Empty ? sem.NodeId : existing.NodeId,
                    SemScore = sem.SemScore,
                    SemNorm = sem.SemNorm,
                    RrfSem = sem.RrfSem,
                    SearchSource = sem.SearchSource,
                    StructureScore = sem.StructureScore,
                    FulltextScore = sem.FulltextScore,
                    BestChunkIndex = sem.BestChunkIndex,
                    BestChunkStart = sem.BestChunkStart,
                    BestChunkEnd = sem.BestChunkEnd
                };
            }
            else
            {
                merged[sem.DocId] = new MergedDocScore(
                    sem.NodeId,
                    sem.DocId,
                    0.0,
                    0.0,
                    0.0,
                    0.0,
                    sem.SemScore,
                    sem.SemNorm,
                    0.0,
                    sem.RrfSem,
                    sem.SearchSource,
                    sem.StructureScore,
                    sem.FulltextScore,
                    sem.BestChunkIndex,
                    sem.BestChunkStart,
                    sem.BestChunkEnd);
            }
        }

        // Normalize raw semantic scores using absolute anchors based on embedding model characteristics.
        // Voyage 1024d cosine similarity ranges:
        //   0.60+ = strong match, 0.40-0.60 = moderate, 0.20-0.40 = weak, <0.20 = noise
        // Using fixed floor/ceiling preserves absolute relevance signal — irrelevant queries
        // ("quantum physics") stay low instead of being inflated to 1.0 by relative scaling.
        const double semFloor = 0.20;   // below this is noise
        const double semCeiling = 0.65; // above this is a strong match
        const double semRange = semCeiling - semFloor;

        foreach (var key in merged.Keys.ToList())
        {
            var d = merged[key];
            var rescaled = d.SemScore > 0 ? (d.SemScore - semFloor) / semRange : 0.0;
            merged[key] = d with { SemNorm = Math.Clamp(rescaled, 0.0, 1.0) };
        }

        var ranked = merged.Values
            .OrderByDescending(static d => Combine(d.Bm25Norm, d.FuzzNorm, d.SemNorm))
            .ThenByDescending(static d => d.RrfLex + d.RrfSem)
            .ThenBy(static d => d.DocId)
            .ToList();

        return (merged, ranked);
    }

    private IReadOnlyList<ScoredObject> ScoreObjects(
        IReentrantReader reader,
        IReadOnlyCollection<Guid> topDocIds,
        string queryLower,
        IReadOnlyList<SemDocScore> semDocs,
        int perDocCap)
    {
        var children = ReadObjectCandidates(reader, topDocIds);
        _logger.LogDebug("search_pipeline: {ChildCount} children from {DocCount} top docs. Query: '{Query}'",
            children.Count, topDocIds.Count, queryLower);
        if (children.Count == 0)
            return [];

        // Log per-doc child counts for debugging
        var childrenByDoc = children.GroupBy(c => c.DocId).ToDictionary(g => g.Key, g => g.Count());
        foreach (var docId in topDocIds.Take(5))
        {
            childrenByDoc.TryGetValue(docId, out var count);
            _logger.LogDebug("search_pipeline: doc {DocId} has {Count} children", docId, count);
        }

        var grepHits = ReadGrepHits(reader, topDocIds, queryLower);
        var grepByDoc = grepHits.ToLookup(static g => g.DocId);
        var semByDoc = semDocs
            .Where(static s => s.BestChunkStart is not null && s.BestChunkEnd is not null)
            .ToLookup(static s => s.DocId);

        var scored = children
            .Select(obj => ScoreObject(obj, queryLower, grepByDoc[obj.DocId], semByDoc[obj.DocId]))
            .ToList();
        var withSignal = scored.Where(static s => s.ObjectScore > 0).ToList();
        _logger.LogDebug("search_pipeline: {Scored} scored, {WithSignal} with signal > 0", scored.Count, withSignal.Count);

        return withSignal
            .GroupBy(static s => s.DocId)
            .SelectMany(g => g.OrderByDescending(static s => s.ObjectScore)
                .ThenByDescending(static s => s.GrepHits)
                .ThenBy(static s => s.StartLine ?? int.MaxValue)
                .ThenBy(static s => s.NodeId)
                .Take(perDocCap))
            .ToList();
    }

    private IReadOnlyList<ObjectCandidate> ReadObjectCandidates(IReentrantReader reader, IReadOnlyCollection<Guid> topDocIds)
    {
        try
        {
            var docIdList = JoinGuids(topDocIds);
            return reader.Read(
                $"""
                SELECT
                    child.id,
                    s.document_id,
                    s.start_line,
                    s.end_line,
                    s.start_byte,
                    s.end_byte,
                    LOWER(COALESCE(
                        repository_uri_symbol(child.uri),
                        json_extract_string(child.properties, '$.symbol'),
                        json_extract_string(child.properties, '$.name'),
                        ''
                    )) AS symbol_key,
                    LOWER(COALESCE(child.headline, '') || ' ' || COALESCE(child.structure, '')) AS headline_text
                FROM span s
                JOIN node child ON child.span_id = s.id AND child.kind <> 'document'
                WHERE s.document_id IN ({docIdList})
                """,
                r => new ObjectCandidate(
                    r.GetGuid(0),
                    r.GetGuid(1),
                    GetIntOrNull(r, 2),
                    GetIntOrNull(r, 3),
                    GetLongOrNull(r, 4),
                    GetLongOrNull(r, 5),
                    GetStringOrEmpty(r, 6),
                    GetStringOrEmpty(r, 7)));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "search_pipeline: object candidate fetch failed.");
            return [];
        }
    }

    private IReadOnlyList<GrepHit> ReadGrepHits(IReentrantReader reader, IReadOnlyCollection<Guid> topDocIds, string queryLower)
    {
        if (string.IsNullOrWhiteSpace(queryLower))
            return [];

        try
        {
            var docIdList = JoinGuids(topDocIds);
            return reader.Read(
                $"""
                SELECT n.id AS doc_id, CAST(g.line_number AS INTEGER) AS line_number
                FROM grep_matches('{EscapeSql(queryLower)}', '**', 500) g
                JOIN node n ON n.uri = g.uri AND n.kind = 'document'
                WHERE n.id IN ({docIdList})
                """,
                r => new GrepHit(r.GetGuid(0), r.GetInt32(1)));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "search_pipeline: grep stage failed.");
            return [];
        }
    }

    private IReadOnlyList<Guid> ReadFallbackNodes(IReentrantReader reader, string scopeParam, int limit)
    {
        try
        {
            return reader.Read(
                $"""
                SELECT sf.node_id
                FROM _scope_filter(uri_glob := {scopeParam}, scope := 'document') sf
                JOIN node n ON n.id = sf.node_id
                QUALIFY ROW_NUMBER() OVER (ORDER BY n.updated_at DESC, sf.node_id) <= {limit}
                """,
                r => r.GetGuid(0));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "search_pipeline: fallback stage failed.");
            return [];
        }
    }

    private IReadOnlyList<EnrichedNode> ReadEnrichedNodes(IReentrantReader reader, IReadOnlyCollection<Guid> nodeIds)
    {
        var nodeIdList = JoinGuids(nodeIds);
        return reader.Read(
            $"""
            SELECT
                n.id AS node_id,
                COALESCE(sp.document_id, n.id) AS doc_id,
                COALESCE(
                    n.uri,
                    repository_uri_join(
                        COALESCE(doc.uri, 'repoql://unknown'),
                        COALESCE(
                            fragment_from_line_range(CAST(sp.start_line AS VARCHAR), CAST(sp.end_line AS VARCHAR)),
                            concat('node/', n.kind, '/', REPLACE(CAST(n.id AS VARCHAR), '-', ''))
                        )
                    )
                ) AS uri,
                REPLACE(repository_uri_container(COALESCE(doc.uri, n.uri, 'repoql://unknown')), '\\', '/') AS path,
                CASE WHEN n.kind = 'document' THEN 'document' ELSE 'object' END AS node_scope,
                n.kind,
                COALESCE(
                    repository_uri_symbol(n.uri),
                    json_extract_string(n.properties, '$.symbol'),
                    json_extract_string(n.properties, '$.name')
                ) AS symbol,
                media_type_kind(a.media_type) AS lang,
                media_type_base(a.media_type) AS mime,
                CASE
                    WHEN n.kind = 'document' THEN COALESCE(NULLIF(n.headline, ''), NULLIF(a.headline, ''))
                    ELSE COALESCE(
                        NULLIF(n.headline, ''),
                        json_extract_string(n.properties, '$.name'),
                        repository_uri_file_name(doc.uri)
                    )
                END AS headline,
                CASE
                    WHEN n.kind = 'document' THEN COALESCE(NULLIF(n.structure, ''), NULLIF(a.structure, ''))
                    ELSE NULLIF(n.structure, '')
                END AS structure,
                COALESCE(sp.start_line, TRY_CAST(repository_uri_line_start(n.uri) AS INTEGER)) AS line_start,
                COALESCE(sp.end_line, TRY_CAST(repository_uri_line_end(n.uri) AS INTEGER)) AS line_end,
                a.digest,
                a.text_content
            FROM node n
            LEFT JOIN span sp ON sp.id = n.span_id
            LEFT JOIN node doc ON doc.id = COALESCE(sp.document_id, n.id)
            LEFT JOIN artifact a ON a.id = COALESCE(
                CASE WHEN n.kind = 'document' THEN n.artifact_id END,
                doc.artifact_id
            )
            WHERE n.id IN ({nodeIdList})
            """,
            r => new EnrichedNode(
                r.GetGuid(0),
                r.GetGuid(1),
                GetStringOrNull(r, 2),
                GetStringOrNull(r, 3),
                GetStringOrNull(r, 4),
                GetStringOrNull(r, 5),
                GetStringOrNull(r, 6),
                GetStringOrNull(r, 7),
                GetStringOrNull(r, 8),
                GetStringOrNull(r, 9),
                GetStringOrNull(r, 10),
                GetIntOrNull(r, 11),
                GetIntOrNull(r, 12),
                GetStringOrNull(r, 13),
                GetStringOrNull(r, 14)));
    }

    private static ScoredObject ScoreObject(
        ObjectCandidate obj,
        string queryLower,
        IEnumerable<GrepHit> grepHits,
        IEnumerable<SemDocScore> semanticSignals)
    {
        double symbolScore = 0;
        if (!string.IsNullOrWhiteSpace(queryLower))
        {
            if (string.Equals(obj.SymbolKey, queryLower, StringComparison.Ordinal))
                symbolScore = 4.0;
            else if (!string.IsNullOrWhiteSpace(obj.SymbolKey) &&
                     obj.SymbolKey.Contains(queryLower, StringComparison.Ordinal))
                symbolScore = 3.2;
        }

        var grepCount = 0;
        if (obj.StartLine is not null && obj.EndLine is not null)
        {
            grepCount = grepHits.Count(g => g.LineNumber >= obj.StartLine && g.LineNumber <= obj.EndLine);
        }

        var grepScore = grepCount switch
        {
            >= 2 => 2.5 + (0.1 * grepCount),
            1 => 2.0,
            _ => 0.0
        };

        var headlineScore = !string.IsNullOrWhiteSpace(queryLower) &&
                            !string.IsNullOrWhiteSpace(obj.HeadlineText) &&
                            obj.HeadlineText.Contains(queryLower, StringComparison.Ordinal)
            ? 1.5
            : 0.0;

        double? chunkSem = null;
        if (obj.StartByte is not null && obj.EndByte is not null)
        {
            foreach (var signal in semanticSignals)
            {
                if (signal.BestChunkStart is null || signal.BestChunkEnd is null)
                    continue;

                if (signal.BestChunkStart < obj.EndByte && signal.BestChunkEnd > obj.StartByte)
                {
                    var candidate = signal.FulltextScore ?? signal.SemScore;
                    chunkSem = Math.Max(chunkSem ?? 0.0, candidate);
                }
            }
        }

        var objectScore = Math.Max(symbolScore, Math.Max(grepScore, headlineScore)) + (0.3 * (chunkSem ?? 0.0));
        return new ScoredObject(
            obj.NodeId,
            obj.DocId,
            objectScore,
            symbolScore,
            grepCount,
            chunkSem,
            headlineScore > 0,
            obj.StartLine);
    }

    private SearchResultRow BuildDocumentRow(EnrichedNode node, MergedDocScore doc, string explainJson)
    {
        var snippet = BuildSnippet(node, doc.BestChunkStart, doc.BestChunkEnd);
        var denseScore = doc.SemNorm;
        var score = Combine(doc.Bm25Norm, doc.FuzzNorm, denseScore);
        return new SearchResultRow(
            DocId: node.DocIdText,
            NodeId: node.NodeIdText,
            Uri: node.Uri,
            Path: node.Path,
            NodeScope: node.NodeScope,
            Kind: node.Kind,
            Symbol: node.Symbol,
            Lang: node.Lang,
            Mime: node.Mime,
            Headline: node.Headline,
            Structure: node.Structure,
            Snippet: snippet,
            LineStart: node.LineStart,
            LineEnd: node.LineEnd,
            Digest: node.Digest,
            Bm25Score: doc.Bm25Norm,
            FuzzyScore: doc.FuzzNorm,
            DenseScore: denseScore,
            Rrf: doc.RrfLex + doc.RrfSem,
            DocSemn: doc.SemNorm,
            Score: score,
            Confidence: ScoreConfidence(score),
            ExplainJson: explainJson);
    }

    private SearchResultRow BuildObjectRow(
        EnrichedNode node,
        MergedDocScore doc,
        ScoredObject obj,
        string explainJson,
        int docRank,
        int totalDocs)
    {
        var snippet = BuildSnippet(node, doc.BestChunkStart, doc.BestChunkEnd);
        var bm25Score = Math.Min(obj.ObjectScore / 4.5, 1.0);
        var fuzzyScore = Math.Min(obj.SymbolScore / 4.0, 1.0);
        // Use chunk-level semantic score for objects, NOT document-level SemNorm.
        // doc.SemNorm is the same for all objects in a document (often saturated at 1.0),
        // which destroys differentiation. chunk_sem varies per object based on byte-range overlap.
        var denseScore = obj.ChunkSem.HasValue
            ? Math.Clamp(obj.ChunkSem.Value, 0.0, 1.0)
            : doc.SemNorm * 0.5; // weak inheritance: object didn't overlap any chunk
        // Doc-rank boost: objects in higher-ranked documents score higher.
        // An object in the #1 doc gets +0.15, objects in the last doc get ~0.
        var docRankBoost = totalDocs > 1
            ? 0.15 * (1.0 - ((double)(docRank - 1) / (totalDocs - 1)))
            : 0.15;
        var score = Combine(bm25Score, fuzzyScore, denseScore)
            + (0.25 * Math.Min(obj.ObjectScore / 4.5, 1.0))
            + docRankBoost;

        return new SearchResultRow(
            DocId: node.DocIdText,
            NodeId: node.NodeIdText,
            Uri: node.Uri,
            Path: node.Path,
            NodeScope: node.NodeScope,
            Kind: node.Kind,
            Symbol: node.Symbol,
            Lang: node.Lang,
            Mime: node.Mime,
            Headline: node.Headline,
            Structure: node.Structure,
            Snippet: snippet,
            LineStart: node.LineStart,
            LineEnd: node.LineEnd,
            Digest: node.Digest,
            Bm25Score: bm25Score,
            FuzzyScore: fuzzyScore,
            DenseScore: denseScore,
            Rrf: doc.RrfSem,
            DocSemn: doc.SemNorm,
            Score: score,
            Confidence: ScoreConfidence(score),
            ExplainJson: explainJson);
    }

    private static SearchResultRow BuildFallbackRow(EnrichedNode node, string explainJson)
        => new(
            DocId: node.DocIdText,
            NodeId: node.NodeIdText,
            Uri: node.Uri,
            Path: node.Path,
            NodeScope: node.NodeScope,
            Kind: node.Kind,
            Symbol: node.Symbol,
            Lang: node.Lang,
            Mime: node.Mime,
            Headline: node.Headline,
            Structure: node.Structure,
            Snippet: BuildSnippet(node, null, null),
            LineStart: node.LineStart,
            LineEnd: node.LineEnd,
            Digest: node.Digest,
            Bm25Score: 0.0,
            FuzzyScore: 0.0,
            DenseScore: 0.0,
            Rrf: 0.0,
            DocSemn: 0.0,
            Score: 0.0,
            Confidence: ScoreConfidence(0.0),
            ExplainJson: explainJson);

    private static string? BuildSnippet(EnrichedNode node, long? bestChunkStart, long? bestChunkEnd)
    {
        if (!string.IsNullOrWhiteSpace(node.TextContent) &&
            bestChunkStart is not null &&
            bestChunkEnd is not null)
        {
            var chunkSnippet = ExtractChunkSnippet(node.TextContent, bestChunkStart.Value, bestChunkEnd.Value, 2);
            if (!string.IsNullOrWhiteSpace(chunkSnippet))
                return chunkSnippet;
        }

        if (string.Equals(node.NodeScope, "document", StringComparison.OrdinalIgnoreCase))
            return TrimTo(node.TextContent, 640);

        var objectSnippet = JoinNonEmpty("\n\n", node.Headline, node.Structure);
        return TrimTo(objectSnippet, 640);
    }

    private static string? ExtractChunkSnippet(string text, long startByte, long endByte, int contextLines)
    {
        var lineStarts = BuildLineStarts(text);
        var newlineByteOffsets = BuildNewlineByteOffsets(text);
        if (lineStarts.Count == 0)
            return TrimTo(text, 640);

        var totalLines = lineStarts.Count;
        var startLine = LineForByteOffset(newlineByteOffsets, startByte);
        var endLine = LineForByteOffset(newlineByteOffsets, endByte);
        var fromLine = Math.Max(1, startLine - contextLines);
        var toLine = Math.Min(totalLines, endLine + contextLines);
        return SliceLines(text, lineStarts, fromLine, toLine);
    }

    private static List<int> BuildLineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
                starts.Add(i + 1);
        }
        return starts;
    }

    private static List<long> BuildNewlineByteOffsets(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var offsets = new List<long>();
        for (long i = 0; i < bytes.LongLength; i++)
        {
            if (bytes[i] == (byte)'\n')
                offsets.Add(i);
        }
        return offsets;
    }

    private static int LineForByteOffset(IReadOnlyList<long> newlineByteOffsets, long byteOffset)
    {
        if (newlineByteOffsets.Count == 0)
            return 1;

        var low = 0;
        var high = newlineByteOffsets.Count;
        while (low < high)
        {
            var mid = low + ((high - low) / 2);
            if (newlineByteOffsets[mid] < byteOffset)
                low = mid + 1;
            else
                high = mid;
        }

        return low + 1;
    }

    private static string SliceLines(string text, IReadOnlyList<int> lineStarts, int startLine, int endLine)
    {
        var startIndex = lineStarts[Math.Max(0, startLine - 1)];
        var endIndex = endLine < lineStarts.Count ? lineStarts[endLine] : text.Length;
        if (endIndex <= startIndex)
            return string.Empty;

        return text[startIndex..endIndex].TrimEnd('\r', '\n');
    }

    private static int RankBias(SearchResultRow row, string queryLower, bool noScope)
    {
        if (!noScope && string.Equals(row.NodeScope, "document", StringComparison.OrdinalIgnoreCase))
            return 0;

        if (!noScope)
            return 1;

        if (!string.IsNullOrWhiteSpace(queryLower) &&
            !string.IsNullOrWhiteSpace(row.Symbol) &&
            string.Equals(row.Symbol, queryLower, StringComparison.OrdinalIgnoreCase))
            return -1;

        return 0;
    }

    /// <summary>
    /// Combine search signals into a final score. Uses a weighted blend (60%) plus
    /// a best-signal boost (40%). The previous MAX + 0.2*blend formula gave MAX 83%
    /// weight, causing score saturation when semantic was 1.0 for all results.
    /// </summary>
    private static double Combine(double bm25Norm, double fuzzNorm, double semNorm, double wb = 0.15, double wf = 0.15, double ws = 0.70)
    {
        var blend = (wb * bm25Norm) + (wf * fuzzNorm) + (ws * semNorm);
        var best = Math.Max(bm25Norm, Math.Max(fuzzNorm, semNorm));
        return (0.6 * blend) + (0.4 * best);
    }

    private static double ScoreConfidence(double score)
        => score switch
        {
            >= 2.0 => 0.95,
            >= 1.2 => 0.80,
            >= 0.8 => 0.65,
            _ => 0.40
        };

    private static string SearchClassifyQuery(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return "empty";

        if (query.Contains("::", StringComparison.Ordinal) ||
            query.Contains('.', StringComparison.Ordinal) ||
            query.Contains("()", StringComparison.Ordinal))
            return "symbol";

        return "auto";
    }

    private static string JoinGuids(IEnumerable<Guid> ids)
        => string.Join(",", ids.Select(id => $"'{id:D}'::UUID"));

    private IReentrantReader? ResolveReader()
        => _reader ?? DuckDbDataStore.GetAmbientReentrantReader();

    private static double GetDoubleOrZero(IDataRecord record, int ordinal)
        => GetDoubleOrNull(record, ordinal) ?? 0.0;

    private static double? GetDoubleOrNull(IDataRecord record, int ordinal)
        => record.IsDBNull(ordinal)
            ? null
            : Convert.ToDouble(record.GetValue(ordinal), CultureInfo.InvariantCulture);

    private static int? GetIntOrNull(IDataRecord record, int ordinal)
        => record.IsDBNull(ordinal)
            ? null
            : Convert.ToInt32(record.GetValue(ordinal), CultureInfo.InvariantCulture);

    private static long GetLongOrZero(IDataRecord record, int ordinal)
        => GetLongOrNull(record, ordinal) ?? 0L;

    private static long? GetLongOrNull(IDataRecord record, int ordinal)
        => record.IsDBNull(ordinal)
            ? null
            : Convert.ToInt64(record.GetValue(ordinal), CultureInfo.InvariantCulture);

    private static string GetStringOrEmpty(IDataRecord record, int ordinal)
        => GetStringOrNull(record, ordinal) ?? string.Empty;

    private static string? GetStringOrNull(IDataRecord record, int ordinal)
        => record.IsDBNull(ordinal) ? null : Convert.ToString(record.GetValue(ordinal), CultureInfo.InvariantCulture);

    private static string EscapeSql(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    private static string? JoinNonEmpty(string separator, params string?[] values)
    {
        var items = values.Where(v => !string.IsNullOrWhiteSpace(v)).ToArray();
        return items.Length == 0 ? null : string.Join(separator, items);
    }

    private static string? TrimTo(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        return value.Length <= maxLength ? value : value[..maxLength];
    }

    private sealed record LexDocScore(
        Guid NodeId,
        Guid DocId,
        double Bm25Score,
        double FuzzyScore,
        double Bm25Norm,
        double FuzzNorm,
        long LexRank,
        double RrfLex);

    private sealed record SemDocScore(
        Guid NodeId,
        Guid DocId,
        double SemScore,
        double SemNorm,
        long SemRank,
        double RrfSem,
        string SearchSource,
        double? StructureScore,
        double? FulltextScore,
        int? BestChunkIndex,
        long? BestChunkStart,
        long? BestChunkEnd);

    private sealed record MergedDocScore(
        Guid NodeId,
        Guid DocId,
        double Bm25Score,
        double FuzzyScore,
        double Bm25Norm,
        double FuzzNorm,
        double SemScore,
        double SemNorm,
        double RrfLex,
        double RrfSem,
        string SearchSource,
        double? StructureScore,
        double? FulltextScore,
        int? BestChunkIndex,
        long? BestChunkStart,
        long? BestChunkEnd);

    private sealed record ObjectCandidate(
        Guid NodeId,
        Guid DocId,
        int? StartLine,
        int? EndLine,
        long? StartByte,
        long? EndByte,
        string SymbolKey,
        string HeadlineText);

    private sealed record GrepHit(Guid DocId, int LineNumber);

    private sealed record ScoredObject(
        Guid NodeId,
        Guid DocId,
        double ObjectScore,
        double SymbolScore,
        int GrepHits,
        double? ChunkSem,
        bool HeadlineHit,
        int? StartLine);

    private sealed record EnrichedNode(
        Guid NodeId,
        Guid DocId,
        string? Uri,
        string? Path,
        string? NodeScope,
        string? Kind,
        string? Symbol,
        string? Lang,
        string? Mime,
        string? Headline,
        string? Structure,
        int? LineStart,
        int? LineEnd,
        string? Digest,
        string? TextContent)
    {
        public string NodeIdText => NodeId.ToString("D");
        public string DocIdText => DocId.ToString("D");
    }

    public sealed record SearchResultRow(
        string DocId,
        string NodeId,
        string? Uri,
        string? Path,
        string? NodeScope,
        string? Kind,
        string? Symbol,
        string? Lang,
        string? Mime,
        string? Headline,
        string? Structure,
        string? Snippet,
        int? LineStart,
        int? LineEnd,
        string? Digest,
        double Bm25Score,
        double FuzzyScore,
        double DenseScore,
        double Rrf,
        double DocSemn,
        double Score,
        double Confidence,
        string ExplainJson);
}
