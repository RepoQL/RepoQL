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
    private static readonly int RefreshConcurrency = GetRefreshConcurrency();
    private readonly IVectorIndexRefresher _refresher;
    private readonly DuckDbDataStore? _db;
    private readonly IEmbeddingProvider? _embeddingProvider;
    private readonly EmbeddingMode _embeddingMode;
    private readonly ILogger<VectorIndexCoordinator> _logger;
    private readonly UriRegistry? _uriRegistry;
    private readonly SemaphoreSlim _refreshGate = new(RefreshConcurrency, RefreshConcurrency);
    private long _lastRefreshedEpoch = long.MinValue;
    private volatile bool _needsRefresh;
    private VssIndexManager? _vssIndexManager;

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
        UriRegistry? uriRegistry = null)
    {
        _refresher = refresher ?? throw new ArgumentNullException(nameof(refresher));
        _db = db;
        _embeddingProvider = embeddingProvider;
        _embeddingMode = embeddingMode;
        _logger = logger ?? NullLogger<VectorIndexCoordinator>.Instance;
        _uriRegistry = uriRegistry;
    }

    public Task ApplyDeletesAsync(IReadOnlyList<RepoUri> deletedArtifacts, CancellationToken cancellationToken)
    {
        if (deletedArtifacts.Count == 0)
            return Task.CompletedTask;

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

            await RefreshEmbeddingsAsync(targetDocumentIds, forceFullRefresh, cancellationToken).ConfigureAwait(false);
            Interlocked.Exchange(ref _lastRefreshedEpoch, epoch);
            _needsRefresh = false;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private async Task RefreshEmbeddingsAsync(
        IReadOnlyList<Guid> targetDocumentIds,
        bool forceFullRefresh,
        CancellationToken cancellationToken)
    {
        var runFullRefresh = forceFullRefresh || targetDocumentIds.Count == 0;
        _logger.LogDebug("Vector index refresh triggered (mode={Mode}, docs={DocCount})",
            runFullRefresh ? "full" : "targeted",
            targetDocumentIds.Count);

        try
        {
            if (runFullRefresh)
            {
                await _refresher.RefreshAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await _refresher.RefreshAsync(targetDocumentIds, cancellationToken).ConfigureAwait(false);
            }

            _logger.LogDebug("Vector index refresh completed");

            // Sync UriRegistry with actual embedding counts from the database
            SyncRegistryEmbeddingStatus(runFullRefresh ? null : targetDocumentIds);
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
            IReadOnlyList<(string? ContainerUri, int ChunkCount)> results;
            if (documentIds is { Count: > 0 })
            {
                var idList = ToUuidListSql(documentIds);
                var targetedQuery = $"""
                    SELECT
                        repository_uri_container(uri) as container_uri,
                        COUNT(*) as chunk_count
                    FROM document_embedding
                    WHERE doc_id IN ({idList})
                    GROUP BY repository_uri_container(uri)
                    """;

                results = _db.Read(targetedQuery, record =>
                {
                    var containerUri = record["container_uri"]?.ToString();
                    var chunkCount = Convert.ToInt32(record["chunk_count"]);
                    return (containerUri, chunkCount);
                });
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

                results = _db.Read(fullQuery, record =>
                {
                    var containerUri = record["container_uri"]?.ToString();
                    var chunkCount = Convert.ToInt32(record["chunk_count"]);
                    return (containerUri, chunkCount);
                });
            }

            foreach (var (containerUriStr, chunkCount) in results)
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

    public async Task RefreshVssIndexAsync(CancellationToken cancellationToken)
    {
        if (_db is null)
        {
            _logger.LogDebug("VSS index refresh skipped: no database");
            return;
        }

        // Lazily create the VSS index manager
        _vssIndexManager ??= new VssIndexManager(_db);

        try
        {
            await _vssIndexManager.RefreshIndexesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "VSS index refresh failed");
        }
    }

    public void Dispose()
    {
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
