using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RepoQL.Contracts;
using RepoQL.Contracts.Embeddings;
using RepoQL.Data.DuckDB;
using RepoQL.Data.DuckDB.UdfFramework;

namespace RepoQL.Data.DuckDB.UdfImplementations;

/// <summary>
/// Purpose: Refine semantic chunk ranges via binary chop and local embeddings.
/// Complexity: Parses JSON input, loads artifact text once, precomputes a bounded
/// split tree, runs batched embeddings, and emits refined line ranges with scores.
/// </summary>
[UdfClass]
public sealed class ZoomAndEnhanceUdf(
    IReentrantReader reader,
    IEmbeddingProvider? embeddingProvider = null,
    ILogger<ZoomAndEnhanceUdf>? logger = null)
{
    private readonly IReentrantReader _reader = reader ?? throw new ArgumentNullException(nameof(reader));
    private readonly IEmbeddingProvider? _embeddingProvider = embeddingProvider;
    private readonly ILogger<ZoomAndEnhanceUdf> _logger = logger ?? NullLogger<ZoomAndEnhanceUdf>.Instance;

    private const int DefaultMaxDepth = 2;
    private const int DefaultMinLines = 8;
    private const double DefaultThreshold = 0.2;
    private const int MaxBatchSize = 512;
    private static readonly TimeSpan QueryEmbeddingTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan BatchEmbeddingTimeout = TimeSpan.FromMinutes(5);

    private static readonly ConcurrentDictionary<string, CacheEntry> EmbeddingCache = new();
    private static readonly TimeSpan CacheExpiry = TimeSpan.FromSeconds(60);
    private const int MaxCacheSize = 600;
    private static long _cacheAccessCounter;

    [StructuredUdf("_zoom_and_enhance_internal", MacroName = "zoom_and_enhance",
        Description = "Refine semantic chunks with BFS binary chop and local embeddings")]
    public IEnumerable<RefinedChunkRow> ZoomAndEnhance(
        string chunks_json,
        string query,
        [UdfDefault("8")] int min_lines,
        [UdfDefault("2")] int max_depth,
        [UdfDefault("0.2")] double threshold)
    {
        min_lines = min_lines <= 0 ? DefaultMinLines : min_lines;
        max_depth = max_depth < 0 ? DefaultMaxDepth : max_depth;
        threshold = threshold < 0 ? DefaultThreshold : threshold;

        var inputs = ParseInputs(chunks_json);
        if (inputs.Count == 0 || string.IsNullOrWhiteSpace(query))
            return [];

        var documents = LoadDocuments(inputs);
        if (documents.Count == 0)
            return [];

        // If embeddings are unavailable, return base ranges with original scores.
        if (_embeddingProvider?.Enabled != true)
            return BuildBaseRows(inputs, documents);

        float[]? queryEmbedding;
        try
        {
            queryEmbedding = RunEmbeddingWithTimeout(
                ct => _embeddingProvider.EmbedQueryAsync(query, ct),
                QueryEmbeddingTimeout,
                "query embedding");
        }
        catch (TimeoutException)
        {
            // Timeout is treated as a partial capability loss, not a hard failure.
            return BuildBaseRows(inputs, documents);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "zoom_and_enhance: query embedding failed");
            return BuildBaseRows(inputs, documents);
        }

        if (queryEmbedding is null)
            return BuildBaseRows(inputs, documents);

        var roots = new List<WorkItem>(inputs.Count);
        foreach (var input in inputs)
        {
            if (!documents.TryGetValue(input.DocumentUri, out var doc))
                continue;

            if (!TryResolveLineRange(input, doc, out var startLine, out var endLine))
                continue;

            roots.Add(new WorkItem(
                doc,
                startLine,
                endLine,
                SemanticScore: input.Score,
                FinalScore: input.Score,
                Depth: 0));
        }

        if (roots.Count == 0)
            return [];

        var queryTerms = ParseQueryTerms(query);
        var results = new List<RefinedChunkRow>();
        RunPrecomputedTree(roots, queryEmbedding, queryTerms, min_lines, max_depth, threshold, results);
        return SnapResultsToObjects(results);
    }

    public sealed record RefinedChunkRow(
        string Uri,
        int StartLine,
        int EndLine,
        double Score,
        int Depth);

    private sealed record InputChunk(
        string Uri,
        string DocumentUri,
        long? StartByte,
        long? EndByte,
        int? StartLine,
        int? EndLine,
        double Score);

    private sealed record DocumentText(
        string Uri,
        string Text,
        string Preamble,
        int[] LineStarts,
        long[] NewlineByteOffsets,
        long ByteLength);

    private sealed record WorkItem(
        DocumentText Doc,
        int StartLine,
        int EndLine,
        double SemanticScore,
        double FinalScore,
        int Depth);

    private sealed record SplitRequest(
        WorkItem Parent,
        int LeftStart,
        int LeftEnd,
        int RightStart,
        int RightEnd,
        string LeftRawText,
        string RightRawText,
        string LeftText,
        string RightText);

    private sealed class PrecomputedNode
    {
        public required WorkItem Item { get; set; }
        public string? RawText { get; init; }
        public string? FullText { get; init; }
        public PrecomputedNode? Left { get; set; }
        public PrecomputedNode? Right { get; set; }
    }

    private sealed record SnapCandidate(
        string Uri,
        int StartLine,
        int EndLine,
        string? Headline,
        string Kind);

    private sealed class CacheEntry
    {
        public required float[] Embedding { get; init; }
        public DateTime ExpiresAtUtc { get; set; }
        public long LastAccess { get; set; }
    }

    private static List<InputChunk> ParseInputs(string? chunksJson)
    {
        if (string.IsNullOrWhiteSpace(chunksJson))
            return [];

        try
        {
            using var doc = JsonDocument.Parse(chunksJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return [];

            var list = new List<InputChunk>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object)
                    continue;

                var uri = ReadString(el, "uri");
                if (string.IsNullOrWhiteSpace(uri))
                    continue;

                var docUri = GetContainerUri(uri);
                if (string.IsNullOrWhiteSpace(docUri))
                    continue;

                var startByte = ReadLong(el, "start_byte");
                var endByte = ReadLong(el, "end_byte");
                var startLine = ReadInt(el, "start_line", "start");
                var endLine = ReadInt(el, "end_line", "end");
                var score = ReadDouble(el, "score") ?? 0.0;
                if (double.IsNaN(score) || double.IsInfinity(score))
                    score = 0.0;

                list.Add(new InputChunk(
                    Uri: uri,
                    DocumentUri: docUri,
                    StartByte: startByte,
                    EndByte: endByte,
                    StartLine: startLine,
                    EndLine: endLine,
                    Score: score));
            }

            return list;
        }
        catch
        {
            return [];
        }
    }

    internal static string[] ParseQueryTerms(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var terms = new List<string>();

        foreach (var part in query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var lowered = part.Trim().ToLowerInvariant();
            if (lowered.Length <= 1 || !seen.Add(lowered))
                continue;

            terms.Add(lowered);
        }

        return terms.ToArray();
    }

    internal static double TermCoverage(string text, string[] terms)
    {
        if (string.IsNullOrEmpty(text) || terms.Length == 0)
            return 0.0;

        var lowered = text.ToLowerInvariant();
        var matched = 0;
        foreach (var term in terms)
        {
            if (lowered.Contains(term, StringComparison.Ordinal))
                matched++;
        }

        return matched == 0 ? 0.0 : matched / (double)terms.Length;
    }

    private Dictionary<string, DocumentText> LoadDocuments(IReadOnlyList<InputChunk> inputs)
    {
        var uris = inputs.Select(i => i.DocumentUri)
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (uris.Count == 0)
            return new Dictionary<string, DocumentText>(StringComparer.OrdinalIgnoreCase);

        var uriList = string.Join(",", uris.Select(u => $"'{EscapeSql(u)}'"));
        var sql = $"""
            SELECT n.uri, a.text_content, a.headline, a.summary
            FROM node n
            JOIN artifact a ON a.id = n.artifact_id
            WHERE n.uri IN ({uriList})
            """;

        var rows = _reader.Read(sql, r =>
        {
            var uri = r.IsDBNull(0) ? "" : r.GetString(0);
            var text = r.IsDBNull(1) ? "" : r.GetString(1);
            var headline = r.IsDBNull(2) ? null : r.GetString(2);
            var summary = r.IsDBNull(3) ? null : r.GetString(3);

            var preamble = BuildPreamble(headline, summary);
            var lineStarts = BuildLineStarts(text);
            var newlineBytes = BuildNewlineByteOffsets(text, out var byteLen);

            return new DocumentText(
                Uri: uri,
                Text: text,
                Preamble: preamble,
                LineStarts: lineStarts,
                NewlineByteOffsets: newlineBytes,
                ByteLength: byteLen);
        });

        return rows
            .Where(r => !string.IsNullOrWhiteSpace(r.Uri))
            .ToDictionary(r => r.Uri, r => r, StringComparer.OrdinalIgnoreCase);
    }

    private void RunBfs(
        Queue<WorkItem> queue,
        float[] queryEmbedding,
        string[] queryTerms,
        int minLines,
        int maxDepth,
        double threshold,
        List<RefinedChunkRow> results)
    {
        while (queue.Count > 0)
        {
            var depth = queue.Peek().Depth;
            var batch = new List<SplitRequest>();

            while (queue.Count > 0 && queue.Peek().Depth == depth && batch.Count < MaxBatchSize)
            {
                var item = queue.Dequeue();
                var lineCount = item.EndLine - item.StartLine + 1;

                if (lineCount < minLines || item.Depth >= maxDepth)
                {
                    results.Add(new RefinedChunkRow(
                        item.Doc.Uri,
                        item.StartLine,
                        item.EndLine,
                        item.FinalScore,
                        item.Depth));
                    continue;
                }

                var mid = item.StartLine + (lineCount / 2);
                if (!TryBuildHalf(item.Doc, item.StartLine, mid, out var leftRawText, out var leftText) ||
                    !TryBuildHalf(item.Doc, mid + 1, item.EndLine, out var rightRawText, out var rightText))
                {
                    results.Add(new RefinedChunkRow(
                        item.Doc.Uri,
                        item.StartLine,
                        item.EndLine,
                        item.FinalScore,
                        item.Depth));
                    continue;
                }

                batch.Add(new SplitRequest(
                    Parent: item,
                    LeftStart: item.StartLine,
                    LeftEnd: mid,
                    RightStart: mid + 1,
                    RightEnd: item.EndLine,
                    LeftRawText: leftRawText,
                    RightRawText: rightRawText,
                    LeftText: leftText,
                    RightText: rightText));
            }

            if (batch.Count == 0)
                continue;

            var texts = new List<string>(batch.Count * 2);
            foreach (var split in batch)
            {
                texts.Add(split.LeftText);
                texts.Add(split.RightText);
            }

            var embeddings = GetOrComputeBatch(texts);

            for (var i = 0; i < batch.Count; i++)
            {
                var split = batch[i];
                var leftEmbedding = embeddings[i * 2];
                var rightEmbedding = embeddings[i * 2 + 1];

                var leftSemanticScore = leftEmbedding is null ? 0.0 : CosineSimilarity(queryEmbedding, leftEmbedding);
                var rightSemanticScore = rightEmbedding is null ? 0.0 : CosineSimilarity(queryEmbedding, rightEmbedding);
                var leftFinalScore = leftSemanticScore + (0.10 * TermCoverage(split.LeftRawText, queryTerms));
                var rightFinalScore = rightSemanticScore + (0.10 * TermCoverage(split.RightRawText, queryTerms));

                var any = false;
                if (leftSemanticScore >= threshold)
                {
                    queue.Enqueue(split.Parent with
                    {
                        StartLine = split.LeftStart,
                        EndLine = split.LeftEnd,
                        SemanticScore = leftSemanticScore,
                        FinalScore = leftFinalScore,
                        Depth = split.Parent.Depth + 1
                    });
                    any = true;
                }

                if (rightSemanticScore >= threshold)
                {
                    queue.Enqueue(split.Parent with
                    {
                        StartLine = split.RightStart,
                        EndLine = split.RightEnd,
                        SemanticScore = rightSemanticScore,
                        FinalScore = rightFinalScore,
                        Depth = split.Parent.Depth + 1
                    });
                    any = true;
                }

                if (!any)
                {
                    results.Add(new RefinedChunkRow(
                        split.Parent.Doc.Uri,
                        split.Parent.StartLine,
                        split.Parent.EndLine,
                        split.Parent.FinalScore,
                        split.Parent.Depth));
                }
            }
        }
    }

    private void RunPrecomputedTree(
        IReadOnlyList<WorkItem> roots,
        float[] queryEmbedding,
        string[] queryTerms,
        int minLines,
        int maxDepth,
        double threshold,
        List<RefinedChunkRow> results)
    {
        var embeddableNodes = new List<PrecomputedNode>();
        var rootNodes = new List<PrecomputedNode>(roots.Count);

        foreach (var root in roots)
            rootNodes.Add(BuildPrecomputedTree(root, minLines, maxDepth, embeddableNodes));

        if (embeddableNodes.Count > 0)
        {
            var embeddings = GetOrComputeBatch(embeddableNodes
                .Select(static node => node.FullText!)
                .ToList());

            for (var i = 0; i < embeddableNodes.Count; i++)
            {
                var node = embeddableNodes[i];
                var embedding = embeddings[i];
                var semanticScore = embedding is null ? 0.0 : CosineSimilarity(queryEmbedding, embedding);
                var finalScore = semanticScore + (0.10 * TermCoverage(node.RawText ?? string.Empty, queryTerms));

                node.Item = node.Item with
                {
                    SemanticScore = semanticScore,
                    FinalScore = finalScore
                };
            }
        }

        foreach (var root in rootNodes)
            TraversePrecomputedTree(root, threshold, results, isRoot: true);
    }

    private PrecomputedNode BuildPrecomputedTree(
        WorkItem item,
        int minLines,
        int maxDepth,
        List<PrecomputedNode> embeddableNodes,
        string? rawText = null,
        string? fullText = null)
    {
        var node = new PrecomputedNode
        {
            Item = item,
            RawText = rawText,
            FullText = fullText
        };

        if (fullText is not null)
            embeddableNodes.Add(node);

        var lineCount = item.EndLine - item.StartLine + 1;
        if (lineCount < minLines || item.Depth >= maxDepth)
            return node;

        var mid = item.StartLine + (lineCount / 2);
        if (!TryBuildHalf(item.Doc, item.StartLine, mid, out var leftRawText, out var leftText) ||
            !TryBuildHalf(item.Doc, mid + 1, item.EndLine, out var rightRawText, out var rightText))
            return node;

        node.Left = BuildPrecomputedTree(
            item with
            {
                StartLine = item.StartLine,
                EndLine = mid,
                SemanticScore = 0.0,
                FinalScore = 0.0,
                Depth = item.Depth + 1
            },
            minLines,
            maxDepth,
            embeddableNodes,
            leftRawText,
            leftText);

        node.Right = BuildPrecomputedTree(
            item with
            {
                StartLine = mid + 1,
                EndLine = item.EndLine,
                SemanticScore = 0.0,
                FinalScore = 0.0,
                Depth = item.Depth + 1
            },
            minLines,
            maxDepth,
            embeddableNodes,
            rightRawText,
            rightText);

        return node;
    }

    private static void TraversePrecomputedTree(
        PrecomputedNode node,
        double threshold,
        List<RefinedChunkRow> results,
        bool isRoot)
    {
        if (node.Left is null || node.Right is null)
        {
            results.Add(new RefinedChunkRow(
                node.Item.Doc.Uri,
                node.Item.StartLine,
                node.Item.EndLine,
                node.Item.FinalScore,
                node.Item.Depth));
            return;
        }

        if (!isRoot && node.Item.SemanticScore < threshold)
        {
            results.Add(new RefinedChunkRow(
                node.Item.Doc.Uri,
                node.Item.StartLine,
                node.Item.EndLine,
                node.Item.FinalScore,
                node.Item.Depth));
            return;
        }

        var any = false;
        if (node.Left.Item.SemanticScore >= threshold)
        {
            TraversePrecomputedTree(node.Left, threshold, results, isRoot: false);
            any = true;
        }

        if (node.Right.Item.SemanticScore >= threshold)
        {
            TraversePrecomputedTree(node.Right, threshold, results, isRoot: false);
            any = true;
        }

        if (!any)
        {
            results.Add(new RefinedChunkRow(
                node.Item.Doc.Uri,
                node.Item.StartLine,
                node.Item.EndLine,
                node.Item.FinalScore,
                node.Item.Depth));
        }
    }

    private float[]?[] GetOrComputeBatch(IReadOnlyList<string> texts)
    {
        var results = new float[]?[texts.Count];
        var uncachedTexts = new List<string>();
        var uncachedIndices = new List<int>();
        var uncachedKeys = new List<string>();

        for (var i = 0; i < texts.Count; i++)
        {
            var key = BuildCacheKey(texts[i]);
            if (TryGetCached(key) is { } cached)
            {
                results[i] = cached;
            }
            else
            {
                uncachedTexts.Add(texts[i]);
                uncachedIndices.Add(i);
                uncachedKeys.Add(key);
            }
        }

        if (uncachedTexts.Count == 0)
            return results;

        float[]?[] computed;
        try
        {
            computed = RunEmbeddingWithTimeout(
                ct => _embeddingProvider!.EmbedPassageBatchAsync(uncachedTexts, ct),
                BatchEmbeddingTimeout,
                "passage batch embedding");
        }
        catch (TimeoutException)
        {
            return results;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "zoom_and_enhance: passage batch embedding failed");
            return results;
        }

        for (var j = 0; j < uncachedIndices.Count; j++)
        {
            var idx = uncachedIndices[j];
            var embedding = computed != null && j < computed.Length ? computed[j] : null;
            results[idx] = embedding;
            if (embedding is not null)
                AddCache(uncachedKeys[j], embedding);
        }

        return results;
    }

    private IReadOnlyList<RefinedChunkRow> BuildBaseRows(
        IReadOnlyList<InputChunk> inputs,
        IReadOnlyDictionary<string, DocumentText> documents)
        => inputs
            .Select(i => TryBuildBaseRow(i, documents))
            .Where(r => r is not null)
            .Cast<RefinedChunkRow>()
            .ToList();

    private T RunEmbeddingWithTimeout<T>(
        Func<CancellationToken, Task<T>> operation,
        TimeSpan timeout,
        string operationName)
    {
        using var timeoutCts = new CancellationTokenSource();
        var task = Task.Run(
            async () => await operation(timeoutCts.Token).ConfigureAwait(false),
            CancellationToken.None);

        try
        {
            return task.WaitAsync(timeout).GetAwaiter().GetResult();
        }
        catch (TimeoutException)
        {
            timeoutCts.Cancel();
            _logger.LogWarning(
                "zoom_and_enhance: {Operation} timed out after {Timeout}.",
                operationName,
                timeout);
            throw;
        }
    }

    private static string BuildCacheKey(string text)
    {
        // Keep cache keys bounded regardless of input size.
        var bytes = Encoding.UTF8.GetBytes(text);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash, 0, 16); // 128-bit prefix
    }

    // Test hooks: avoid reflection-based test access and keep cache invariants verifiable.
    internal static string BuildCacheKeyForTests(string text) => BuildCacheKey(text);

    internal static int CacheEntryCountForTests => EmbeddingCache.Count;

    internal static int MaxCacheSizeForTests => MaxCacheSize;

    internal static void ClearCacheForTests() => EmbeddingCache.Clear();

    internal void AddCacheForTests(string key, float[] embedding) => AddCache(key, embedding);

    private float[]? TryGetCached(string key)
    {
        var cacheKey = $"{_embeddingProvider?.Model ?? "unknown"}:passage:{key}";
        if (EmbeddingCache.TryGetValue(cacheKey, out var entry))
        {
            var now = DateTime.UtcNow;
            if (now <= entry.ExpiresAtUtc)
            {
                // Sliding TTL keeps hot entries alive and supports deterministic LRU eviction.
                entry.ExpiresAtUtc = now + CacheExpiry;
                entry.LastAccess = Interlocked.Increment(ref _cacheAccessCounter);
                return entry.Embedding;
            }
            EmbeddingCache.TryRemove(cacheKey, out _);
        }
        return null;
    }

    private void AddCache(string key, float[] embedding)
    {
        var cacheKey = $"{_embeddingProvider?.Model ?? "unknown"}:passage:{key}";
        var now = DateTime.UtcNow;
        EmbeddingCache[cacheKey] = new CacheEntry
        {
            Embedding = embedding,
            ExpiresAtUtc = now + CacheExpiry,
            LastAccess = Interlocked.Increment(ref _cacheAccessCounter)
        };

        TrimCache(now);
    }

    private static void TrimCache(DateTime now)
    {
        if (EmbeddingCache.Count <= MaxCacheSize)
            return;

        // First pass: remove expired entries.
        foreach (var (key, entry) in EmbeddingCache)
        {
            if (entry.ExpiresAtUtc <= now)
                EmbeddingCache.TryRemove(key, out _);
        }

        if (EmbeddingCache.Count <= MaxCacheSize)
            return;

        // Second pass: enforce hard cap by evicting least recently used entries.
        var overflow = EmbeddingCache.Count - MaxCacheSize;
        if (overflow <= 0)
            return;

        var evictionKeys = EmbeddingCache
            .OrderBy(static kvp => kvp.Value.LastAccess)
            .Take(overflow)
            .Select(static kvp => kvp.Key)
            .ToList();

        foreach (var key in evictionKeys)
            EmbeddingCache.TryRemove(key, out _);
    }

    private static bool TryResolveLineRange(InputChunk input, DocumentText doc, out int startLine, out int endLine)
    {
        var lineCount = doc.LineStarts.Length;
        if (lineCount == 0 || string.IsNullOrEmpty(doc.Text))
        {
            startLine = 0;
            endLine = 0;
            return false;
        }

        if (input.StartByte.HasValue || input.EndByte.HasValue)
        {
            startLine = LineForByteOffset(doc, input.StartByte ?? 0);
            endLine = LineForByteOffset(doc, input.EndByte ?? doc.ByteLength);
        }
        else
        {
            startLine = input.StartLine ?? 1;
            endLine = input.EndLine ?? lineCount;
        }

        startLine = Math.Clamp(startLine, 1, lineCount);
        endLine = Math.Clamp(endLine, startLine, lineCount);
        return true;
    }

    private static bool TryBuildHalf(DocumentText doc, int startLine, int endLine, out string rawText, out string text)
    {
        rawText = string.Empty;
        text = string.Empty;
        if (!TrySliceLines(doc, startLine, endLine, out var slice))
            return false;

        if (string.IsNullOrWhiteSpace(slice))
            return false;

        rawText = slice;
        if (string.IsNullOrWhiteSpace(doc.Preamble))
        {
            text = slice;
            return true;
        }

        text = $"{doc.Preamble}\n\n{slice}";
        return true;
    }

    private static bool TryBuildHalfText(DocumentText doc, int startLine, int endLine, out string text)
        => TryBuildHalf(doc, startLine, endLine, out _, out text);

    private static bool TrySliceLines(DocumentText doc, int startLine, int endLine, out string slice)
    {
        slice = string.Empty;
        var lineCount = doc.LineStarts.Length;
        if (lineCount == 0)
            return false;

        startLine = Math.Clamp(startLine, 1, lineCount);
        endLine = Math.Clamp(endLine, startLine, lineCount);

        var startIndex = doc.LineStarts[startLine - 1];
        var endIndex = endLine < lineCount ? doc.LineStarts[endLine] : doc.Text.Length;
        if (endIndex < startIndex)
            return false;

        slice = doc.Text.Substring(startIndex, endIndex - startIndex);
        return true;
    }

    private static int LineForByteOffset(DocumentText doc, long offset)
    {
        if (offset < 0) offset = 0;
        if (offset > doc.ByteLength) offset = doc.ByteLength;

        if (doc.NewlineByteOffsets.Length == 0)
            return 1;

        var idx = Array.BinarySearch(doc.NewlineByteOffsets, offset);
        var count = idx >= 0 ? idx : ~idx;
        return count + 1;
    }

    private static string BuildPreamble(string? headline, string? summary)
    {
        var parts = new List<string>(2);
        if (!string.IsNullOrWhiteSpace(headline))
            parts.Add(headline);
        if (!string.IsNullOrWhiteSpace(summary))
            parts.Add(summary);
        return parts.Count == 0 ? "" : string.Join("\n", parts);
    }

    private static int[] BuildLineStarts(string text)
    {
        var starts = new List<int>(Math.Max(8, text.Length / 32)) { 0 };
        for (var i = 0; i < text.Length; i++)
            if (text[i] == '\n') starts.Add(i + 1);
        return starts.ToArray();
    }

    private static long[] BuildNewlineByteOffsets(string text, out long byteLength)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        byteLength = bytes.LongLength;
        if (byteLength == 0)
            return [];

        var list = new List<long>();
        for (long i = 0; i < byteLength; i++)
        {
            if (bytes[i] == (byte)'\n')
                list.Add(i);
        }
        return list.ToArray();
    }

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

    private static string EscapeSql(string value) => value.Replace("'", "''");

    private static string GetContainerUri(string uri)
    {
        if (RepoUri.TryParse(uri, out var repoUri))
            return repoUri.Container.AbsoluteUri;

        var hash = uri.IndexOf('#', StringComparison.Ordinal);
        return hash > 0 ? uri[..hash] : uri;
    }

    private static string? ReadString(JsonElement element, string name)
        => element.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;

    private static long? ReadLong(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var prop) || prop.ValueKind == JsonValueKind.Null)
            return null;

        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt64(out var l))
            return l;

        if (prop.ValueKind == JsonValueKind.String && long.TryParse(prop.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out l))
            return l;

        return null;
    }

    private static int? ReadInt(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var prop) || prop.ValueKind == JsonValueKind.Null)
                continue;

            if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var i))
                return i;

            if (prop.ValueKind == JsonValueKind.String && int.TryParse(prop.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out i))
                return i;
        }

        return null;
    }

    private static double? ReadDouble(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var prop) || prop.ValueKind == JsonValueKind.Null)
            return null;

        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetDouble(out var d))
            return d;

        if (prop.ValueKind == JsonValueKind.String && double.TryParse(prop.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out d))
            return d;

        return null;
    }

    private RefinedChunkRow? TryBuildBaseRow(
        InputChunk input,
        IReadOnlyDictionary<string, DocumentText> documents)
    {
        if (!documents.TryGetValue(input.DocumentUri, out var doc))
            return null;

        if (!TryResolveLineRange(input, doc, out var startLine, out var endLine))
            return null;

        return new RefinedChunkRow(doc.Uri, startLine, endLine, input.Score, Depth: 0);
    }

    private IReadOnlyList<RefinedChunkRow> SnapResultsToObjects(IReadOnlyList<RefinedChunkRow> results)
    {
        if (results.Count == 0)
            return results;

        var snapped = TrySnapResultsToObjects(results);
        var deduped = new Dictionary<string, RefinedChunkRow>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in snapped)
        {
            var key = $"{row.Uri}|{row.StartLine}|{row.EndLine}";
            if (!deduped.TryGetValue(key, out var existing) || row.Score > existing.Score)
                deduped[key] = row;
        }

        return deduped.Values.ToList();
    }

    private IReadOnlyList<RefinedChunkRow> TrySnapResultsToObjects(IReadOnlyList<RefinedChunkRow> results)
    {
        var uris = results
            .Select(static row => row.Uri)
            .Where(static uri => !string.IsNullOrWhiteSpace(uri))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (uris.Count == 0)
            return results;

        try
        {
            var uriList = string.Join(",", uris.Select(u => $"'{EscapeSql(u)}'"));
            var sql = $"""
                SELECT doc.uri, sp.start_line, sp.end_line, child.headline, child.kind
                FROM node doc
                JOIN span sp ON sp.document_id = doc.id
                JOIN node child ON child.span_id = sp.id
                WHERE doc.uri IN ({uriList})
                  AND doc.kind = 'document'
                  AND child.kind != 'document'
                  AND sp.start_line IS NOT NULL
                  AND sp.end_line IS NOT NULL
                """;

            var candidates = _reader.Read(sql, r => new SnapCandidate(
                Uri: r.IsDBNull(0) ? "" : r.GetString(0),
                StartLine: r.IsDBNull(1) ? 0 : r.GetInt32(1),
                EndLine: r.IsDBNull(2) ? 0 : r.GetInt32(2),
                Headline: r.IsDBNull(3) ? null : r.GetString(3),
                Kind: r.IsDBNull(4) ? "" : r.GetString(4)));

            var byUri = candidates
                .Where(c => !string.IsNullOrWhiteSpace(c.Uri) && c.StartLine > 0 && c.EndLine >= c.StartLine)
                .GroupBy(c => c.Uri, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

            var snapped = new List<RefinedChunkRow>(results.Count);
            foreach (var row in results)
            {
                if (!byUri.TryGetValue(row.Uri, out var objects))
                {
                    snapped.Add(row);
                    continue;
                }

                var snippetSize = row.EndLine - row.StartLine + 1;
                var maxObjectSize = Math.Max(snippetSize * 2, 40);

                var best = objects
                    .Where(o => o.StartLine <= row.StartLine && o.EndLine >= row.EndLine)
                    .Select(o => new
                    {
                        Candidate = o,
                        Size = o.EndLine - o.StartLine + 1
                    })
                    .Where(x => x.Size <= maxObjectSize)
                    .OrderBy(x => x.Size)
                    .ThenBy(x => x.Candidate.StartLine)
                    .FirstOrDefault();

                if (best is null)
                {
                    snapped.Add(row);
                    continue;
                }

                snapped.Add(row with
                {
                    StartLine = best.Candidate.StartLine,
                    EndLine = best.Candidate.EndLine
                });
            }

            return snapped;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "zoom_and_enhance: object snap query failed");
            return results;
        }
    }
}
