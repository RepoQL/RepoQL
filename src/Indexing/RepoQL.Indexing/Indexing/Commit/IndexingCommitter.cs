using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RepoQL.Contracts;
using RepoQL.Contracts.Data;
using RepoQL.Contracts.Embeddings;
using RepoQL.Contracts.Models;
using RepoQL.Data.DuckDB;
using RepoQL.Indexing.Indexing.Pipelines;
using RepoQL.Indexing.Indexing.State;
using static RepoQL.Contracts.Embeddings.EmbeddingModeExtensions;

namespace RepoQL.Indexing.Indexing.Commit;

/// <summary>
/// Converts <see cref="IndexItem"/> to <see cref="ParsedArtifact"/> and persists to database.
/// Updates <see cref="IDocumentCatalog"/> after write succeeds.
/// </summary>
/// <remarks>
/// <para><strong>Batching</strong></para>
/// <para>
/// Items are queued and committed in batches for better performance.
/// Batches are flushed when:
/// </para>
/// <list type="bullet">
/// <item><description>Batch size reaches <see cref="MaxBatchSize"/> (default 32)</description></item>
/// <item><description>Timer fires after <see cref="FlushIntervalMs"/> (default 25ms)</description></item>
/// </list>
/// <para>Callers wait on a TaskCompletionSource until their item is committed.</para>
///
/// <para><strong>Validation</strong></para>
/// <para>
/// Before creating write operation, validates:
/// </para>
/// <list type="bullet">
/// <item><description><see cref="IndexItem.Records"/> is not null</description></item>
/// <item><description><see cref="IndexItem.DigestHex"/> is not null</description></item>
/// <item><description><see cref="IndexItem.MediaType"/> is not null</description></item>
/// <item><description>Records contain a document node</description></item>
/// </list>
/// <para>If validation fails, logs warning and returns early (no exception).</para>
///
/// <para><strong>Annotation Merging</strong></para>
/// <para>
/// Combines annotations from two sources:
/// </para>
/// <list type="number">
/// <item><description><see cref="IndexItem.Records"/>.Annotations (from parsers)</description></item>
/// <item><description><see cref="IndexItem.AnnotationsList"/> (from analyzers)</description></item>
/// </list>
/// </remarks>
public sealed class IndexingCommitter : IIndexingCommitter, IDisposable
{
    private const int MaxBatchSize = 64;
    private const int FlushIntervalMs = 100; // 100ms to balance batching vs latency

    private readonly DuckDbDataStore _db;
    private readonly IDocumentCatalog _catalog;
    private readonly ILogger<IndexingCommitter> _logger;
    private readonly UriRegistry? _uriRegistry;
    private readonly IEmbeddingProvider? _embeddingProvider;
    private readonly EmbeddingMode _embeddingMode;

    // Batching infrastructure
    private readonly object _batchLock = new();
    private readonly object _flushLock = new(); // Ensures only one flush at a time
    private readonly List<PendingCommit> _pendingItems = new();
    private readonly Timer _flushTimer;
    private volatile bool _disposed;

    private sealed class PendingCommit
    {
        public required IndexItem Item { get; init; }
        public required ParsedArtifact Artifact { get; init; }
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public IndexingCommitter(
        DuckDbDataStore db,
        IDocumentCatalog catalog,
        ILogger<IndexingCommitter>? logger = null,
        UriRegistry? uriRegistry = null,
        IEmbeddingProvider? embeddingProvider = null,
        EmbeddingMode embeddingMode = EmbeddingMode.Full)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _logger = logger ?? NullLogger<IndexingCommitter>.Instance;
        _uriRegistry = uriRegistry;
        _embeddingProvider = embeddingProvider;
        _embeddingMode = embeddingMode;
        _flushTimer = new Timer(OnFlushTimer, null, FlushIntervalMs, FlushIntervalMs);
    }

    private void OnFlushTimer(object? state)
    {
        if (_disposed)
            return;
        FlushPendingItems();
    }

    public async Task CommitAsync(IndexItem item, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (item.Records is null)
        {
            _logger.LogWarning("Skipping commit for {Uri} because no records were produced.", item.Uri);
            return;
        }

        if (string.IsNullOrEmpty(item.DigestHex))
        {
            _logger.LogWarning("Skipping commit for {Uri} because digest is unavailable.", item.Uri);
            return;
        }

        var mediaType = item.MediaType ?? item.RawArtifact.ProvisionalMediaType.Value;
        if (mediaType is null)
        {
            _logger.LogWarning("Skipping commit for {Uri} because media type could not be resolved.", item.Uri);
            return;
        }

        var documentNode = item.Records.Nodes.FirstOrDefault(n =>
            string.Equals(n.Kind, "document", StringComparison.OrdinalIgnoreCase));
        if (documentNode is null)
        {
            _logger.LogWarning("Skipping commit for {Uri} because records do not contain a document node.", item.Uri);
            return;
        }

        var commitRecords = CreateCommitRecords(item);

        // Create ParsedArtifact from Records
        var parsedArtifact = new ParsedArtifact
        {
            Artifact = commitRecords.Artifacts?.FirstOrDefault() ?? throw new InvalidOperationException("No artifact in records"),
            DocumentNode = documentNode,
            Children = commitRecords.Nodes?.Where(n => n.Kind != "document").ToArray() ?? [],
            Spans = commitRecords.Spans ?? [],
            Edges = commitRecords.Edges ?? [],
            Annotations = commitRecords.Annotations ?? [],
            AnnotationSources = commitRecords.AnnotationSources ?? []
        };

        // Queue for batch commit
        var pending = new PendingCommit { Item = item, Artifact = parsedArtifact };
        bool shouldFlush;

        lock (_batchLock)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(IndexingCommitter));

            _pendingItems.Add(pending);
            shouldFlush = _pendingItems.Count >= MaxBatchSize;
        }

        // Flush immediately if batch is full
        if (shouldFlush)
            FlushPendingItems();

        // Wait for commit to complete
        await pending.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Flush all pending items to the database in a single batch.
    /// Thread-safe - serializes flushes to prevent concurrent database writes.
    /// </summary>
    private void FlushPendingItems()
    {
        // Serialize all flushes - only one can run at a time
        // This prevents write-write conflicts in DuckDB
        lock (_flushLock)
        {
            List<PendingCommit> batch;

            lock (_batchLock)
            {
                if (_pendingItems.Count == 0)
                    return;

                batch = new List<PendingCommit>(_pendingItems);
                _pendingItems.Clear();
            }

            var sw = Stopwatch.StartNew();

            try
            {
                // Batch commit to database
                var dbItems = batch.Select(p => (p.Item.Uri, p.Artifact)).ToList();
                var structureEmbeddings = batch
                    .Select(p => p.Item.StructureEmbedding)
                    .Where(e => e is not null)
                    .Select(e => e!)
                    .ToList();

                var dbSw = Stopwatch.StartNew();
                _db.IndexArtifactBatch(dbItems);
                var dbMs = dbSw.Elapsed.TotalMilliseconds;

                var embedMs = 0.0;
                var embeddingWriteFailed = false;
                if (structureEmbeddings.Count > 0)
                {
                    dbSw.Restart();
                    try
                    {
                        _db.WriteEmbeddings(structureEmbeddings);
                    }
                    catch (Exception ex)
                    {
                        embeddingWriteFailed = true;
                        MarkEmbeddingWriteFailed(batch.Select(p => p.Item), ex);
                        _logger.LogWarning(ex,
                            "Structure embedding write failed for {EmbeddingCount} items in commit batch. Artifact commit and catalog update will continue.",
                            structureEmbeddings.Count);
                    }
                    embedMs = dbSw.Elapsed.TotalMilliseconds;
                }

                if (!embeddingWriteFailed)
                {
                    UpdateUriRegistryEmbeddingStatus(batch.Select(p => p.Item), structureEmbeddings);
                }

                dbSw.Restart();
                // Update catalog and complete all items
                foreach (var pending in batch)
                {
                    var item = pending.Item;
                    var mediaType = item.MediaType ?? item.RawArtifact.ProvisionalMediaType.Value;
                    var entry = new DocumentCatalogEntry(
                        item.Uri,
                        item.DigestHex!,
                        mediaType!,
                        item.RawArtifact.PhysicalPath,
                        item.LastModified);
                    _catalog.ApplyUpsert(entry);
                    pending.Completion.TrySetResult();
                }
                var catalogMs = dbSw.Elapsed.TotalMilliseconds;

                _logger.LogDebug(
                    "Committed batch of {Count} items in {ElapsedMs:F1}ms ({PerItem:F1}ms/item) " +
                    "[db={DbMs:F1}ms, embed={EmbedMs:F1}ms, catalog={CatalogMs:F1}ms]",
                    batch.Count, sw.Elapsed.TotalMilliseconds, sw.Elapsed.TotalMilliseconds / batch.Count,
                    dbMs, embedMs, catalogMs);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Batch commit failed for {Count} items", batch.Count);
                foreach (var pending in batch)
                {
                    pending.Completion.TrySetException(ex);
                }
            }
        }
    }

    public Task CommitBatchAsync(IReadOnlyList<IndexItem> items, CancellationToken cancellationToken)
    {
        if (items.Count == 0)
            return Task.CompletedTask;

        // Prepare batch items, filtering out invalid ones
        var batchItems = new List<(RepoUri Uri, ParsedArtifact Artifact, IndexItem Item)>();

        foreach (var item in items)
        {
            if (item.Records is null || string.IsNullOrEmpty(item.DigestHex))
                continue;

            var mediaType = item.MediaType ?? item.RawArtifact.ProvisionalMediaType.Value;
            if (mediaType is null)
                continue;

            var documentNode = item.Records.Nodes.FirstOrDefault(n =>
                string.Equals(n.Kind, "document", StringComparison.OrdinalIgnoreCase));
            if (documentNode is null)
                continue;

            var commitRecords = CreateCommitRecords(item);
            var parsedArtifact = new ParsedArtifact
            {
                Artifact = commitRecords.Artifacts?.FirstOrDefault() ?? throw new InvalidOperationException("No artifact in records"),
                DocumentNode = documentNode,
                Children = commitRecords.Nodes?.Where(n => n.Kind != "document").ToArray() ?? [],
                Spans = commitRecords.Spans ?? [],
                Edges = commitRecords.Edges ?? [],
                Annotations = commitRecords.Annotations ?? [],
                AnnotationSources = commitRecords.AnnotationSources ?? []
            };

            batchItems.Add((item.Uri, parsedArtifact, item));
        }

        if (batchItems.Count == 0)
            return Task.CompletedTask;

        // Serialize with FlushPendingItems to prevent concurrent database writes
        lock (_flushLock)
        {
            // Index as batch (bypasses the internal queue for explicit batch calls)
            var dbItems = batchItems.Select(b => (b.Uri, b.Artifact)).ToList();
            var structureEmbeddings = batchItems
                .Select(b => b.Item.StructureEmbedding)
                .Where(e => e is not null)
                .Select(e => e!)
                .ToList();

            _db.IndexArtifactBatch(dbItems);

            var embeddingWriteFailed = false;
            if (structureEmbeddings.Count > 0)
            {
                try
                {
                    _db.WriteEmbeddings(structureEmbeddings);
                }
                catch (Exception ex)
                {
                    embeddingWriteFailed = true;
                    MarkEmbeddingWriteFailed(batchItems.Select(b => b.Item), ex);
                    _logger.LogWarning(ex,
                        "Structure embedding write failed for {EmbeddingCount} items in explicit batch commit. Artifact commit and catalog update will continue.",
                        structureEmbeddings.Count);
                }
            }

            if (!embeddingWriteFailed)
            {
                UpdateUriRegistryEmbeddingStatus(batchItems.Select(b => b.Item), structureEmbeddings);
            }

            // Update catalog for all items
            foreach (var (_, _, item) in batchItems)
            {
                var mediaType = item.MediaType ?? item.RawArtifact.ProvisionalMediaType.Value;
                var entry = new DocumentCatalogEntry(
                    item.Uri,
                    item.DigestHex!,
                    mediaType!,
                    item.RawArtifact.PhysicalPath,
                    item.LastModified);
                _catalog.ApplyUpsert(entry);
            }
        }

        return Task.CompletedTask;
    }

    private void MarkEmbeddingWriteFailed(IEnumerable<IndexItem> items, Exception exception)
    {
        if (_uriRegistry is null)
            return;

        var error = $"structure embedding write failed: {exception.Message}";
        foreach (var item in items)
        {
            if (item.StructureEmbedding is not null)
            {
                _uriRegistry.SetEmbeddingFailed(item.Uri, error);
            }
        }
    }

    private void UpdateUriRegistryEmbeddingStatus(IEnumerable<IndexItem> items, IReadOnlyList<DocumentEmbedding> writtenEmbeddings)
    {
        if (_uriRegistry is null)
            return;

        foreach (var embedding in writtenEmbeddings)
        {
            if (RepoUri.TryParse(embedding.Uri, out var uri))
            {
                _uriRegistry.SetEmbedded(uri, 1);
            }
        }

        if (!StructureEmbeddingsAreNotApplicable)
            return;

        foreach (var item in items)
        {
            if (item.StructureEmbedding is null)
            {
                _uriRegistry.SetEmbeddingNotApplicable(item.Uri);
            }
        }
    }

    private bool StructureEmbeddingsAreNotApplicable =>
        !_embeddingMode.IncludesStructure() || _embeddingProvider is null || !_embeddingProvider.Enabled;

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _flushTimer.Dispose();

        // Flush any remaining items
        FlushPendingItems();
    }

    private static Records CreateCommitRecords(IndexItem item)
    {
        var existingAnnotations = item.Records!.Annotations ?? Array.Empty<Annotation>();
        var analyzerAnnotations = item.AnnotationsList.Count > 0
            ? item.AnnotationsList.ToArray()
            : Array.Empty<Annotation>();

        var combinedAnnotations = existingAnnotations.Length == 0
            ? analyzerAnnotations
            : analyzerAnnotations.Length == 0
                ? existingAnnotations
                : [.. existingAnnotations, .. analyzerAnnotations];

        return new Records
        {
            Artifacts = item.Records.Artifacts,
            Nodes = item.Records.Nodes,
            Spans = item.Records.Spans,
            Edges = item.Records.Edges,
            Annotations = combinedAnnotations,
            AnnotationSources = item.Records.AnnotationSources
        };
    }
}
