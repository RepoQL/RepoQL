using System.Data;
using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
using DuckDB.NET.Data;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RepoQL.Contracts.Configuration;
using RepoQL.Contracts.Embeddings;

// ReSharper disable ConvertToLocalFunction

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

    // Voyage API context window is 32k tokens. Code tokenizes at ~2-4 chars/token
    // (dense code with braces/operators is worst case ~2 chars/token).
    // 32k chars guarantees we stay under the token limit regardless of content.
    private const int MaxEmbeddingPayloadChars = 32_000;
    private const string DocumentEmbeddingScope = "document";
    private const string FullEmbeddingType = "full";

    // Batch size for embedding from configuration (embedding.batch_size).
    // Default aligns with OpenRouter's 100-item API limit to avoid split batches.
    private const int DefaultEmbeddingBatchSize = 100;

    // How many documents to pull per DB round-trip when building embedding payloads.
    // Kept intentionally small to bound peak memory (text_content can be large).
    private const int DefaultDocumentFetchBatchSize = 32;

    #endregion

    private readonly DuckDbDataStore _store;
    private readonly EmbeddingMode _embeddingMode;
    private readonly RepoQlConfig.EmbeddingSettings _embeddingSettings;
    private readonly IContextualEmbeddingProvider? _contextualProvider;
    private readonly ILogger _logger;
    private readonly Lazy<VoyageTokenCounter> _tokenCounter;

    public EmbeddingRefresher(
        DuckDbDataStore store,
        EmbeddingMode embeddingMode = EmbeddingMode.Full,
        ILogger? logger = null,
        RepoQlConfig.EmbeddingSettings? embeddingSettings = null,
        IContextualEmbeddingProvider? contextualProvider = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _embeddingMode = embeddingMode;
        _embeddingSettings = embeddingSettings ?? new RepoQlConfig.EmbeddingSettings();
        _contextualProvider = contextualProvider is { Enabled: true } ? contextualProvider : null;
        _logger = logger ?? NullLogger<EmbeddingRefresher>.Instance;
        _tokenCounter = new Lazy<VoyageTokenCounter>(() => new VoyageTokenCounter(logger));
    }

    #region Public Methods

    /// <summary>
    /// Async version with pipelined embedding generation and DB writes.
    /// Uses a producer-consumer pattern where embedding batches are generated
    /// concurrently with DB writes for the previous batch.
    /// </summary>
    public async Task<bool> RefreshAsync(IEmbeddingProvider embeddingProvider, CancellationToken cancellationToken = default)
    {
        return await RefreshInternalAsync(embeddingProvider, targetDocumentIds: null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Refreshes embeddings for a targeted set of document node ids.
    /// </summary>
    public async Task<bool> RefreshAsync(
        IEmbeddingProvider embeddingProvider,
        IReadOnlyList<Guid> documentIds,
        CancellationToken cancellationToken = default)
    {
        if (documentIds is null)
            throw new ArgumentNullException(nameof(documentIds));

        if (documentIds.Count == 0)
            return false;

        return await RefreshInternalAsync(embeddingProvider, DistinctDocumentIds(documentIds), cancellationToken).ConfigureAwait(false);
    }

    // Tracks whether the contextual provider has been disabled at runtime due to failures.
    private bool _contextualDisabled;

    /// <summary>
    /// Connection-level failures disable contextual for the run.
    /// Payload-level failures (oversized input, validation) only skip the current batch.
    /// </summary>
    private static bool IsConnectionFailure(Exception ex)
    {
        // Walk the exception chain looking for connection/transport indicators
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is HttpRequestException or System.Net.Sockets.SocketException)
                return true;
            // gRPC unavailable/unauthenticated = service-level
            if (current.GetType().Name == "RpcException" &&
                current.Message.Contains("Unavailable", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Returns the model name and dimension of the active embedding provider.
    /// Contextual provider takes priority when available, initialized, and not runtime-disabled.
    /// Falls back to flat provider if contextual model info hasn't been fetched yet.
    /// </summary>
    private (string Model, int Dimension) ActiveModelInfo(IEmbeddingProvider flatProvider)
    {
        if (_contextualProvider is not null && !_contextualDisabled)
        {
            var model = _contextualProvider.Model;
            var dim = _contextualProvider.Dimension;
            if (model is not (null or "unknown") && dim > 0)
                return (model, dim);
        }
        return (flatProvider.Model, flatProvider.Dimension);
    }

    private async Task<bool> RefreshInternalAsync(
        IEmbeddingProvider embeddingProvider,
        IReadOnlyList<Guid>? targetDocumentIds,
        CancellationToken cancellationToken)
    {
        // At least one provider must be available.
        if (_contextualProvider is null && (embeddingProvider is null || !embeddingProvider.Enabled))
            return false;

        // Eagerly initialize contextual provider so ActiveModelInfo returns the correct model
        // before we build the refresh plan. Without this, documents with ONNX embeddings
        // would be skipped even when switching to a contextual model.
        if (_contextualProvider is not null && !_contextualDisabled)
        {
            try
            {
                await _contextualProvider.InitializeAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Contextual provider initialization failed, falling back to local embedding");
                _contextualDisabled = true;
            }
        }

        var (activeModel, _) = ActiveModelInfo(embeddingProvider);

        var refreshPlan = LoadDocumentRefreshPlan(targetDocumentIds, activeModel);
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

            return false;
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
            return false;

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
        var stats = await ConsumeAndWriteEmbeddingsAsync(channel.Reader, totalExpectedItems, cancellationToken).ConfigureAwait(false);

        // Wait for producer to complete (handles exceptions)
        await producerTask.ConfigureAwait(false);

        sw.Stop();
        LogEmbeddingCompletionStats(sw.Elapsed, stats, activeModel);
        return stats.DocSuccess > 0;
    }

    /// <summary>
    /// Removes embeddings where the referenced doc_id or node_id no longer exists in the node table.
    /// </summary>
    public int RemoveDangling()
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

        return deletedCount;
    }

    /// <summary>
    /// Clears all embeddings. Manual recovery tool — not called during normal operation.
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

        _logger.LogInformation("Cleared {Count} embeddings", deletedCount);
    }


    #endregion

    #region Private Helper Methods

    private int GetEffectiveBatchSize(IEmbeddingProvider provider)
    {
        var batchSize = _embeddingSettings.BatchSize is > 0
            ? _embeddingSettings.BatchSize.Value
            : DefaultEmbeddingBatchSize;

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
        var (activeModel, _) = ActiveModelInfo(provider);

        // Accumulate documents with their chunks. Flush when total chunks reach batchSize.
        var pendingDocs = new List<PendingDocument>();
        var pendingChunkCount = 0;

        async Task FlushAsync()
        {
            if (pendingDocs.Count == 0)
                return;

            ct.ThrowIfCancellationRequested();
            batchNum++;

            // Flatten all work items from pending documents.
            var allItems = pendingDocs.SelectMany(d => d.Items).ToArray();
            var itemsAfterBatch = itemsProcessed + allItems.Length;
            var progress = totalBatches > 0
                ? new BatchEmbeddingProgress(batchNum, totalBatches, itemsAfterBatch, totalExpectedItems, overallTimer.Elapsed)
                : default;

            float[]?[] vectors;
            var batchTimer = Stopwatch.StartNew();
            try
            {
                if (_contextualProvider is not null && !_contextualDisabled)
                {
                    try
                    {
                        vectors = await EmbedContextualAsync(pendingDocs, allItems.Length, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (IsConnectionFailure(ex))
                    {
                        // Service unreachable, auth failure, etc. — disable for remainder of this run.
                        _contextualDisabled = true;
                        _logger.LogWarning(ex,
                            "Contextual embedding service unavailable, falling back to local embedding for this run");
                        vectors = await EmbedFlatAsync(pendingDocs, allItems.Length, provider, progress, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        // Payload-level failure (oversized group, API validation, etc.).
                        // Don't disable contextual — next batch may succeed. Fall back for this batch only.
                        _logger.LogWarning(ex,
                            "Contextual embedding failed for this batch ({DocCount} docs), falling back to local embedding",
                            pendingDocs.Count);
                        vectors = await EmbedFlatAsync(pendingDocs, allItems.Length, provider, progress, ct).ConfigureAwait(false);
                    }
                }
                else
                {
                    vectors = await EmbedFlatAsync(pendingDocs, allItems.Length, provider, progress, ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                batchTimer.Stop();
                _logger.LogWarning(ex, "Embedding batch failed (size={BatchSize}, model={Model})", pendingChunkCount, activeModel);
                vectors = Array.Empty<float[]?>();
            }
            batchTimer.Stop();

            itemsProcessed = itemsAfterBatch;
            pendingDocs.Clear();
            pendingChunkCount = 0;

            var (batchModel, batchDim) = ActiveModelInfo(provider);
            await writer.WriteAsync(new EmbeddingBatchResult(allItems, vectors, batchModel, batchDim, batchTimer.Elapsed), ct).ConfigureAwait(false);
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

                    var item = new EmbeddingWorkItem(
                        doc.Id, doc.Id, ChunkIndex: 0, FullEmbeddingType,
                        doc.Uri, DocumentEmbeddingScope, StartByte: null, EndByte: null);

                    // Large docs: single chunk, no separate context (payload IS the structure).
                    pendingDocs.Add(new PendingDocument(doc.Uri, Context: null, [payload], [item]));
                    pendingChunkCount++;
                    if (pendingChunkCount >= batchSize)
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

                    var context = BuildPreamble(doc);

                    if (doc.Text.Length <= SmallFileThresholdChars)
                    {
                        // Small doc: single chunk. For the flat path, BuildDocumentEmbeddingText
                        // includes headline+summary+structure+text. For contextual, we send
                        // headline+summary as context and text as the chunk.
                        string chunk;
                        if (_contextualProvider is not null)
                        {
                            chunk = doc.Text;
                        }
                        else
                        {
                            var fullPayload = BuildDocumentEmbeddingText(doc);
                            if (string.IsNullOrWhiteSpace(fullPayload))
                                continue;
                            // For flat path, the full payload already includes context.
                            // Set context to null so FlushAsync doesn't double-prepend.
                            context = null;
                            chunk = fullPayload;
                        }

                        var item = new EmbeddingWorkItem(
                            doc.Id, doc.Id, ChunkIndex: 0, FullEmbeddingType,
                            doc.Uri, DocumentEmbeddingScope, StartByte: null, EndByte: null);

                        pendingDocs.Add(new PendingDocument(doc.Uri, context, [chunk], [item]));
                        pendingChunkCount++;
                        if (pendingChunkCount >= batchSize)
                            await FlushAsync().ConfigureAwait(false);
                        continue;
                    }

                    // Chunked doc: multiple chunks with shared context.
                    var chunkRanges = ChunkRanges(doc.Text, ChunkSizeChars, ChunkOverlapChars);
                    if (chunkRanges.Count == 0)
                        continue;

                    var utf8Offsets = ComputeUtf8ByteOffsets(doc.Text, chunkRanges);
                    var chunks = new List<string>(chunkRanges.Count);
                    var items = new List<EmbeddingWorkItem>(chunkRanges.Count);

                    for (var i = 0; i < chunkRanges.Count; i++)
                    {
                        var (startChar, endChar) = chunkRanges[i];
                        var chunkText = doc.Text[startChar..endChar];
                        if (string.IsNullOrWhiteSpace(chunkText))
                            continue;

                        chunks.Add(chunkText);
                        items.Add(new EmbeddingWorkItem(
                            doc.Id, doc.Id, ChunkIndex: i, FullEmbeddingType,
                            doc.Uri, DocumentEmbeddingScope,
                            StartByte: utf8Offsets[startChar],
                            EndByte: utf8Offsets[endChar]));
                    }

                    if (chunks.Count > 0)
                    {
                        pendingDocs.Add(new PendingDocument(doc.Uri, context, chunks, items));
                        pendingChunkCount += chunks.Count;
                        if (pendingChunkCount >= batchSize)
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
                WriteBatchBulk(validItems, batch.Model, batch.Dimension);
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

    private void WriteBatchBulk(List<(EmbeddingWorkItem Item, float[] Vec)> items, string model, int dimension)
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
                cmd.Parameters.Add(new DuckDBParameter { Value = model });
                cmd.Parameters.Add(new DuckDBParameter { Value = dimension });
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

    private void LogEmbeddingCompletionStats(TimeSpan elapsed, EmbeddingStats stats, string activeModel)
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
            activeModel);
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

    private IReadOnlyList<DocumentRefreshPlanRow> LoadDocumentRefreshPlan(IReadOnlyList<Guid>? targetDocumentIds, string activeModel)
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
                              AND de.model = '{activeModel}'
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

    // Voyage API context window is 32K tokens per group (context + all chunks).
    // We use the actual Voyage tokenizer for accurate counting (via VoyageTokenCounter).
    // 30K leaves headroom for tokenizer edge cases and API overhead.
    internal const int MaxContextualGroupTokens = 30_000;
    // Char-based fallback when tokenizer is unavailable: 2 chars/token is conservative
    // (overestimates tokens → smaller groups → safe but suboptimal).
    internal const int MaxContextualGroupChars = MaxContextualGroupTokens * 2;

    private async Task<float[]?[]> EmbedContextualAsync(
        List<PendingDocument> pendingDocs, int totalItems, CancellationToken ct)
    {
        var (groups, groupMeta) = BuildContextualGroups(pendingDocs, MaxContextualGroupTokens, _tokenCounter.Value);

        _logger.LogInformation(
            "Contextual embedding: {Docs} docs, {Groups} groups (after splitting), {TotalItems} items, {TotalChunks} total chunks",
            pendingDocs.Count, groups.Count, totalItems,
            groups.Sum(g => g.Chunks.Count));

        _contextualProvider!.SetUseCaseHint("batch");
        var result = await _contextualProvider.EmbedChunksAsync(groups, ct).ConfigureAwait(false);

        var nonNullVectors = result.Vectors.Count(v => v.Vector is { Length: > 0 });
        _logger.LogInformation(
            "Contextual embedding result: {VectorCount} vectors ({NonNull} non-null), {Tokens} tokens",
            result.Vectors.Count, nonNullVectors, result.TotalTokens);

        var mapped = MapContextualResults(result, pendingDocs, groupMeta, totalItems);

        return mapped;
    }

    /// <summary>
    /// Builds contextual embedding groups from pending documents, splitting oversized groups.
    /// Each group's total tokens (context + all chunks) must stay under maxGroupTokens
    /// to fit within the Voyage API's per-group context window (32K tokens).
    /// Uses the actual Voyage tokenizer for accurate counting when available.
    /// </summary>
    internal static (List<DocumentChunkGroup> Groups, List<(int DocIndex, int ChunkOffset)> GroupMeta)
        BuildContextualGroups(IReadOnlyList<PendingDocument> pendingDocs, int maxGroupTokens, VoyageTokenCounter? tokenCounter = null)
    {
        var groups = new List<DocumentChunkGroup>();
        var groupMeta = new List<(int DocIndex, int ChunkOffset)>();

        for (var d = 0; d < pendingDocs.Count; d++)
        {
            var doc = pendingDocs[d];
            var contextTokens = tokenCounter?.CountTokens(doc.Context) ?? FallbackTokenEstimate(doc.Context);
            var totalTokens = contextTokens;
            foreach (var chunk in doc.Chunks)
                totalTokens += tokenCounter?.CountTokens(chunk) ?? FallbackTokenEstimate(chunk);

            if (totalTokens <= maxGroupTokens)
            {
                groupMeta.Add((d, 0));
                groups.Add(new DocumentChunkGroup(doc.Uri, doc.Context, doc.Chunks));
            }
            else
            {
                // Split chunks across multiple groups, each sharing the same context.
                var maxChunkTokensPerGroup = maxGroupTokens - contextTokens;
                if (maxChunkTokensPerGroup < 1) maxChunkTokensPerGroup = 1;

                var chunkStart = 0;
                while (chunkStart < doc.Chunks.Count)
                {
                    var subChunks = new List<string>();
                    var subTokensUsed = 0;
                    for (var i = chunkStart; i < doc.Chunks.Count; i++)
                    {
                        var chunkTokens = tokenCounter?.CountTokens(doc.Chunks[i]) ?? FallbackTokenEstimate(doc.Chunks[i]);
                        // Always include at least one chunk per group.
                        if (subChunks.Count > 0 && subTokensUsed + chunkTokens > maxChunkTokensPerGroup)
                            break;
                        subChunks.Add(doc.Chunks[i]);
                        subTokensUsed += chunkTokens;
                    }

                    groupMeta.Add((d, chunkStart));
                    groups.Add(new DocumentChunkGroup(doc.Uri, doc.Context, subChunks));
                    chunkStart += subChunks.Count;
                }
            }
        }

        return (groups, groupMeta);
    }

    /// <summary>Conservative fallback: 2 chars/token overestimates tokens → safe splits.</summary>
    private static int FallbackTokenEstimate(string? text)
        => string.IsNullOrEmpty(text) ? 0 : (text.Length + 1) / 2;

    /// <summary>
    /// Maps contextual embedding results back to the flat item array.
    /// Handles split groups by using groupMeta to find the original doc and chunk offset.
    /// </summary>
    internal static float[]?[] MapContextualResults(
        ContextualEmbeddingResult result,
        IReadOnlyList<PendingDocument> pendingDocs,
        List<(int DocIndex, int ChunkOffset)> groupMeta,
        int totalItems)
    {
        // Compute flat item offset for each pendingDoc.
        var docItemOffset = new int[pendingDocs.Count];
        var offset = 0;
        for (var d = 0; d < pendingDocs.Count; d++)
        {
            docItemOffset[d] = offset;
            offset += pendingDocs[d].Items.Count;
        }

        var vectors = new float[]?[totalItems];
        foreach (var cv in result.Vectors)
        {
            if (cv.GroupIndex < 0 || cv.GroupIndex >= groupMeta.Count) continue;
            var (docIndex, chunkOffset) = groupMeta[cv.GroupIndex];
            var flatIdx = docItemOffset[docIndex] + chunkOffset + cv.ChunkIndex;
            if (flatIdx >= 0 && flatIdx < vectors.Length)
                vectors[flatIdx] = cv.Vector;
        }

        return vectors;
    }

    private static async Task<float[]?[]> EmbedFlatAsync(
        List<PendingDocument> pendingDocs, int totalItems, IEmbeddingProvider provider,
        BatchEmbeddingProgress progress, CancellationToken ct)
    {
        var payloads = new List<string>(totalItems);
        foreach (var doc in pendingDocs)
        {
            foreach (var chunk in doc.Chunks)
            {
                payloads.Add(string.IsNullOrWhiteSpace(doc.Context)
                    ? chunk
                    : $"{doc.Context}\n\n{chunk}");
            }
        }

        return await provider.EmbedPassageBatchAsync(payloads, progress, ct).ConfigureAwait(false);
    }

    private static string BuildStructureOnlyEmbeddingText(string? headline, string? structure)
    {
        // For large files, use headline + structure (they contain different data).
        // Truncate to fit within embedding model context window.
        return CombineSegments(new[] { headline, structure }, MaxEmbeddingPayloadChars);
    }

    // Preamble is the contextual embedding context — it must leave room for chunks.
    // Half the group budget keeps the split ratio reasonable.
    private const int MaxPreambleChars = MaxContextualGroupChars / 2;

    private static string BuildPreamble(TextDocumentEmbeddingRow doc)
    {
        // Build preamble from x-ray fields for contextual embedding context.
        // Structure is the richest field — it tells the model what the document contains
        // (class hierarchy, method signatures, etc.) so each chunk is embedded with
        // awareness of its surrounding document shape.
        // Ordered by density: headline (one-liner) → summary (paragraph) → structure (full outline).
        // CombineSegments truncates at MaxPreambleChars to guarantee chunk room.
        return CombineSegments(
            new[] { doc.Headline, doc.Summary, doc.Structure },
            MaxPreambleChars);
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
            MaxEmbeddingPayloadChars);
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

    /// <summary>
    /// A document with its chunks ready for embedding. The producer accumulates these;
    /// FlushAsync decides whether to use contextual or flat embedding.
    /// </summary>
    internal sealed record PendingDocument(
        string Uri,
        string? Context,
        IReadOnlyList<string> Chunks,
        IReadOnlyList<EmbeddingWorkItem> Items);

    internal readonly record struct EmbeddingWorkItem(
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
        string Model,
        int Dimension,
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
