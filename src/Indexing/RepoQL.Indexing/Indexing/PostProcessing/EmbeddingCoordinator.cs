using System.Diagnostics;
using Humanizer;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RepoQL.Contracts;
using RepoQL.Contracts.Configuration;
using RepoQL.Contracts.Data;
using RepoQL.Contracts.Embeddings;
using RepoQL.Contracts.Models;
using RepoQL.Data.DuckDB;
using RepoQL.Indexing.Indexing.Pipelines;
using static RepoQL.Contracts.Embeddings.EmbeddingModeExtensions;

namespace RepoQL.Indexing.Indexing.PostProcessing;

/// <summary>
/// Coordinates post-index embedding generation and refresh work.
/// </summary>
public sealed class EmbeddingCoordinator : IEmbeddingCoordinator, IDisposable
{
    private const int StructureEmbeddingBatchSize = 500;
    internal const int RegistrySyncBatchSize = 256;

    private readonly IEmbeddingRefreshRunner _refreshRunner;
    private readonly DuckDbDataStore? _db;
    private readonly IEmbeddingProvider? _embeddingProvider;
    private readonly IContextualEmbeddingProvider? _contextualProvider;
    private readonly EmbeddingMode _embeddingMode;
    private readonly ILogger<EmbeddingCoordinator> _logger;
    private readonly UriRegistry? _uriRegistry;
    private readonly SemaphoreSlim _refreshGate;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task? _startupRefreshTask;
    private long _lastRefreshedEpoch = long.MinValue;
    private volatile bool _needsRefresh;

    private static int ResolveRefreshConcurrency(RepoQlConfig.EmbeddingSettings? settings)
        => settings?.Concurrency is > 0 and var configured ? configured : 2;

    public EmbeddingCoordinator(
        DuckDbDataStore database,
        IEmbeddingProvider embeddingProvider,
        EmbeddingMode embeddingMode = EmbeddingMode.Full,
        ILogger<EmbeddingCoordinator>? logger = null,
        UriRegistry? uriRegistry = null,
        RepoQlConfig.EmbeddingSettings? embeddingSettings = null,
        IContextualEmbeddingProvider? contextualProvider = null)
        : this(
            new DuckDbEmbeddingRefreshRunner(
                database,
                embeddingProvider,
                embeddingMode,
                logger: logger,
                embeddingSettings: embeddingSettings,
                contextualProvider: contextualProvider),
            database,
            embeddingProvider,
            embeddingMode,
            logger,
            uriRegistry,
            embeddingSettings,
            contextualProvider: contextualProvider)
    {
    }

    internal EmbeddingCoordinator(
        IEmbeddingRefreshRunner refreshRunner,
        DuckDbDataStore? db = null,
        IEmbeddingProvider? embeddingProvider = null,
        EmbeddingMode embeddingMode = EmbeddingMode.Full,
        ILogger<EmbeddingCoordinator>? logger = null,
        UriRegistry? uriRegistry = null,
        RepoQlConfig.EmbeddingSettings? embeddingSettings = null,
        IContextualEmbeddingProvider? contextualProvider = null)
    {
        _refreshRunner = refreshRunner ?? throw new ArgumentNullException(nameof(refreshRunner));
        _db = db;
        _embeddingProvider = embeddingProvider;
        _contextualProvider = contextualProvider is { Enabled: true } ? contextualProvider : null;
        _embeddingMode = embeddingMode;
        _logger = logger ?? NullLogger<EmbeddingCoordinator>.Instance;
        _uriRegistry = uriRegistry;
        var refreshConcurrency = ResolveRefreshConcurrency(embeddingSettings);
        _refreshGate = new SemaphoreSlim(refreshConcurrency, refreshConcurrency);

        if (_db is not null)
            _startupRefreshTask = Task.Run(() => TriggerStartupContentRefreshAsync(_shutdown.Token));
    }

    public Task ApplyDeletesAsync(IReadOnlyList<RepoUri> deletedArtifacts, CancellationToken cancellationToken)
    {
        if (deletedArtifacts.Count > 0)
            _needsRefresh = true;

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
                SyncRegistryEmbeddingStatus(forceFullRefresh ? null : targetDocumentIds);
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
        _logger.LogDebug("Embedding refresh triggered (mode={Mode}, docs={DocCount})",
            runFullRefresh ? "full" : "targeted",
            targetDocumentIds.Count);

        try
        {
            var embeddingsChanged = runFullRefresh
                ? await _refreshRunner.RefreshAsync(cancellationToken).ConfigureAwait(false)
                : await _refreshRunner.RefreshAsync(targetDocumentIds, cancellationToken).ConfigureAwait(false);

            _logger.LogDebug("Embedding refresh completed");
            return embeddingsChanged;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Embedding refresh failed");
            throw;
        }
    }

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
                    _uriRegistry.SetEmbedded(containerUri, chunkCount);
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
        => string.Join(",", ids.Select(id => $"'{id:D}'::UUID"));

    public async Task GenerateStructureEmbeddingsAsync(IReadOnlyList<IndexItem> items, CancellationToken cancellationToken)
    {
        if (items.Count == 0)
            return;

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
        var totalWorkItems = CountStructureEmbeddingCandidates(
            items,
            out var withRecords,
            out var withArtifacts,
            out var withDocNodes,
            out var alreadyEmbedded);

        _logger.LogDebug(
            "Structure embedding: {Total} items, {WithRecords} with records, {WithArtifacts} with artifacts, {WithDocNodes} with docNodes, {AlreadyEmbedded} already embedded, {WorkItems} work items",
            items.Count, withRecords, withArtifacts, withDocNodes, alreadyEmbedded, totalWorkItems);

        if (totalWorkItems == 0)
        {
            _logger.LogDebug("No structure embeddings to generate");
            return;
        }

        _logger.LogDebug("Generating {Count} structure embeddings...", totalWorkItems);

        var totalWritten = 0;
        var totalBatches = (totalWorkItems + StructureEmbeddingBatchSize - 1) / StructureEmbeddingBatchSize;
        var batchNum = 0;
        var itemsProcessed = 0;

        using var structureActivity = IndexingEngine.ActivitySource.StartActivity("repoql.embedding.structure", ActivityKind.Internal);
        structureActivity?.SetTag("items", totalWorkItems);
        structureActivity?.SetTag("batches", totalBatches);
        structureActivity?.SetTag("model", _contextualProvider?.Model ?? _embeddingProvider.Model);

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
        string activeModel;
        int activeDimension;
        var usedContextual = false;

        using (var batchActivity = IndexingEngine.ActivitySource.StartActivity("repoql.embedding.structure.batch", ActivityKind.Internal))
        {
            batchActivity?.SetTag("batch", batchNumber);
            batchActivity?.SetTag("total_batches", totalBatches);
            batchActivity?.SetTag("size", batchCount);

            // Prefer contextual provider (Voyage) for structure embeddings so they share
            // the same vector space as full-content and query embeddings.
            if (_contextualProvider is not null)
            {
                try
                {
                    var groups = payloads.Select((p, i) =>
                        new DocumentChunkGroup(batch[i].Uri, Context: null, new[] { p })).ToList();
                    var result = await _contextualProvider.EmbedChunksAsync(groups, cancellationToken).ConfigureAwait(false);
                    vectors = new float[]?[batchCount];
                    foreach (var cv in result.Vectors)
                    {
                        if (cv.GroupIndex >= 0 && cv.GroupIndex < batchCount)
                            vectors[cv.GroupIndex] = cv.Vector;
                    }
                    usedContextual = true;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Contextual structure embedding failed, falling back to local");
                    vectors = await _embeddingProvider!.EmbedPassageBatchAsync(payloads, progress, cancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
                vectors = await _embeddingProvider!.EmbedPassageBatchAsync(payloads, progress, cancellationToken).ConfigureAwait(false);
            }

            if (usedContextual)
            {
                activeModel = _contextualProvider!.Model;
                activeDimension = _contextualProvider.Dimension;
            }
            else
            {
                activeModel = _embeddingProvider!.Model;
                activeDimension = _embeddingProvider.Dimension;
            }
        }
        batchTimer.Stop();

        var percentComplete = (int)(itemsProcessed * 100.0 / totalItems);
        var eta = progress.EstimatedRemaining;
        var etaStr = eta.HasValue && eta.Value > TimeSpan.Zero
            ? $", ETA {eta.Value.Humanize(precision: 2, minUnit: Humanizer.Localisation.TimeUnit.Second)}"
            : "";
        if (totalBatches > 1)
        {
            _logger.LogInformation("Structure embeddings: {Batch}/{Total} ({Percent}%) - {BatchSize} items in {Time}{Eta} ({Provider})",
                batchNumber, totalBatches, percentComplete, batchCount,
                batchTimer.Elapsed.Humanize(precision: 2, minUnit: Humanizer.Localisation.TimeUnit.Millisecond), etaStr,
                usedContextual ? "contextual" : "local");
        }

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
                ChunkIndex: 0,
                DocumentEmbedding.TypeStructure,
                work.Uri,
                DocumentEmbedding.ScopeDocument,
                vec,
                activeModel,
                activeDimension));
        }

        if (documentEmbeddings.Count > 0)
        {
            _db!.WriteEmbeddings(documentEmbeddings);

            if (_uriRegistry is not null)
            {
                foreach (var embedding in documentEmbeddings)
                {
                    if (RepoUri.TryParse(embedding.Uri, out var uri))
                        _uriRegistry.SetEmbedded(uri, 1);
                }
            }
        }

        return documentEmbeddings.Count;
    }

    internal static string BuildStructurePayload(string uri, string? headline, string? structure)
    {
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

    public void Dispose()
    {
        _shutdown.Cancel();
        try
        {
            _startupRefreshTask?.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Startup embedding catch-up stopped with an error during disposal");
        }

        _shutdown.Dispose();
        _refreshGate.Dispose();
    }

    public long GetLastRefreshedEpoch() => Interlocked.Read(ref _lastRefreshedEpoch);

    public bool GetNeedsRefresh() => _needsRefresh;

    public EmbeddingMode GetEmbeddingMode() => _embeddingMode;

    private async Task TriggerStartupContentRefreshAsync(CancellationToken cancellationToken)
    {
        if (_db is null)
            return;

        try
        {
            var hasContentEmbeddings = (_db.ReadScalar<long?>(
                "SELECT 1 FROM document_embedding WHERE embedding_type = 'full' LIMIT 1") ?? 0) > 0;

            if (hasContentEmbeddings)
            {
                _logger.LogDebug("Startup content embedding check: content embeddings exist, skipping");
                return;
            }

            var documentCount = _db.ReadScalar<long?>(
                "SELECT COUNT(*) FROM node WHERE kind = 'document'") ?? 0;

            if (documentCount == 0)
            {
                _logger.LogDebug("Startup content embedding check: no documents indexed yet, skipping");
                return;
            }

            _logger.LogInformation(
                "Startup content embedding check: {DocumentCount} documents indexed but no content embeddings. Triggering full refresh.",
                documentCount);

            await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var embeddingsChanged = await _refreshRunner.RefreshAsync(cancellationToken).ConfigureAwait(false);
                if (embeddingsChanged)
                    SyncRegistryEmbeddingStatus(documentIds: null);
            }
            finally
            {
                _refreshGate.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Startup content embedding refresh failed");
        }
    }

    private void MarkItemsAsNotApplicable(IReadOnlyList<IndexItem> items)
    {
        if (_uriRegistry is null)
            return;

        foreach (var item in items)
            _uriRegistry.SetEmbeddingNotApplicable(item.Uri);

        _logger.LogDebug("Marked {Count} items as embedding NotApplicable", items.Count);
    }
}
