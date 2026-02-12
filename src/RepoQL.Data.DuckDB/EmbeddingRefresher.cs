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
    // Default aligns with OpenRouter's 100-item API limit to avoid split batches.
    private const int DefaultEmbeddingBatchSize = 100;

    // How many documents to pull per DB round-trip when building embedding payloads.
    // Kept intentionally small to bound peak memory (text_content can be large).
    private const int DefaultDocumentFetchBatchSize = 32;

    #endregion

    private readonly DuckDbDataStore _store;
    private readonly EmbeddingMode _embeddingMode;
    private readonly ILogger<EmbeddingRefresher> _logger;

    public EmbeddingRefresher(DuckDbDataStore store, EmbeddingMode embeddingMode = EmbeddingMode.Full, ILogger<EmbeddingRefresher>? logger = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _embeddingMode = embeddingMode;
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
        await RefreshInternalAsync(embeddingProvider, targetDocumentIds: null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Refreshes embeddings for a targeted set of document node ids.
    /// </summary>
    public async Task RefreshAsync(
        IEmbeddingProvider embeddingProvider,
        IReadOnlyList<Guid> documentIds,
        CancellationToken cancellationToken = default)
    {
        if (documentIds is null)
            throw new ArgumentNullException(nameof(documentIds));

        if (documentIds.Count == 0)
            return;

        await RefreshInternalAsync(embeddingProvider, DistinctDocumentIds(documentIds), cancellationToken).ConfigureAwait(false);
    }

    private async Task RefreshInternalAsync(
        IEmbeddingProvider embeddingProvider,
        IReadOnlyList<Guid>? targetDocumentIds,
        CancellationToken cancellationToken)
    {
        if (embeddingProvider is null || !embeddingProvider.Enabled)
            return;

        PruneEmbeddingsForCurrentModel(embeddingProvider);

        var refreshPlan = LoadDocumentRefreshPlan(targetDocumentIds);
        var totalDocuments = CountTotalDocuments(targetDocumentIds);
        var docsSkippedAsUpToDate = totalDocuments - refreshPlan.Count;
        var isTargeted = targetDocumentIds is { Count: > 0 };

        if (refreshPlan.Count == 0)
        {
            if (isTargeted)
            {
                _logger.LogInformation("Semantic indexing complete: all {Total} targeted documents up-to-date", totalDocuments);
            }
            else
            {
                _logger.LogInformation("Semantic indexing complete: all {Total} documents up-to-date", totalDocuments);
            }

            return;
        }

        if (isTargeted)
        {
            _logger.LogInformation("Semantic indexing (targeted): {NeedRefresh} of {Total} documents need refresh ({Skipped} up-to-date)",
                refreshPlan.Count, totalDocuments, docsSkippedAsUpToDate);
        }
        else
        {
            _logger.LogInformation("Semantic indexing: {NeedRefresh} of {Total} documents need refresh ({Skipped} up-to-date)",
                refreshPlan.Count, totalDocuments, docsSkippedAsUpToDate);
        }

        var totalExpectedItems = refreshPlan.Sum(p => p.WorkItemCount);
        if (totalExpectedItems <= 0)
            return;

        var batchSize = GetEffectiveBatchSize(embeddingProvider);
        var largeDocs = refreshPlan.Count(p => p.IsLarge);
        var chunkedDocs = refreshPlan.Count(p => !p.IsLarge && p.WorkItemCount > 1);

        _logger.LogInformation("Semantic indexing: {Docs} documents ({Items} embeddings, {Chunked} chunked, {Large} large)...",
            refreshPlan.Count, totalExpectedItems, chunkedDocs, largeDocs);

        var sw = Stopwatch.StartNew();

        // Bounded channel for double-buffering: producer can be 1 batch ahead
        var channel = Channel.CreateBounded<EmbeddingBatchResult>(
            new BoundedChannelOptions(2) { SingleReader = true, SingleWriter = true });

        // Start producer task (runs embedding on background thread)
        var producerTask = ProduceEmbeddingsAsync(refreshPlan, batchSize, embeddingProvider, channel.Writer, totalExpectedItems, cancellationToken);

        // Consumer: writes to DB on current thread (single-writer architecture)
        var stats = await ConsumeAndWriteEmbeddingsAsync(channel.Reader, embeddingProvider, totalExpectedItems, cancellationToken).ConfigureAwait(false);

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

    /// <summary>
    /// Clears all embeddings (used when embedding model changes and dimensions are incompatible).
    /// </summary>
    public void ClearAllEmbeddings()
    {
        var deletedCount = _store.WriteTransaction((conn, tx) =>
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM document_embedding";
            return cmd.ExecuteNonQuery();
        });

        _logger.LogInformation("Cleared {Count} embeddings due to model change", deletedCount);
    }

    private void PruneEmbeddingsForCurrentModel(IEmbeddingProvider embeddingProvider)
    {
        // Avoid dimension mismatches inside list_cosine_similarity() by pruning embeddings generated by other models.
        // This uses only existing schema (document_embedding) and keeps the single-writer invariant.
        var currentModel = embeddingProvider.Model;
        var currentDim = embeddingProvider.Dimension;

        var distinct = _store.Read(
            "SELECT DISTINCT model, dim FROM document_embedding LIMIT 2",
            r => (Model: r.GetString(0), Dim: r.GetInt32(1)));

        if (distinct.Count == 0)
            return;

        if (distinct.Count == 1
            && string.Equals(distinct[0].Model, currentModel, StringComparison.Ordinal)
            && distinct[0].Dim == currentDim)
        {
            return;
        }

        _logger.LogWarning(
            "Embedding model mismatch detected. Keeping embeddings for {Model}:{Dim} and pruning others. " +
            "Run 'repoql reindex' to regenerate all embeddings for the new model.",
            currentModel, currentDim);

        var deletedCount = _store.WriteTransaction((conn, tx) =>
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM document_embedding WHERE model <> ? OR dim <> ?";
            cmd.AddParameters(currentModel, currentDim);
            return cmd.ExecuteNonQuery();
        });

        if (deletedCount > 0)
            _logger.LogInformation("Pruned {Count} embeddings from previous models/dimensions", deletedCount);
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
        IReadOnlyList<DocumentRefreshPlanRow> refreshPlan,
        int batchSize,
        IEmbeddingProvider provider,
        ChannelWriter<EmbeddingBatchResult> writer,
        int totalExpectedItems,
        CancellationToken ct)
    {
        var totalBatches = (totalExpectedItems > 0 && batchSize > 0)
            ? (totalExpectedItems + batchSize - 1) / batchSize
            : 0;

        var batchNum = 0;
        var itemsProcessed = 0;
        var overallTimer = Stopwatch.StartNew();

        // We intentionally keep these lists bounded to batchSize so payload strings don't accumulate.
        var pendingItems = new List<EmbeddingWorkItem>(batchSize);
        var pendingPayloads = new List<string>(batchSize);

        async Task FlushAsync()
        {
            if (pendingItems.Count == 0)
                return;

            ct.ThrowIfCancellationRequested();
            batchNum++;

            var items = pendingItems.ToArray();
            var itemsAfterBatch = itemsProcessed + items.Length;
            var progress = totalBatches > 0
                ? new BatchEmbeddingProgress(batchNum, totalBatches, itemsAfterBatch, totalExpectedItems, overallTimer.Elapsed)
                : default;

            float[]?[] vectors;
            var batchTimer = Stopwatch.StartNew();
            try
            {
                // Use passage embedding for document content (E5 models prepend "passage: " prefix)
                vectors = await provider.EmbedPassageBatchAsync(pendingPayloads, progress, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                batchTimer.Stop();
                _logger.LogWarning(ex, "Embedding batch failed (size={BatchSize}, model={Model})", pendingPayloads.Count, provider.Model);
                vectors = Array.Empty<float[]?>();
            }
            batchTimer.Stop();

            itemsProcessed = itemsAfterBatch;

            // Drop payload references immediately (channel result never needs payload text).
            pendingItems.Clear();
            pendingPayloads.Clear();

            await writer.WriteAsync(new EmbeddingBatchResult(items, vectors, batchTimer.Elapsed), ct).ConfigureAwait(false);
        }

        try
        {
            // Process large docs first: avoids pulling large text_content into memory.
            var largeDocIds = refreshPlan.Where(p => p.IsLarge).Select(p => p.Id).ToArray();
            for (var ofs = 0; ofs < largeDocIds.Length; ofs += DefaultDocumentFetchBatchSize)
            {
                ct.ThrowIfCancellationRequested();
                var len = Math.Min(DefaultDocumentFetchBatchSize, largeDocIds.Length - ofs);
                var batchIds = largeDocIds.AsSpan(ofs, len).ToArray();
                var docs = LoadLargeDocumentEmbeddingSources(batchIds);

                foreach (var doc in docs)
                {
                    var payload = BuildStructureOnlyEmbeddingText(doc.Headline, doc.Structure);
                    if (string.IsNullOrWhiteSpace(payload))
                        continue;

                    pendingItems.Add(new EmbeddingWorkItem(
                        doc.Id,
                        doc.Id,
                        ChunkIndex: 0,
                        FullEmbeddingType,
                        doc.Uri,
                        DocumentEmbeddingScope,
                        StartByte: null,
                        EndByte: null));
                    pendingPayloads.Add(payload);
                    if (pendingItems.Count >= batchSize)
                        await FlushAsync().ConfigureAwait(false);
                }
            }

            // Then process normal docs (small + chunked), fetching text in small batches.
            var textDocIds = refreshPlan.Where(p => !p.IsLarge).Select(p => p.Id).ToArray();
            for (var ofs = 0; ofs < textDocIds.Length; ofs += DefaultDocumentFetchBatchSize)
            {
                ct.ThrowIfCancellationRequested();
                var len = Math.Min(DefaultDocumentFetchBatchSize, textDocIds.Length - ofs);
                var batchIds = textDocIds.AsSpan(ofs, len).ToArray();
                var docs = LoadTextDocumentEmbeddingSources(batchIds);

                foreach (var doc in docs)
                {
                    if (string.IsNullOrEmpty(doc.Text))
                        continue;

                    if (doc.Text.Length <= SmallFileThresholdChars)
                    {
                        var payload = BuildDocumentEmbeddingText(doc);
                        if (string.IsNullOrWhiteSpace(payload))
                            continue;

                        pendingItems.Add(new EmbeddingWorkItem(
                            doc.Id,
                            doc.Id,
                            ChunkIndex: 0,
                            FullEmbeddingType,
                            doc.Uri,
                            DocumentEmbeddingScope,
                            StartByte: null,
                            EndByte: null));
                        pendingPayloads.Add(payload);
                        if (pendingItems.Count >= batchSize)
                            await FlushAsync().ConfigureAwait(false);
                        continue;
                    }

                    var chunkRanges = ChunkRanges(doc.Text, ChunkSizeChars, ChunkOverlapChars);
                    if (chunkRanges.Count == 0)
                        continue;

                    var preamble = BuildPreamble(doc);
                    var utf8Offsets = ComputeUtf8ByteOffsets(doc.Text, chunkRanges);

                    for (var i = 0; i < chunkRanges.Count; i++)
                    {
                        var (startChar, endChar) = chunkRanges[i];
                        var chunkText = doc.Text[startChar..endChar];
                        var payload = string.IsNullOrWhiteSpace(preamble)
                            ? chunkText
                            : $"{preamble}\n\n{chunkText}";

                        if (string.IsNullOrWhiteSpace(payload))
                            continue;

                        pendingItems.Add(new EmbeddingWorkItem(
                            doc.Id,
                            doc.Id,
                            ChunkIndex: i,
                            FullEmbeddingType,
                            doc.Uri,
                            DocumentEmbeddingScope,
                            StartByte: utf8Offsets[startChar],
                            EndByte: utf8Offsets[endChar]));
                        pendingPayloads.Add(payload);
                        if (pendingItems.Count >= batchSize)
                            await FlushAsync().ConfigureAwait(false);
                    }
                }
            }

            await FlushAsync().ConfigureAwait(false);
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
                sb.Append("(?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, CURRENT_TIMESTAMP)");

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
            "Semantic indexing complete: {DocSuccess} embeddings, {Skipped} skipped | {Batches} batches, {Items} items | embed={EmbedMs:F1}ms ({EmbedPct:F1}%), db={DbMs:F1}ms ({DbPct:F1}%), total={TotalSec:F1}s @ {Throughput:F1} items/s | model={Model}",
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

    private int CountTotalDocuments(IReadOnlyList<Guid>? targetDocumentIds)
    {
        if (targetDocumentIds is { Count: > 0 })
        {
            var idList = ToUuidListSql(targetDocumentIds);
            var query = $"""
                SELECT COUNT(*)
                FROM node
                WHERE kind = 'document'
                  AND id IN ({idList});
                """;
            var targetedResult = _store.ReadScalar<long?>(query);
            return (int)(targetedResult ?? 0L);
        }

        var result = _store.ReadScalar<long?>("SELECT COUNT(*) FROM node WHERE kind = 'document'");
        return (int)(result ?? 0L);
    }

    private IReadOnlyList<DocumentRefreshPlanRow> LoadDocumentRefreshPlan(IReadOnlyList<Guid>? targetDocumentIds)
    {
        if (targetDocumentIds is { Count: 0 })
            return [];

        var stride = ChunkSizeChars - ChunkOverlapChars;
        if (stride <= 0) stride = ChunkSizeChars;
        var idFilter = targetDocumentIds is { Count: > 0 }
            ? $"\n                  AND n.id IN ({ToUuidListSql(targetDocumentIds)})"
            : string.Empty;

        // In Hybrid mode, we need to know if meaningful x-ray data exists (headline OR structure with actual content)
        // This ensures we only use structure-only embedding when there's actual content to embed
        var structureCheck = _embeddingMode.IsHybrid()
            ? ", CASE WHEN (a.headline IS NOT NULL AND length(trim(a.headline)) > 0) OR (a.structure IS NOT NULL AND length(trim(a.structure)) > 0) THEN 1 ELSE 0 END AS has_structure"
            : ", 0 AS has_structure";

        var sql = $"""
            WITH refreshable AS (
                SELECT
                    n.id,
                    n.uri,
                    length(a.text_content)       AS char_len,
                    a.byte_size                  AS byte_len
                    {structureCheck}
                FROM node n
                         JOIN artifact a ON a.id = n.artifact_id
                         LEFT JOIN document_embedding de
                              ON de.doc_id = n.id AND de.scope = '{DocumentEmbeddingScope}' AND de.embedding_type = '{FullEmbeddingType}'
                WHERE n.kind = 'document'
                  AND a.text_content IS NOT NULL
                  AND (de.doc_id IS NULL OR de.updated_at < n.updated_at)
                  {idFilter}
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
                       OR a.media_type LIKE 'application/x-php%')
            )
            SELECT
                id,
                uri,
                CASE WHEN byte_len > {LargeFileThresholdBytes} THEN 1 ELSE 0 END AS is_large,
                CASE
                    WHEN byte_len > {LargeFileThresholdBytes} THEN 1
                    WHEN char_len <= {SmallFileThresholdChars} THEN 1
                    ELSE CAST(((char_len - {ChunkSizeChars}) + {stride} - 1) / {stride} AS INTEGER) + 1
                END AS work_items,
                has_structure
            FROM refreshable
            ORDER BY id;
            """;

        return _store.Read(sql, record =>
        {
            var id = record.GetGuid(0);
            var uri = record.IsDBNull(1) ? string.Empty : record.GetString(1);
            var isLargeFromSize = !record.IsDBNull(2) && record.GetInt32(2) != 0;
            var workItems = record.IsDBNull(3) ? 0 : record.GetInt32(3);
            var hasStructure = !record.IsDBNull(4) && record.GetInt32(4) != 0;

            // Apply structure-only filter: files matching patterns get structure-only embedding
            // even if they're under the size threshold
            var isLarge = isLargeFromSize || StructureOnlyFilter.IsStructureOnly(uri);

            // Hybrid mode: if structure exists, use structure-only embedding
            if (_embeddingMode.IsHybrid() && hasStructure)
            {
                isLarge = true;
                workItems = 1;
            }

            // If newly marked as structure-only by pattern (not size), work items = 1
            if (isLarge && !isLargeFromSize && !hasStructure)
                workItems = 1;

            return new DocumentRefreshPlanRow(id, uri, isLarge, workItems);
        });
    }

    private IReadOnlyList<TextDocumentEmbeddingRow> LoadTextDocumentEmbeddingSources(IReadOnlyList<Guid> docIds)
    {
        if (docIds.Count == 0)
            return [];

        var idList = ToUuidListSql(docIds);
        // Note: Size filtering is done in LoadDocumentRefreshPlan() which also applies pattern-based
        // structure-only filtering. The docIds passed here are already filtered to non-large documents.
        var sql = $"""
            SELECT n.id,
                   n.uri,
                   a.text_content,
                   a.headline,
                   a.summary,
                   a.structure
            FROM node n
                     JOIN artifact a ON a.id = n.artifact_id
            WHERE n.id IN ({idList})
              AND a.text_content IS NOT NULL;
            """;

        return _store.Read(sql, record =>
        {
            var id = record.GetGuid(0);
            var uri = record.IsDBNull(1) ? null : record.GetString(1);
            var text = record.IsDBNull(2) ? string.Empty : record.GetString(2);
            return new TextDocumentEmbeddingRow(
                id,
                string.IsNullOrWhiteSpace(uri) ? $"repoql://document/{id:D}" : uri!,
                text,
                record.IsDBNull(3) ? null : record.GetString(3),
                record.IsDBNull(4) ? null : record.GetString(4),
                record.IsDBNull(5) ? null : record.GetString(5));
        });
    }

    private IReadOnlyList<LargeDocumentEmbeddingRow> LoadLargeDocumentEmbeddingSources(IReadOnlyList<Guid> docIds)
    {
        if (docIds.Count == 0)
            return [];

        var idList = ToUuidListSql(docIds);
        // Note: "Large" documents are those marked for structure-only embedding, either by size
        // or by pattern match (e.g., minified files, vendored libraries). The docIds passed
        // here are already filtered to large documents in LoadDocumentRefreshPlan().
        var sql = $"""
            SELECT n.id,
                   n.uri,
                   a.headline,
                   a.structure
            FROM node n
                     JOIN artifact a ON a.id = n.artifact_id
            WHERE n.id IN ({idList})
              AND a.text_content IS NOT NULL;
            """;

        return _store.Read(sql, record =>
        {
            var id = record.GetGuid(0);
            var uri = record.IsDBNull(1) ? null : record.GetString(1);
            return new LargeDocumentEmbeddingRow(
                id,
                string.IsNullOrWhiteSpace(uri) ? $"repoql://document/{id:D}" : uri!,
                record.IsDBNull(2) ? null : record.GetString(2),
                record.IsDBNull(3) ? null : record.GetString(3));
        });
    }

    private static string ToUuidListSql(IReadOnlyList<Guid> ids)
    {
        var sb = new StringBuilder(capacity: ids.Count * 40);
        for (var i = 0; i < ids.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append('\'').Append(ids[i].ToString("D")).Append("'::UUID");
        }
        return sb.ToString();
    }

    private static IReadOnlyList<Guid> DistinctDocumentIds(IReadOnlyList<Guid> documentIds)
    {
        var seen = new HashSet<Guid>();
        var distinct = new List<Guid>(documentIds.Count);
        for (var i = 0; i < documentIds.Count; i++)
        {
            var id = documentIds[i];
            if (id == Guid.Empty || !seen.Add(id))
                continue;

            distinct.Add(id);
        }

        return distinct;
    }

    private static string BuildStructureOnlyEmbeddingText(string? headline, string? structure)
    {
        // For large files, use headline + structure (they contain different data).
        var parts = new List<string>(2);
        if (!string.IsNullOrWhiteSpace(headline))
            parts.Add(headline);
        if (!string.IsNullOrWhiteSpace(structure))
            parts.Add(structure);
        return string.Join("\n\n", parts);
    }

    private static string BuildPreamble(TextDocumentEmbeddingRow doc)
    {
        // Build a short preamble from x-ray fields for context in each chunk.
        var parts = new List<string>(2);
        if (!string.IsNullOrWhiteSpace(doc.Headline))
            parts.Add(doc.Headline);
        if (!string.IsNullOrWhiteSpace(doc.Summary))
            parts.Add(doc.Summary);
        return string.Join("\n", parts);
    }

    private static List<(int StartChar, int EndChar)> ChunkRanges(string text, int chunkSize, int overlap)
    {
        var chunks = new List<(int, int)>();
        var stride = chunkSize - overlap;
        if (stride <= 0) stride = chunkSize; // Fallback if overlap >= size

        for (var start = 0; start < text.Length; start += stride)
        {
            var end = Math.Min(start + chunkSize, text.Length);
            chunks.Add((start, end));
            if (end >= text.Length)
                break;
        }

        return chunks;
    }

    private static Dictionary<int, long> ComputeUtf8ByteOffsets(string text, IReadOnlyList<(int StartChar, int EndChar)> chunks)
    {
        if (chunks.Count == 0)
            return new Dictionary<int, long>(1) { { 0, 0 } };

        var boundaries = new List<int>(chunks.Count * 2 + 1) { 0 };
        var lastAdded = 0;
        var si = 0;
        var ei = 0;
        while (si < chunks.Count || ei < chunks.Count)
        {
            int next;
            if (ei >= chunks.Count || (si < chunks.Count && chunks[si].StartChar <= chunks[ei].EndChar))
            {
                next = chunks[si].StartChar;
                si++;
            }
            else
            {
                next = chunks[ei].EndChar;
                ei++;
            }

            if (next == lastAdded)
                continue;

            boundaries.Add(next);
            lastAdded = next;
        }

        var map = new Dictionary<int, long>(boundaries.Count);
        long bytes = 0;
        var prev = 0;
        map[0] = 0;
        for (var i = 1; i < boundaries.Count; i++)
        {
            var pos = boundaries[i];
            bytes += Encoding.UTF8.GetByteCount(text.AsSpan(prev, pos - prev));
            map[pos] = bytes;
            prev = pos;
        }

        return map;
    }

    private static string BuildDocumentEmbeddingText(TextDocumentEmbeddingRow doc)
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

    private sealed record DocumentRefreshPlanRow(
        Guid Id,
        string Uri,
        bool IsLarge,
        int WorkItemCount);

    private sealed record TextDocumentEmbeddingRow(
        Guid Id,
        string Uri,
        string Text,
        string? Headline,
        string? Summary,
        string? Structure);

    private sealed record LargeDocumentEmbeddingRow(
        Guid Id,
        string Uri,
        string? Headline,
        string? Structure);

    private readonly record struct EmbeddingWorkItem(
        Guid DocId,
        Guid NodeId,
        int ChunkIndex,
        string EmbeddingType,  // 'structure' or 'full'
        string Uri,
        string Scope,
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
