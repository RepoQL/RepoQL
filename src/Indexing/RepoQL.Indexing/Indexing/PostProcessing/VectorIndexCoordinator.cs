using System.Diagnostics;
using Humanizer;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RepoQL.Contracts;
using RepoQL.Contracts.Data;
using RepoQL.Contracts.Embeddings;
using RepoQL.Contracts.Models;
using RepoQL.Indexing.Indexing.Pipelines;
using RepoQL.Data.DuckDB;
using static RepoQL.Contracts.Embeddings.EmbeddingModeExtensions;

namespace RepoQL.Indexing.Indexing.PostProcessing;

/// <summary>
/// Coordinates post-index vector refreshes. The heavy lifting is delegated to an <see cref="IVectorIndexRefresher"/>.
/// </summary>
/// <remarks>
/// <para><strong>Environment Variables:</strong></para>
/// <list type="bullet">
///   <item><c>REPOQL_EMBED_CONCURRENCY</c> - Max concurrent refresh operations (default: 1). Increase to allow parallel embedding batches for higher throughput.</item>
/// </list>
/// </remarks>
public sealed class VectorIndexCoordinator : IVectorIndexCoordinator, IDisposable
{
    private const int StructureEmbeddingBatchSize = 100;
    internal const int RegistrySyncBatchSize = 256;
    private static readonly TimeSpan VssRefreshDebounce = TimeSpan.FromMilliseconds(250);
    private const string MetadataValueTrue = "true";
    private const string MetadataValueFalse = "false";
    private static readonly int RefreshConcurrency = GetRefreshConcurrency();
    private readonly IVectorIndexRefresher _refresher;
    private readonly DuckDbDataStore? _db;
    private readonly IEmbeddingProvider? _embeddingProvider;
    private readonly EmbeddingMode _embeddingMode;
    private readonly ILogger<VectorIndexCoordinator> _logger;
    private readonly UriRegistry? _uriRegistry;
    private readonly Func<IVssIndexManager>? _vssIndexManagerFactory;
    private readonly SemaphoreSlim _refreshGate = new(RefreshConcurrency, RefreshConcurrency);
    private readonly SemaphoreSlim _vssRefreshSignal = new(0);
    private readonly CancellationTokenSource _vssRefreshShutdown = new();
    private long _lastRefreshedEpoch = long.MinValue;
    private volatile bool _needsRefresh;
    private IVssIndexManager? _vssIndexManager;
    private Task? _vssRefreshWorker;
    private int _vssWorkerStarted;
    private int _vssInitialBuildCompleted;
    private int _vssRefreshRequested;
    private int _vssStructureReadyState = -1;

    private static int GetRefreshConcurrency()
    {
        if (int.TryParse(Environment.GetEnvironmentVariable("REPOQL_EMBED_CONCURRENCY"), out var c) && c > 0)
            return c;
        return 2; // Default to 2 concurrent batches for better throughput
    }

    public VectorIndexCoordinator(
        DuckDbDataStore database,
        IEmbeddingProvider embeddingProvider,
        EmbeddingMode embeddingMode = EmbeddingMode.Full,
        ILogger<VectorIndexCoordinator>? logger = null,
        UriRegistry? uriRegistry = null)
        : this(new DuckDbVectorIndexRefresher(database, embeddingProvider, embeddingMode), database, embeddingProvider, embeddingMode, logger, uriRegistry)
    {
    }

    internal VectorIndexCoordinator(
        IVectorIndexRefresher refresher,
        DuckDbDataStore? db = null,
        IEmbeddingProvider? embeddingProvider = null,
        EmbeddingMode embeddingMode = EmbeddingMode.Full,
        ILogger<VectorIndexCoordinator>? logger = null,
        UriRegistry? uriRegistry = null,
        Func<IVssIndexManager>? vssIndexManagerFactory = null)
    {
        _refresher = refresher ?? throw new ArgumentNullException(nameof(refresher));
        _db = db;
        _embeddingProvider = embeddingProvider;
        _embeddingMode = embeddingMode;
        _logger = logger ?? NullLogger<VectorIndexCoordinator>.Instance;
        _uriRegistry = uriRegistry;
        _vssIndexManagerFactory = vssIndexManagerFactory;

        // VSS is ephemeral; force semantic fallback until this process completes an in-memory rebuild.
        SetVssStructureReadyMetadata(isReady: false);

        // VSS indexes are in-memory only; always schedule an initial rebuild after startup.
        RequestVssRefresh();
    }

    public Task ApplyDeletesAsync(IReadOnlyList<RepoUri> deletedArtifacts, CancellationToken cancellationToken)
    {
        if (deletedArtifacts.Count == 0)
            return Task.CompletedTask;

        _needsRefresh = true;
        RequestVssRefresh();
        return Task.CompletedTask;
    }

    public async Task ApplyAsync(IReadOnlyList<IndexItem> items, CancellationToken cancellationToken)
    {
        if (items.Count == 0)
            return;

        var epoch = GetLatestEpoch(items);
        if (!_needsRefresh && Interlocked.Read(ref _lastRefreshedEpoch) == epoch)
            return;

        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_needsRefresh && Interlocked.Read(ref _lastRefreshedEpoch) == epoch)
                return;

            var forceFullRefresh = _needsRefresh;
            var targetDocumentIds = forceFullRefresh ? [] : CollectDirtyDocumentIds(items);

            var embeddingsChanged = await RefreshEmbeddingsAsync(targetDocumentIds, forceFullRefresh, cancellationToken).ConfigureAwait(false);
            Interlocked.Exchange(ref _lastRefreshedEpoch, epoch);
            _needsRefresh = false;
            if (embeddingsChanged)
            {
                RequestVssRefresh();
            }
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private async Task<bool> RefreshEmbeddingsAsync(
        IReadOnlyList<Guid> targetDocumentIds,
        bool forceFullRefresh,
        CancellationToken cancellationToken)
    {
        var runFullRefresh = forceFullRefresh || targetDocumentIds.Count == 0;
        _logger.LogDebug("Vector index refresh triggered (mode={Mode}, docs={DocCount})",
            runFullRefresh ? "full" : "targeted",
            targetDocumentIds.Count);

        var embeddingsChanged = false;
        try
        {
            if (runFullRefresh)
            {
                embeddingsChanged = await _refresher.RefreshAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                embeddingsChanged = await _refresher.RefreshAsync(targetDocumentIds, cancellationToken).ConfigureAwait(false);
            }

            _logger.LogDebug("Vector index refresh completed");

            // Sync UriRegistry with actual embedding counts from the database
            if (embeddingsChanged)
            {
                SyncRegistryEmbeddingStatus(runFullRefresh ? null : targetDocumentIds);
            }

            return embeddingsChanged;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Vector index refresh failed");
            throw;
        }
    }

    /// <summary>
    /// Syncs the UriRegistry embedding status from the database after full-text embedding refresh.
    /// This ensures the registry reflects actual embedding counts including chunked documents.
    /// </summary>
    private void SyncRegistryEmbeddingStatus(IReadOnlyList<Guid>? documentIds)
    {
        if (_uriRegistry is null || _db is null)
            return;

        try
        {
            var chunkCountsByContainer = new Dictionary<string, int>(StringComparer.Ordinal);

            if (documentIds is { Count: > 0 })
            {
                foreach (var idBatch in BatchDocumentIds(documentIds))
                {
                    var idList = ToUuidListSql(idBatch);
                    var targetedQuery = $"""
                        SELECT
                            repository_uri_container(uri) as container_uri,
                            COUNT(*) as chunk_count
                        FROM document_embedding
                        WHERE doc_id IN ({idList})
                        GROUP BY repository_uri_container(uri)
                        """;

                    var batchResults = _db.Read(targetedQuery, record =>
                    {
                        var containerUri = record["container_uri"]?.ToString();
                        var chunkCount = Convert.ToInt32(record["chunk_count"]);
                        return (containerUri, chunkCount);
                    });

                    MergeChunkCounts(chunkCountsByContainer, batchResults);
                }
            }
            else
            {
                const string fullQuery = """
                    SELECT
                        repository_uri_container(uri) as container_uri,
                        COUNT(*) as chunk_count
                    FROM document_embedding
                    GROUP BY repository_uri_container(uri)
                    """;

                var results = _db.Read(fullQuery, record =>
                {
                    var containerUri = record["container_uri"]?.ToString();
                    var chunkCount = Convert.ToInt32(record["chunk_count"]);
                    return (containerUri, chunkCount);
                });

                MergeChunkCounts(chunkCountsByContainer, results);
            }

            foreach (var (containerUriStr, chunkCount) in chunkCountsByContainer)
            {
                if (string.IsNullOrEmpty(containerUriStr))
                    continue;

                if (!RepoUri.TryParse(containerUriStr, out var containerUri))
                    continue;

                if (_uriRegistry.TryGetValue(containerUri, out _))
                {
                    _uriRegistry.SetEmbedded(containerUri, chunkCount);
                }
            }

            _logger.LogDebug("UriRegistry embedding status synced from database");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to sync UriRegistry embedding status from database");
        }
    }

    private static void MergeChunkCounts(
        IDictionary<string, int> destination,
        IReadOnlyList<(string? ContainerUri, int ChunkCount)> source)
    {
        foreach (var (containerUri, chunkCount) in source)
        {
            if (string.IsNullOrWhiteSpace(containerUri))
                continue;

            destination.TryGetValue(containerUri, out var existingCount);
            destination[containerUri] = existingCount + chunkCount;
        }
    }

    internal static IReadOnlyList<Guid[]> BatchDocumentIds(IReadOnlyList<Guid> documentIds)
    {
        if (documentIds.Count == 0)
            return [];

        var batches = new List<Guid[]>();
        var seen = new HashSet<Guid>();
        var currentBatch = new List<Guid>(Math.Min(RegistrySyncBatchSize, documentIds.Count));

        for (var i = 0; i < documentIds.Count; i++)
        {
            var id = documentIds[i];
            if (!seen.Add(id))
                continue;

            currentBatch.Add(id);
            if (currentBatch.Count < RegistrySyncBatchSize)
                continue;

            batches.Add([.. currentBatch]);
            currentBatch.Clear();
        }

        if (currentBatch.Count > 0)
            batches.Add([.. currentBatch]);

        return batches;
    }

    private static IReadOnlyList<Guid> CollectDirtyDocumentIds(IReadOnlyList<IndexItem> items)
    {
        var documentIds = new HashSet<Guid>();

        for (var i = 0; i < items.Count; i++)
        {
            var docNode = FindDocumentNode(items[i].Records?.Nodes);
            if (docNode is null || docNode.Id == Guid.Empty)
                continue;

            documentIds.Add(docNode.Id);
        }

        return [.. documentIds];
    }

    private static long GetLatestEpoch(IReadOnlyList<IndexItem> items)
    {
        var epoch = items[0].Epoch;
        for (var i = 1; i < items.Count; i++)
        {
            if (items[i].Epoch > epoch)
                epoch = items[i].Epoch;
        }

        return epoch;
    }

    private static string ToUuidListSql(IReadOnlyList<Guid> ids)
    {
        return string.Join(",", ids.Select(id => $"'{id:D}'::UUID"));
    }

    public async Task GenerateStructureEmbeddingsAsync(IReadOnlyList<IndexItem> items, CancellationToken cancellationToken)
    {
        if (items.Count == 0)
            return;

        // Check embedding mode - structure embeddings require at least StructureOnly mode
        if (!_embeddingMode.IncludesStructure())
        {
            _logger.LogDebug("Structure embedding skipped: mode={Mode}", _embeddingMode);
            MarkItemsAsNotApplicable(items);
            return;
        }

        if (_db is null || _embeddingProvider is null || !_embeddingProvider.Enabled)
        {
            _logger.LogDebug("Structure embedding skipped: db={Db}, provider={Provider}, enabled={Enabled}",
                _db is not null, _embeddingProvider is not null, _embeddingProvider?.Enabled);
            MarkItemsAsNotApplicable(items);
            return;
        }

        var timer = Stopwatch.StartNew();

        // Pre-count candidates to keep progress reporting accurate.
        var totalWorkItems = CountStructureEmbeddingCandidates(
            items,
            out var withRecords,
            out var withArtifacts,
            out var withDocNodes,
            out var alreadyEmbedded);

        _logger.LogInformation(
            "Structure embedding: {Total} items, {WithRecords} with records, {WithArtifacts} with artifacts, {WithDocNodes} with docNodes, {AlreadyEmbedded} already embedded, {WorkItems} work items",
            items.Count, withRecords, withArtifacts, withDocNodes, alreadyEmbedded, totalWorkItems);

        if (totalWorkItems == 0)
        {
            _logger.LogDebug("No structure embeddings to generate");
            return;
        }

        _logger.LogInformation("Generating {Count} structure embeddings...", totalWorkItems);

        // Generate embeddings in batches and write each batch immediately.
        var totalWritten = 0;
        var totalBatches = (totalWorkItems + StructureEmbeddingBatchSize - 1) / StructureEmbeddingBatchSize;
        var batchNum = 0;
        var itemsProcessed = 0;

        using var structureActivity = IndexingEngine.ActivitySource.StartActivity("repoql.embedding.structure", ActivityKind.Internal);
        structureActivity?.SetTag("items", totalWorkItems);
        structureActivity?.SetTag("batches", totalBatches);
        structureActivity?.SetTag("model", _embeddingProvider.Model);

        var batch = new List<StructureWorkItem>(StructureEmbeddingBatchSize);
        foreach (var item in items)
        {
            if (IsAlreadyEmbedded(item.Uri))
                continue;

            if (!TryBuildStructureWorkItem(item, out var work))
                continue;

            batch.Add(work);
            if (batch.Count < StructureEmbeddingBatchSize)
                continue;

            batchNum++;
            itemsProcessed += batch.Count;
            totalWritten += await EmbedStructureBatchAsync(batch, batchNum, totalBatches, itemsProcessed, totalWorkItems, timer, cancellationToken)
                .ConfigureAwait(false);
            batch.Clear();
        }

        if (batch.Count > 0)
        {
            batchNum++;
            itemsProcessed += batch.Count;
            totalWritten += await EmbedStructureBatchAsync(batch, batchNum, totalBatches, itemsProcessed, totalWorkItems, timer, cancellationToken)
                .ConfigureAwait(false);
        }

        timer.Stop();
        _logger.LogInformation("Structure embeddings complete: {Count} embeddings in {Time}",
            totalWritten, timer.Elapsed.Humanize(precision: 2, minUnit: Humanizer.Localisation.TimeUnit.Millisecond));
    }

    private readonly record struct StructureWorkItem(Guid DocId, Guid NodeId, string Uri, string Payload);

    private int CountStructureEmbeddingCandidates(
        IReadOnlyList<IndexItem> items,
        out int withRecords,
        out int withArtifacts,
        out int withDocNodes,
        out int alreadyEmbedded)
    {
        var total = 0;
        withRecords = 0;
        withArtifacts = 0;
        withDocNodes = 0;
        alreadyEmbedded = 0;

        foreach (var item in items)
        {
            var records = item.Records;
            if (records is not null) withRecords++;

            var artifacts = records?.Artifacts;
            var artifact = artifacts is { Length: > 0 } ? artifacts[0] : null;
            if (artifact is not null) withArtifacts++;

            var docNode = FindDocumentNode(records?.Nodes);
            if (docNode is not null) withDocNodes++;

            if (artifact is not null && docNode is not null)
            {
                if (IsAlreadyEmbedded(item.Uri))
                {
                    alreadyEmbedded++;
                    continue;
                }

                total++;
            }
        }

        return total;
    }

    private bool IsAlreadyEmbedded(RepoUri uri)
    {
        if (_uriRegistry is null)
            return false;

        return _uriRegistry.TryGetValue(uri, out var entry)
               && entry.EmbeddingStatus == EmbeddingStatus.Embedded;
    }

    private static bool TryBuildStructureWorkItem(IndexItem item, out StructureWorkItem work)
    {
        work = default;
        if (!TryBuildStructureEmbedding(item, out var documentNodeId, out var uri, out var payload))
            return false;

        work = new StructureWorkItem(documentNodeId, documentNodeId, uri, payload);
        return true;
    }

    internal static bool TryBuildStructureEmbedding(IndexItem item, out Guid documentNodeId, out string uri, out string payload)
    {
        documentNodeId = default;
        uri = item.Uri.ToString();
        payload = string.Empty;

        var records = item.Records;
        var artifacts = records?.Artifacts;
        var artifact = artifacts is { Length: > 0 } ? artifacts[0] : null;
        if (artifact is null)
            return false;

        var docNode = FindDocumentNode(records?.Nodes);
        if (docNode is null)
            return false;

        payload = BuildStructurePayload(uri, artifact.Headline, artifact.Structure);
        if (string.IsNullOrWhiteSpace(payload))
            return false;

        documentNodeId = docNode.Id;
        return true;
    }

    internal static Node? FindDocumentNode(Node[]? nodes)
    {
        if (nodes is null || nodes.Length == 0)
            return null;

        for (var i = 0; i < nodes.Length; i++)
        {
            var n = nodes[i];
            if (n.Kind == "document")
                return n;
        }

        return null;
    }

    private async Task<int> EmbedStructureBatchAsync(
        IReadOnlyList<StructureWorkItem> batch,
        int batchNumber,
        int totalBatches,
        int itemsProcessed,
        int totalItems,
        Stopwatch timer,
        CancellationToken cancellationToken)
    {
        var batchCount = batch.Count;
        var payloads = new string[batchCount];
        for (var i = 0; i < batchCount; i++)
            payloads[i] = batch[i].Payload;

        var progress = new BatchEmbeddingProgress(batchNumber, totalBatches, itemsProcessed, totalItems, timer.Elapsed);

        var batchTimer = Stopwatch.StartNew();
        float[]?[] vectors;
        using (var batchActivity = IndexingEngine.ActivitySource.StartActivity("repoql.embedding.structure.batch", ActivityKind.Internal))
        {
            batchActivity?.SetTag("batch", batchNumber);
            batchActivity?.SetTag("total_batches", totalBatches);
            batchActivity?.SetTag("size", batchCount);

            vectors = await _embeddingProvider!.EmbedPassageBatchAsync(payloads, progress, cancellationToken).ConfigureAwait(false);
        }
        batchTimer.Stop();

        var percentComplete = (int)(itemsProcessed * 100.0 / totalItems);
        var eta = progress.EstimatedRemaining;
        var etaStr = eta.HasValue && eta.Value > TimeSpan.Zero
            ? $", ETA {eta.Value.Humanize(precision: 2, minUnit: Humanizer.Localisation.TimeUnit.Second)}"
            : "";
        _logger.LogInformation("Structure embeddings: {Batch}/{Total} ({Percent}%) - {BatchSize} items in {Time}{Eta}",
            batchNumber, totalBatches, percentComplete, batchCount,
            batchTimer.Elapsed.Humanize(precision: 2, minUnit: Humanizer.Localisation.TimeUnit.Millisecond), etaStr);

        var documentEmbeddings = new List<DocumentEmbedding>(batchCount);
        for (var i = 0; i < batchCount; i++)
        {
            var vec = (vectors != null && i < vectors.Length) ? vectors[i] : null;
            if (vec is null || vec.Length == 0)
                continue;

            var work = batch[i];
            documentEmbeddings.Add(new DocumentEmbedding(
                work.DocId,
                work.NodeId,
                ChunkIndex: 0, // structure embeddings are always chunk 0
                DocumentEmbedding.TypeStructure,
                work.Uri,
                DocumentEmbedding.ScopeDocument,
                vec,
                _embeddingProvider!.Model,
                _embeddingProvider!.Dimension));
        }

        if (documentEmbeddings.Count > 0)
        {
            _db!.WriteEmbeddings(documentEmbeddings);
            RequestVssRefresh();

            // Update UriRegistry for successfully embedded files
            if (_uriRegistry is not null)
            {
                foreach (var embedding in documentEmbeddings)
                {
                    if (RepoUri.TryParse(embedding.Uri, out var uri))
                    {
                        // Structure embeddings are chunk 0, count as 1 chunk
                        _uriRegistry.SetEmbedded(uri, 1);
                    }
                }
            }
        }

        return documentEmbeddings.Count;
    }

    internal static string BuildStructurePayload(string uri, string? headline, string? structure)
    {
        // Build payload: relative uri + headline + structure
        var relativeUri = uri;
        if (relativeUri.StartsWith("file:///", StringComparison.OrdinalIgnoreCase))
            relativeUri = relativeUri[8..];
        relativeUri = relativeUri.Replace('\\', '/');

        var hasHeadline = !string.IsNullOrWhiteSpace(headline);
        var hasStructure = !string.IsNullOrWhiteSpace(structure);

        if (!hasHeadline && !hasStructure)
            return relativeUri;

        if (hasHeadline && !hasStructure)
            return string.Concat(relativeUri, "\n\n", headline);

        if (!hasHeadline)
            return string.Concat(relativeUri, "\n\n", structure);

        return string.Concat(relativeUri, "\n\n", headline, "\n\n", structure);
    }

    public Task RefreshVssIndexAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled(cancellationToken);

        if (_db is null && _vssIndexManagerFactory is null)
        {
            _logger.LogDebug("VSS index refresh skipped: no database");
            return Task.CompletedTask;
        }

        // VSS indexes are in-memory only, so always build once after startup.
        if (Volatile.Read(ref _vssInitialBuildCompleted) == 0)
            RequestVssRefresh();

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _vssRefreshShutdown.Cancel();
        try
        {
            _vssRefreshWorker?.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "VSS refresh worker stopped with an error during disposal");
        }

        _vssRefreshSignal.Dispose();
        _vssRefreshShutdown.Dispose();
        _refreshGate.Dispose();
    }

    /// <summary>
    /// Gets the last epoch that was refreshed for embeddings.
    /// </summary>
    public long GetLastRefreshedEpoch() => Interlocked.Read(ref _lastRefreshedEpoch);

    /// <summary>
    /// Gets whether the coordinator needs a refresh (e.g., due to deletes).
    /// </summary>
    public bool GetNeedsRefresh() => _needsRefresh;

    /// <summary>
    /// Gets the current embedding mode.
    /// </summary>
    public EmbeddingMode GetEmbeddingMode() => _embeddingMode;

    private void RequestVssRefresh()
    {
        if (_db is null && _vssIndexManagerFactory is null)
            return;

        EnsureVssRefreshWorkerStarted();
        if (Interlocked.Exchange(ref _vssRefreshRequested, 1) == 0)
        {
            _vssRefreshSignal.Release();
        }
    }

    private void EnsureVssRefreshWorkerStarted()
    {
        if (Interlocked.CompareExchange(ref _vssWorkerStarted, 1, 0) != 0)
            return;

        _vssRefreshWorker = Task.Run(ProcessVssRefreshLoopAsync);
    }

    private async Task ProcessVssRefreshLoopAsync()
    {
        try
        {
            while (true)
            {
                await _vssRefreshSignal.WaitAsync(_vssRefreshShutdown.Token).ConfigureAwait(false);
                await Task.Delay(VssRefreshDebounce, _vssRefreshShutdown.Token).ConfigureAwait(false);

                if (Interlocked.Exchange(ref _vssRefreshRequested, 0) == 0)
                    continue;

                try
                {
                    SetVssStructureReadyMetadata(isReady: false);
                    _vssIndexManager ??= _vssIndexManagerFactory?.Invoke() ?? new VssIndexManager(_db!);
                    await _vssIndexManager.RefreshIndexesAsync(forceRefresh: true, cancellationToken: _vssRefreshShutdown.Token).ConfigureAwait(false);
                    Volatile.Write(ref _vssInitialBuildCompleted, 1);
                    SetVssStructureReadyMetadata(isReady: true);
                }
                catch (OperationCanceledException) when (_vssRefreshShutdown.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "VSS index refresh failed");
                    RequestVssRefresh();
                }
            }
        }
        catch (OperationCanceledException) when (_vssRefreshShutdown.IsCancellationRequested)
        {
        }
    }

    private void SetVssStructureReadyMetadata(bool isReady)
    {
        var next = isReady ? 1 : 0;
        var previous = Interlocked.Exchange(ref _vssStructureReadyState, next);
        if (previous == next)
            return;

        if (_db is null)
            return;

        try
        {
            _db.WriteMetadataValue(
                DuckDbDataStore.MetadataKeyVssStructureReady,
                isReady ? MetadataValueTrue : MetadataValueFalse);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to update VSS readiness metadata");
        }
    }

    /// <summary>
    /// Marks all items as NotApplicable for embedding when embeddings are disabled.
    /// This allows operations tracking these URIs to complete.
    /// </summary>
    private void MarkItemsAsNotApplicable(IReadOnlyList<IndexItem> items)
    {
        if (_uriRegistry is null)
            return;

        foreach (var item in items)
        {
            _uriRegistry.SetEmbeddingNotApplicable(item.Uri);
        }

        _logger.LogDebug("Marked {Count} items as embedding NotApplicable", items.Count);
    }
}
