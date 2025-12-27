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
    private const int StructureEmbeddingBatchSize = 128;
    private static readonly int RefreshConcurrency = GetRefreshConcurrency();
    private readonly IVectorIndexRefresher _refresher;
    private readonly DuckDbDataStore? _db;
    private readonly IEmbeddingProvider? _embeddingProvider;
    private readonly EmbeddingMode _embeddingMode;
    private readonly ILogger<VectorIndexCoordinator> _logger;
    private readonly SemaphoreSlim _refreshGate = new(RefreshConcurrency, RefreshConcurrency);
    private long _lastRefreshedEpoch = long.MinValue;
    private volatile bool _needsRefresh;

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
        ILogger<VectorIndexCoordinator>? logger = null)
        : this(new DuckDbVectorIndexRefresher(database, embeddingProvider, embeddingMode), database, embeddingProvider, embeddingMode, logger)
    {
    }

    internal VectorIndexCoordinator(
        IVectorIndexRefresher refresher,
        DuckDbDataStore? db = null,
        IEmbeddingProvider? embeddingProvider = null,
        EmbeddingMode embeddingMode = EmbeddingMode.Full,
        ILogger<VectorIndexCoordinator>? logger = null)
    {
        _refresher = refresher ?? throw new ArgumentNullException(nameof(refresher));
        _db = db;
        _embeddingProvider = embeddingProvider;
        _embeddingMode = embeddingMode;
        _logger = logger ?? NullLogger<VectorIndexCoordinator>.Instance;
    }

    public Task ApplyDeletesAsync(IReadOnlyList<RepoUri> deletedArtifacts, CancellationToken cancellationToken)
    {
        if (deletedArtifacts.Count == 0)
            return Task.CompletedTask;

        _needsRefresh = true;
        return Task.CompletedTask;
    }

    public async Task ApplyAsync(IndexItem item, CancellationToken cancellationToken)
    {
        var epoch = item.Epoch;
        if (!_needsRefresh && Interlocked.Read(ref _lastRefreshedEpoch) == epoch)
            return;

        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_needsRefresh && Interlocked.Read(ref _lastRefreshedEpoch) == epoch)
                return;

            await RefreshEmbeddingsAsync(cancellationToken).ConfigureAwait(false);
            Interlocked.Exchange(ref _lastRefreshedEpoch, epoch);
            _needsRefresh = false;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private async Task RefreshEmbeddingsAsync(CancellationToken cancellationToken)
    {
        _logger.LogDebug("Vector index refresh triggered");
        try
        {
        await _refresher.RefreshAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogDebug("Vector index refresh completed");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Vector index refresh failed");
            throw;
        }
    }

    public async Task GenerateStructureEmbeddingsAsync(IReadOnlyList<IndexItem> items, CancellationToken cancellationToken)
    {
        if (items.Count == 0)
            return;

        // Check embedding mode - structure embeddings require at least StructureOnly mode
        if (!_embeddingMode.IncludesStructure())
        {
            _logger.LogDebug("Structure embedding skipped: mode={Mode}", _embeddingMode);
            return;
        }

        if (_db is null || _embeddingProvider is null || !_embeddingProvider.Enabled)
        {
            _logger.LogDebug("Structure embedding skipped: db={Db}, provider={Provider}, enabled={Enabled}",
                _db is not null, _embeddingProvider is not null, _embeddingProvider?.Enabled);
            return;
        }

        var timer = Stopwatch.StartNew();

        // Build structure payloads from items that have document nodes
        var workItems = new List<(Guid DocId, Guid NodeId, string Uri, string Payload)>(items.Count);
        var withRecords = 0;
        var withArtifacts = 0;
        var withDocNodes = 0;
        foreach (var item in items)
        {
            var records = item.Records;
            if (records is not null) withRecords++;

            var artifacts = records?.Artifacts;
            var artifact = artifacts is { Length: > 0 } ? artifacts[0] : null;
            if (artifact is not null) withArtifacts++;

            Node? docNode = null;
            var nodes = records?.Nodes;
            if (nodes is { Length: > 0 })
            {
                for (var i = 0; i < nodes.Length; i++)
                {
                    var n = nodes[i];
                    if (n.Kind == "document")
                    {
                        docNode = n;
                        break;
                    }
                }
            }
            if (docNode is not null) withDocNodes++;

            if (artifact is null || docNode is null)
                continue;

            var payload = BuildStructurePayload(item.Uri.ToString(), artifact.Headline, artifact.Structure);
            if (!string.IsNullOrWhiteSpace(payload))
            {
                workItems.Add((docNode.Id, docNode.Id, item.Uri.ToString(), payload));
            }
        }

        _logger.LogInformation("Structure embedding: {Total} items, {WithRecords} with records, {WithArtifacts} with artifacts, {WithDocNodes} with docNodes, {WorkItems} work items",
            items.Count, withRecords, withArtifacts, withDocNodes, workItems.Count);

        if (workItems.Count == 0)
        {
            _logger.LogDebug("No structure embeddings to generate");
            return;
        }

        _logger.LogInformation("Generating {Count} structure embeddings...", workItems.Count);

        // Generate embeddings in batches and write each batch immediately.
        var totalWritten = 0;
        var totalBatches = (workItems.Count + StructureEmbeddingBatchSize - 1) / StructureEmbeddingBatchSize;
        var batchNum = 0;

        using var structureActivity = IndexingEngine.ActivitySource.StartActivity("repoql.embedding.structure", ActivityKind.Internal);
        structureActivity?.SetTag("items", workItems.Count);
        structureActivity?.SetTag("batches", totalBatches);
        structureActivity?.SetTag("model", _embeddingProvider.Model);

        for (var offset = 0; offset < workItems.Count; offset += StructureEmbeddingBatchSize)
        {
            batchNum++;
            var batchCount = Math.Min(StructureEmbeddingBatchSize, workItems.Count - offset);
            var payloads = new string[batchCount];
            for (var i = 0; i < batchCount; i++)
                payloads[i] = workItems[offset + i].Payload;

            var itemsAfterBatch = offset + batchCount;
            var progress = new BatchEmbeddingProgress(batchNum, totalBatches, itemsAfterBatch, workItems.Count, timer.Elapsed);

            var batchTimer = Stopwatch.StartNew();
            float[]?[] vectors;
            using (var batchActivity = IndexingEngine.ActivitySource.StartActivity("repoql.embedding.structure.batch", ActivityKind.Internal))
            {
                batchActivity?.SetTag("batch", batchNum);
                batchActivity?.SetTag("total_batches", totalBatches);
                batchActivity?.SetTag("size", batchCount);

                vectors = await _embeddingProvider.EmbedBatchAsync(payloads, progress, cancellationToken).ConfigureAwait(false);
            }
            batchTimer.Stop();

            var percentComplete = (int)(itemsAfterBatch * 100.0 / workItems.Count);
            var eta = progress.EstimatedRemaining;
            var etaStr = eta.HasValue && eta.Value > TimeSpan.Zero
                ? $", ETA {eta.Value.Humanize(precision: 2, minUnit: Humanizer.Localisation.TimeUnit.Second)}"
                : "";
            _logger.LogInformation("Structure embeddings: {Batch}/{Total} ({Percent}%) - {BatchSize} items in {Time}{Eta}",
                batchNum, totalBatches, percentComplete, batchCount,
                batchTimer.Elapsed.Humanize(precision: 2, minUnit: Humanizer.Localisation.TimeUnit.Millisecond), etaStr);

            var documentEmbeddings = new List<DocumentEmbedding>(batchCount);
            for (var i = 0; i < batchCount; i++)
            {
                var vec = (vectors != null && i < vectors.Length) ? vectors[i] : null;
                if (vec is null || vec.Length == 0)
                    continue;

                var work = workItems[offset + i];
                documentEmbeddings.Add(new DocumentEmbedding(
                    work.DocId,
                    work.NodeId,
                    ChunkIndex: 0, // structure embeddings are always chunk 0
                    DocumentEmbedding.TypeStructure,
                    work.Uri,
                    DocumentEmbedding.ScopeDocument,
                    vec,
                    _embeddingProvider.Model,
                    _embeddingProvider.Dimension));
            }

            if (documentEmbeddings.Count > 0)
            {
                _db.WriteEmbeddings(documentEmbeddings);
                totalWritten += documentEmbeddings.Count;
            }
        }

        timer.Stop();
        _logger.LogInformation("Structure embeddings complete: {Count} embeddings in {Time}",
            totalWritten, timer.Elapsed.Humanize(precision: 2, minUnit: Humanizer.Localisation.TimeUnit.Millisecond));
    }

    private static string BuildStructurePayload(string uri, string? headline, string? structure)
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
}
