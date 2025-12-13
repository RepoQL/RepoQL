using System.Data;
using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
using DuckDB.NET.Data;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RepoQL.Contracts.Embeddings;

namespace RepoQL.Data.DuckDB;

/// <summary>
/// Handles async embedding pipeline for document content.
/// Uses producer-consumer pattern with channels for pipelined embedding generation and DB writes.
/// </summary>
public sealed class EmbeddingRefresher
{
    #region Constants

    // Chunking constants: BGE model has 512 token limit (~2000 chars for code).
    // Small files get a single embedding; large files are chunked with overlap.
    private const int ChunkSizeChars = 1500;          // Target chunk size (~375 tokens)
    private const int ChunkOverlapChars = 150;        // 10% overlap for context continuity
    private const int SmallFileThresholdChars = 2000; // Files under this size = single embedding
    private const int LargeFileThresholdBytes = 150 * 1024; // 150KB - files above this use structure-only embedding

    private const int MaxDocumentPayloadChars = int.MaxValue;
    private const string DocumentEmbeddingScope = "document";
    private const string FullEmbeddingType = "full";

    // Batch size for embedding. Override with REPOQL_EMBED_BATCH_SIZE env var.
    private const int DefaultEmbeddingBatchSize = 256;

    #endregion

    private readonly DuckDbDataStore _store;
    private readonly ILogger<EmbeddingRefresher> _logger;

    public EmbeddingRefresher(DuckDbDataStore store, ILogger<EmbeddingRefresher>? logger = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _logger = logger ?? NullLogger<EmbeddingRefresher>.Instance;
    }

    #region Public Methods

    /// <summary>
    /// Async version with pipelined embedding generation and DB writes.
    /// Uses a producer-consumer pattern where embedding batches are generated
    /// concurrently with DB writes for the previous batch.
    /// </summary>
    public async Task RefreshAsync(IEmbeddingProvider embeddingProvider, CancellationToken cancellationToken = default)
    {
        if (embeddingProvider is null || !embeddingProvider.Enabled)
            return;

        var totalDocuments = CountTotalDocuments();
        var documents = LoadDocumentEmbeddingSources();
        var docsSkippedAsUpToDate = totalDocuments - documents.Count;

        if (documents.Count == 0)
        {
            _logger.LogInformation("Semantic indexing complete: all {Total} documents up-to-date", totalDocuments);
            return;
        }

        _logger.LogInformation("Semantic indexing: {NeedRefresh} of {Total} documents need refresh ({Skipped} up-to-date)",
            documents.Count, totalDocuments, docsSkippedAsUpToDate);

        var workItems = BuildEmbeddingWorkItems(documents);
        if (workItems.Count == 0)
            return;

        var batchSize = GetEffectiveBatchSize(embeddingProvider);
        var uniqueDocs = workItems.Select(w => w.DocId).Distinct().Count();
        var totalChunks = workItems.Count;
        var chunkedDocs = workItems.Where(w => w.ChunkIndex > 0).Select(w => w.DocId).Distinct().Count();

        _logger.LogInformation("Semantic indexing: {Docs} documents ({Chunks} chunks, {Chunked} chunked)...",
            uniqueDocs, totalChunks, chunkedDocs);

        var sw = Stopwatch.StartNew();

        // Bounded channel for double-buffering: producer can be 1 batch ahead
        var channel = Channel.CreateBounded<EmbeddingBatchResult>(
            new BoundedChannelOptions(2) { SingleReader = true, SingleWriter = true });

        // Start producer task (runs embedding on background thread)
        var producerTask = ProduceEmbeddingsAsync(workItems, batchSize, embeddingProvider, channel.Writer, cancellationToken);

        // Consumer: writes to DB on current thread (single-writer architecture)
        var stats = await ConsumeAndWriteEmbeddingsAsync(channel.Reader, embeddingProvider, totalChunks, cancellationToken).ConfigureAwait(false);

        // Wait for producer to complete (handles exceptions)
        await producerTask.ConfigureAwait(false);

        sw.Stop();
        LogEmbeddingCompletionStats(sw.Elapsed, stats, embeddingProvider);
    }

    /// <summary>
    /// Removes embeddings where the referenced doc_id or node_id no longer exists in the node table.
    /// </summary>
    public void RemoveDangling()
    {
        var deletedCount = _store.WriteTransaction((conn, tx) =>
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                DELETE FROM document_embedding
                WHERE doc_id NOT IN (SELECT id FROM node)
                   OR node_id NOT IN (SELECT id FROM node)
                """;
            return cmd.ExecuteNonQuery();
        });

        if (deletedCount > 0)
        {
            _logger.LogInformation("Removed {Count} dangling embeddings", deletedCount);
        }
    }

    #endregion

    #region Private Helper Methods

    private int GetEffectiveBatchSize(IEmbeddingProvider provider)
    {
        var batchSize = DefaultEmbeddingBatchSize;
        if (int.TryParse(Environment.GetEnvironmentVariable("REPOQL_EMBED_BATCH_SIZE"), out var bs) && bs > 0)
        {
            batchSize = bs;
        }

        if (provider is RepoQL.Embeddings.OnnxEmbeddingProvider onnx)
        {
            var providerName = onnx.Provider?.ToUpperInvariant() ?? "CPU";
            if ((providerName == "COREML" || providerName == "DML") && batchSize > 256)
            {
                _logger.LogWarning("Capping embedding batch size from {Requested} to 256 for {Provider}", batchSize, providerName);
                batchSize = 256;
            }
        }
        return batchSize;
    }

    private async Task ProduceEmbeddingsAsync(
        List<EmbeddingWorkItem> workItems,
        int batchSize,
        IEmbeddingProvider provider,
        ChannelWriter<EmbeddingBatchResult> writer,
        CancellationToken ct)
    {
        var totalBatches = (workItems.Count + batchSize - 1) / batchSize;
        var batchNum = 0;
        var overallTimer = Stopwatch.StartNew();

        try
        {
            for (var ofs = 0; ofs < workItems.Count; ofs += batchSize)
            {
                ct.ThrowIfCancellationRequested();
                batchNum++;

                var sliceLength = Math.Min(batchSize, workItems.Count - ofs);
                var sliceItems = new EmbeddingWorkItem[sliceLength];
                var payloads = new string[sliceLength];
                for (var i = 0; i < sliceLength; i++)
                {
                    sliceItems[i] = workItems[ofs + i];
                    payloads[i] = sliceItems[i].Payload;
                }

                // Build progress info for this batch
                var itemsAfterBatch = ofs + sliceLength;
                var progress = new BatchEmbeddingProgress(batchNum, totalBatches, itemsAfterBatch, workItems.Count, overallTimer.Elapsed);

                float[]?[] vectors;
                var batchTimer = Stopwatch.StartNew();
                try
                {
                    vectors = await provider.EmbedBatchAsync(payloads, progress, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    batchTimer.Stop();
                    _logger.LogWarning(ex, "Embedding batch failed (size={BatchSize}, model={Model})", sliceLength, provider.Model);
                    vectors = Array.Empty<float[]?>();
                }
                batchTimer.Stop();

                await writer.WriteAsync(new EmbeddingBatchResult(sliceItems, vectors, batchTimer.Elapsed), ct).ConfigureAwait(false);
            }
        }
        finally
        {
            writer.Complete();
        }
    }

    private async Task<EmbeddingStats> ConsumeAndWriteEmbeddingsAsync(
        ChannelReader<EmbeddingBatchResult> reader,
        IEmbeddingProvider provider,
        int totalExpectedItems,
        CancellationToken ct)
    {
        var docSuccess = 0;
        var docSkipped = 0;
        var batches = 0;
        var totalItems = 0;
        double embedMsTotal = 0;
        double dbMsTotal = 0;

        await foreach (var batch in reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            batches++;
            totalItems += batch.Items.Length;
            embedMsTotal += batch.EmbedTime.TotalMilliseconds;

            var percentComplete = totalExpectedItems > 0 ? (int)(totalItems * 100.0 / totalExpectedItems) : 100;
            _logger.LogInformation("Full-text embeddings: {Processed}/{Total} ({Percent}%)",
                totalItems, totalExpectedItems, percentComplete);

            var dbTimer = Stopwatch.StartNew();

            // Collect valid items for bulk insert
            var validItems = new List<(EmbeddingWorkItem Item, float[] Vec)>();
            for (var i = 0; i < batch.Items.Length; i++)
            {
                var item = batch.Items[i];
                var vec = (batch.Vectors != null && i < batch.Vectors.Length) ? batch.Vectors[i] : null;

                if (vec is null)
                {
                    if (item.Scope == DocumentEmbeddingScope) docSkipped++;
                    continue;
                }

                validItems.Add((item, vec));
            }

            // Bulk insert all valid items
            if (validItems.Count > 0)
            {
                WriteBatchBulk(validItems, provider);
                foreach (var (item, _) in validItems)
                {
                    if (item.Scope == DocumentEmbeddingScope) docSuccess++;
                }
            }

            dbTimer.Stop();
            dbMsTotal += dbTimer.Elapsed.TotalMilliseconds;

            var perItemMs = batch.Items.Length == 0 ? 0 : batch.EmbedTime.TotalMilliseconds / batch.Items.Length;
            _logger.LogInformation("Batch processing: size={BatchSize}, embedding={EmbedMs:F1}ms ({EmbedPerItem:F1}ms/item), database={DbMs:F1}ms ({DbPerItem:F1}ms/item), total={TotalMs:F1}ms",
                batch.Items.Length,
                batch.EmbedTime.TotalMilliseconds, perItemMs,
                dbTimer.Elapsed.TotalMilliseconds, dbTimer.Elapsed.TotalMilliseconds / batch.Items.Length,
                batch.EmbedTime.TotalMilliseconds + dbTimer.Elapsed.TotalMilliseconds);
        }

        return new EmbeddingStats(docSuccess, docSkipped, batches, totalItems, embedMsTotal, dbMsTotal);
    }

    private void WriteBatchBulk(List<(EmbeddingWorkItem Item, float[] Vec)> items, IEmbeddingProvider provider)
    {
        if (items.Count == 0) return;

        _store.WriteTransaction((conn, tx) =>
        {
            var sb = new StringBuilder();
            sb.AppendLine("""
                INSERT INTO document_embedding(doc_id, node_id, chunk_index, embedding_type, uri, scope, model, dim, embedding, start_byte, end_byte, updated_at)
                VALUES
                """);

            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;

            for (var i = 0; i < items.Count; i++)
            {
                if (i > 0) sb.Append(",\n");
                var paramBase = i * 11;
                sb.Append($"(?{paramBase + 1},?{paramBase + 2},?{paramBase + 3},?{paramBase + 4},?{paramBase + 5},?{paramBase + 6},?{paramBase + 7},?{paramBase + 8},?{paramBase + 9},?{paramBase + 10},?{paramBase + 11},CURRENT_TIMESTAMP)");

                var (item, vec) = items[i];
                cmd.Parameters.Add(new DuckDBParameter { Value = item.DocId });
                cmd.Parameters.Add(new DuckDBParameter { Value = item.NodeId });
                cmd.Parameters.Add(new DuckDBParameter { Value = item.ChunkIndex });
                cmd.Parameters.Add(new DuckDBParameter { Value = item.EmbeddingType });
                cmd.Parameters.Add(new DuckDBParameter { Value = item.Uri });
                cmd.Parameters.Add(new DuckDBParameter { Value = item.Scope });
                cmd.Parameters.Add(new DuckDBParameter { Value = provider.Model });
                cmd.Parameters.Add(new DuckDBParameter { Value = provider.Dimension });
                cmd.Parameters.Add(new DuckDBParameter { Value = ToNativeArray(vec) });
                cmd.Parameters.Add(new DuckDBParameter { Value = item.StartByte ?? (object)DBNull.Value });
                cmd.Parameters.Add(new DuckDBParameter { Value = item.EndByte ?? (object)DBNull.Value });
            }

            sb.AppendLine("""
                ON CONFLICT (doc_id, node_id, chunk_index, embedding_type)
                DO UPDATE SET uri=excluded.uri, scope=excluded.scope, model=excluded.model,
                              dim=excluded.dim, embedding=excluded.embedding,
                              start_byte=excluded.start_byte, end_byte=excluded.end_byte,
                              updated_at=excluded.updated_at
                """);

            cmd.CommandText = sb.ToString();
            cmd.ExecuteNonQuery();
        });
    }

    private void LogEmbeddingCompletionStats(TimeSpan elapsed, EmbeddingStats stats, IEmbeddingProvider provider)
    {
        var totalMs = elapsed.TotalMilliseconds;
        var embedPct = totalMs <= 0 ? 0 : (stats.EmbedMsTotal / totalMs) * 100;
        var dbPct = totalMs <= 0 ? 0 : (stats.DbMsTotal / totalMs) * 100;
        var throughput = stats.TotalItems == 0 ? 0 : stats.TotalItems / Math.Max(0.001, totalMs / 1000d);

        _logger.LogInformation(
            "Semantic indexing complete: {DocSuccess} documents, {Skipped} skipped | {Batches} batches, {Items} items | embed={EmbedMs:F1}ms ({EmbedPct:F1}%), db={DbMs:F1}ms ({DbPct:F1}%), total={TotalSec:F1}s @ {Throughput:F1} items/s | model={Model}",
            stats.DocSuccess,
            stats.DocSkipped,
            stats.Batches,
            stats.TotalItems,
            stats.EmbedMsTotal,
            embedPct,
            stats.DbMsTotal,
            dbPct,
            totalMs / 1000.0,
            throughput,
            provider.Model);
    }

    #endregion

    #region Data Loading

    private int CountTotalDocuments()
    {
        var result = _store.ReadScalar<long?>("SELECT COUNT(*) FROM node WHERE kind = 'document'");
        return (int)(result ?? 0L);
    }

    private Dictionary<Guid, DocumentEmbeddingRow> LoadDocumentEmbeddingSources()
    {
        const string sql = """
            SELECT n.id,
                   n.uri,
                   a.text_content,
                   a.headline,
                   a.summary,
                   a.structure
            FROM node n
                     JOIN artifact a ON a.id = n.artifact_id
                     LEFT JOIN document_embedding de
                          ON de.doc_id = n.id AND de.scope = 'document' AND de.embedding_type = 'full'
            WHERE n.kind = 'document'
              AND a.text_content IS NOT NULL
              AND (de.doc_id IS NULL OR de.updated_at < n.updated_at)
              AND (a.media_type LIKE 'text/%'
                   OR a.media_type LIKE 'application/json%'
                   OR a.media_type LIKE 'application/xml%'
                   OR a.media_type LIKE 'application/%yaml%'
                   OR a.media_type LIKE 'application/javascript%'
                   OR a.media_type LIKE 'application/typescript%'
                   OR a.media_type LIKE 'application/%sql%'
                   OR a.media_type LIKE 'application/graphql%'
                   OR a.media_type LIKE 'application/toml%'
                   OR a.media_type LIKE 'application/x-sh%'
                   OR a.media_type LIKE 'application/x-python%'
                   OR a.media_type LIKE 'application/x-ruby%'
                   OR a.media_type LIKE 'application/x-perl%'
                   OR a.media_type LIKE 'application/x-php%');
            """;

        var documents = new Dictionary<Guid, DocumentEmbeddingRow>();
        var results = _store.Read(sql, record =>
        {
            var id = record.GetGuid(0);
            var uri = record.IsDBNull(1) ? null : record.GetString(1);
            var text = record.IsDBNull(2) ? string.Empty : record.GetString(2);
            var bytes = Encoding.UTF8.GetBytes(text);
            return new DocumentEmbeddingRow(
                id,
                string.IsNullOrWhiteSpace(uri) ? $"repoql://document/{id:D}" : uri!,
                text,
                bytes,
                record.IsDBNull(3) ? null : record.GetString(3),
                record.IsDBNull(4) ? null : record.GetString(4),
                record.IsDBNull(5) ? null : record.GetString(5));
        });

        foreach (var doc in results)
        {
            documents[doc.Id] = doc;
        }

        return documents;
    }

    private List<EmbeddingWorkItem> BuildEmbeddingWorkItems(IReadOnlyDictionary<Guid, DocumentEmbeddingRow> documents)
    {
        // Only build document-level embeddings at index time.
        // Object-level embeddings are generated just-in-time during search.
        // Small files get a single embedding; large files are chunked with overlap.
        // Very large files (>250KB) use structure-only embedding to save cost.
        var work = new List<EmbeddingWorkItem>(documents.Count * 2); // Estimate 2x for chunking

        foreach (var doc in documents.Values)
        {
            var byteSize = doc.Utf8Bytes?.Length ?? 0;
            var textLength = doc.Text?.Length ?? 0;

            if (byteSize > LargeFileThresholdBytes)
            {
                // Very large file: use structure-only embedding (or headline+summary if no structure)
                var payload = BuildStructureOnlyEmbeddingText(doc);
                if (!string.IsNullOrWhiteSpace(payload))
                {
                    work.Add(new EmbeddingWorkItem(doc.Id, doc.Id, 0, FullEmbeddingType, doc.Uri, DocumentEmbeddingScope, payload, null, null));
                }
            }
            else if (textLength <= SmallFileThresholdChars)
            {
                // Small file: single embedding covering entire content
                var payload = BuildDocumentEmbeddingText(doc);
                if (!string.IsNullOrWhiteSpace(payload))
                {
                    work.Add(new EmbeddingWorkItem(doc.Id, doc.Id, 0, FullEmbeddingType, doc.Uri, DocumentEmbeddingScope, payload, null, null));
                }
            }
            else
            {
                // Medium file: split into overlapping chunks
                var chunks = ChunkText(doc.Text!, ChunkSizeChars, ChunkOverlapChars);
                var preamble = BuildPreamble(doc); // headline + summary for context

                for (var i = 0; i < chunks.Count; i++)
                {
                    var (chunkText, startChar, endChar) = chunks[i];
                    // Prepend preamble to each chunk for semantic context
                    var payload = string.IsNullOrWhiteSpace(preamble)
                        ? chunkText
                        : $"{preamble}\n\n{chunkText}";

                    if (!string.IsNullOrWhiteSpace(payload))
                    {
                        // Convert char positions to byte positions for the source text
                        var startByte = Encoding.UTF8.GetByteCount(doc.Text!.AsSpan(0, startChar));
                        var endByte = Encoding.UTF8.GetByteCount(doc.Text!.AsSpan(0, endChar));
                        work.Add(new EmbeddingWorkItem(doc.Id, doc.Id, i, FullEmbeddingType, doc.Uri, DocumentEmbeddingScope, payload, startByte, endByte));
                    }
                }
            }
        }

        return work;
    }

    private static string BuildStructureOnlyEmbeddingText(DocumentEmbeddingRow doc)
    {
        // For very large files, use headline + structure (they contain different data)
        var parts = new List<string>(2);
        if (!string.IsNullOrWhiteSpace(doc.Headline))
            parts.Add(doc.Headline);
        if (!string.IsNullOrWhiteSpace(doc.Structure))
            parts.Add(doc.Structure);
        return string.Join("\n\n", parts);
    }

    private static string BuildPreamble(DocumentEmbeddingRow doc)
    {
        // Build a short preamble from x-ray fields for context in each chunk
        var parts = new List<string>(2);
        if (!string.IsNullOrWhiteSpace(doc.Headline))
            parts.Add(doc.Headline);
        if (!string.IsNullOrWhiteSpace(doc.Summary))
            parts.Add(doc.Summary);
        return string.Join("\n", parts);
    }

    private static List<(string Text, int StartChar, int EndChar)> ChunkText(string text, int chunkSize, int overlap)
    {
        var chunks = new List<(string, int, int)>();
        var stride = chunkSize - overlap;
        if (stride <= 0) stride = chunkSize; // Fallback if overlap >= size

        for (var start = 0; start < text.Length; start += stride)
        {
            var end = Math.Min(start + chunkSize, text.Length);
            chunks.Add((text[start..end], start, end));

            // If we've reached the end, stop
            if (end >= text.Length)
                break;
        }

        return chunks;
    }

    private static string BuildDocumentEmbeddingText(DocumentEmbeddingRow doc)
    {
        return CombineSegments(
            new[] { doc.Headline, doc.Summary, doc.Structure, doc.Text },
            MaxDocumentPayloadChars);
    }

    private static string CombineSegments(IEnumerable<string?> segments, int maxChars)
    {
        var builder = new StringBuilder();
        foreach (var segment in segments)
        {
            if (string.IsNullOrWhiteSpace(segment))
                continue;

            if (builder.Length > 0)
            {
                builder.AppendLine().AppendLine();
            }

            builder.Append(segment.Trim());

            if (builder.Length >= maxChars)
                break;
        }

        if (builder.Length == 0)
            return string.Empty;

        var text = builder.ToString();
        return text.Length <= maxChars ? text : text[..maxChars];
    }

    #endregion

    #region Helper Types and Methods

    private sealed record DocumentEmbeddingRow(
        Guid Id,
        string Uri,
        string Text,
        byte[] Utf8Bytes,
        string? Headline,
        string? Summary,
        string? Structure);

    private readonly record struct EmbeddingWorkItem(
        Guid DocId,
        Guid NodeId,
        int ChunkIndex,
        string EmbeddingType,  // 'structure' or 'full'
        string Uri,
        string Scope,
        string Payload,
        long? StartByte,
        long? EndByte);

    private readonly record struct EmbeddingBatchResult(
        EmbeddingWorkItem[] Items,
        float[]?[] Vectors,
        TimeSpan EmbedTime);

    private readonly record struct EmbeddingStats(
        int DocSuccess,
        int DocSkipped,
        int Batches,
        int TotalItems,
        double EmbedMsTotal,
        double DbMsTotal);

    /// <summary>
    /// Converts float[] to List&lt;float&gt; for DuckDB native FLOAT[N] array storage.
    /// DuckDB.NET maps List&lt;T&gt; to DuckDB's array/list types.
    /// </summary>
    private static List<float> ToNativeArray(float[] vec) => new List<float>(vec);

    #endregion
}
