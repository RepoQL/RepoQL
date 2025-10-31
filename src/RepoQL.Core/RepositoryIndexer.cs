using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RepoQL.Contracts;
using RepoQL.Contracts.Analysis;
using RepoQL.Contracts.Data;
using RepoQL.Contracts.Models;
using RepoQL.Core.Analysis;
using RepoQL.Metrics;
using RepoQL.FileSystem;
using RepoQL.FileSystem.Abstractions;
using IFileInfo = Microsoft.Extensions.FileProviders.IFileInfo;
using IFileSystemWatcher = RepoQL.FileSystem.Abstractions.IFileSystemWatcher;

namespace RepoQL.Core;

[DebuggerTypeProxy(typeof(RepositoryIndexerDebugView))]
public class RepositoryIndexer(
    IMultiFileSystem fileSystem,
    IGraphStore storage,
    IFileClassifier classifier,
    IFormatRegistry formatRegistry,
    IAnalysisWorkspace analysisWorkspace,
    IUriFilter uriFilter,
    IHasher hasher,
    IDatabaseWriter? dbWriter = null,
    IndexingMetrics? metrics = null,
    Meter? meter = null,
    IAnalysisResultWriter? analysisWriter = null,
    IAnalyzerSettingsProvider? settingsProvider = null,
    string? repositoryRoot = null,
    ILogger<RepositoryIndexer>? logger = null) : IRepositoryIndexer
{
    private ILogger<RepositoryIndexer> Logger { get; } = logger ?? NullLogger<RepositoryIndexer>.Instance;
    private static readonly ActivitySource Activity = new("RepoQL.Indexing");
    private readonly bool _ownsMetrics = metrics is null;
    private readonly bool _ownsMeter = meter is null;
    private readonly IndexingMetrics _metrics = metrics ?? new IndexingMetrics();
    private readonly Meter _meter = meter ?? new Meter("RepoQL.Indexing");

    private WorkQueue<DiscoveredArtifact>? _classificationQueue;
    private WorkQueue<DiscoveredArtifact>? _parsingQueue;
    private WorkQueue<string>? _enrichmentQueue;

    private long _classificationScheduled;
    private long _classificationCompleted;
    private long _parsingScheduled;
    private long _parsingCompleted;
    private long _enrichmentScheduled;
    private long _enrichmentCompleted;

    private int _activeReindexScopes;
    private readonly object _observerLock = new();
    private readonly List<IObserver<IndexerEvent>> _observers = [];
    private readonly CancellationTokenSource _stopping = new();
    private bool _isDisposed;
    private readonly IFileSystemWatcher _watcher = fileSystem.WatchAll();
    private readonly KeyedDebouncer<string> _classifyDebouncer = new(TimeSpan.FromMilliseconds(500));

    private readonly ConcurrentDictionary<string, byte> _inflightParses = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (string Digest, DateTimeOffset At)> _recentByUri = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _pendingDigestByUri = new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentQueue<DebugEvent> _recentEvents = new();
    private readonly ConcurrentQueue<Exception> _recentErrors = new();

    private readonly IGraphStore _storage = storage;
    private readonly SemaphoreSlim _storageGate = new(1, 1);
    private readonly IDatabaseWriter? _dbWriter = dbWriter;
    private readonly int _writerCapacity = dbWriter?.QueueCapacity ?? 0;

    private readonly ConcurrentDictionary<string, DocumentModel> _documentCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly IAnalysisResultWriter _analysisWriter = analysisWriter ?? NullAnalysisResultWriter.Instance;
    private readonly string _repositoryRoot = string.IsNullOrWhiteSpace(repositoryRoot) ? Directory.GetCurrentDirectory() : repositoryRoot;

    private const int RecentCapacity = 64;

    // Correlate pipeline steps per document (by container URI). We link spans across steps using ActivityLinks.
    private readonly ConcurrentDictionary<string, TraceChain> _traceChains = new(StringComparer.OrdinalIgnoreCase);

    private sealed class TraceChain
    {
        public ActivityContext? Hash { get; set; }
        public ActivityContext? Classify { get; set; }
        public ActivityContext? Parse { get; set; }
        public ActivityContext? DbWrite { get; set; }
        public ActivityContext? Enrich { get; set; }
        public Activity? RootActivity { get; set; }
    }

    private async Task<T> WithStorageAsync<T>(Func<IGraphStore, T> work, CancellationToken ct = default)
    {
        await _storageGate.WaitAsync(ct).ConfigureAwait(false);
        try { return work(_storage); }
        finally { _storageGate.Release(); }
    }

    private async Task WithStorageAsync(Action<IGraphStore> work, CancellationToken ct = default)
    {
        await _storageGate.WaitAsync(ct).ConfigureAwait(false);
        try { work(_storage); }
        finally { _storageGate.Release(); }
    }

    private T WithStorage<T>(Func<IGraphStore, T> work)
    {
        _storageGate.Wait();
        try { return work(_storage); }
        finally { _storageGate.Release(); }
    }

    private void WithStorage(Action<IGraphStore> work)
    {
        _storageGate.Wait();
        try { work(_storage); }
        finally { _storageGate.Release(); }
    }

    private async Task WriteDirectAsync(DiscoveredArtifact artifact, Records records)
    {
        await WithStorageAsync(store =>
        {
            var artifactIdMap = new Dictionary<Guid, Guid>();
            foreach (var a in records.Artifacts)
            {
                var saved = store.UpsertArtifact(a);
                artifactIdMap[a.Id] = saved.Id;
            }

            var docRec = records.Nodes.FirstOrDefault(n => string.Equals(n.Kind, "document", StringComparison.OrdinalIgnoreCase));
            if (docRec is null)
                throw new InvalidOperationException("Format materializer did not produce a document node.");

            var docArtifactId = docRec.ArtifactId is { } da && artifactIdMap.TryGetValue(da, out var newDa)
                ? newDa
                : docRec.ArtifactId;

            var docNode = new Node
            {
                Id = docRec.Id,
                Kind = "document",
                Uri = artifact.RepoUri,
                ArtifactId = docArtifactId,
                SpanId = null,
                Props = docRec.Props,
                CreatedAt = docRec.CreatedAt,
                UpdatedAt = DateTimeOffset.UtcNow
            };

            var savedDoc = store.UpsertDocumentByUri(artifact.RepoUri, docNode);
            var savedDocId = savedDoc.Id;

            var childNodes = new List<Node>();
            foreach (var n in records.Nodes.Where(n => !string.Equals(n.Kind, "document", StringComparison.OrdinalIgnoreCase)))
            {
                var node = n;
                if (n.ArtifactId is { } aid && artifactIdMap.TryGetValue(aid, out var newAid))
                {
                    node = new Node
                    {
                        Id = n.Id,
                        Kind = n.Kind,
                        Uri = n.Uri,
                        ArtifactId = newAid,
                        SpanId = n.SpanId,
                        Props = n.Props,
                        CreatedAt = n.CreatedAt,
                        UpdatedAt = n.UpdatedAt
                    };
                }
                childNodes.Add(node);
            }

            var spans = records.Spans.Select(s => new Span
            {
                Id = s.Id,
                DocumentId = savedDocId,
                StartByte = s.StartByte,
                EndByte = s.EndByte,
                StartLine = s.StartLine,
                StartColumn = s.StartColumn,
                EndLine = s.EndLine,
                EndColumn = s.EndColumn
            }).ToArray();

            var edges = records.Edges.Select(e => new Edge
            {
                Id = e.Id,
                SrcId = e.SrcId == docRec.Id ? savedDocId : e.SrcId,
                DstId = e.DstId == docRec.Id ? savedDocId : e.DstId,
                Type = e.Type,
                IsComposition = e.IsComposition,
                Ordinal = e.Ordinal,
                ScopeDocumentId = savedDocId,
                EdgeKey = e.EdgeKey,
                SrcSpanId = e.SrcSpanId,
                DstSpanId = e.DstSpanId,
                Props = e.Props,
                CreatedAt = e.CreatedAt
            }).ToArray();

            store.ReplaceDocumentContent(savedDocId, childNodes, spans, edges);
        }, _stopping.Token).ConfigureAwait(false);
    }

    private static string FileNameFromUri(RepoUri uri)
    {
        try
        {
            var container = uri.Container.AbsoluteUri;
            var lastSlash = container.LastIndexOf('/') + 1;
            if (lastSlash <= 0 || lastSlash >= container.Length)
                return container; // fallback
            return container[lastSlash..];
        }
        catch { return uri.AbsoluteUri; }
    }

    private static string? FileExtFromUri(RepoUri uri)
    {
        try
        {
            var name = FileNameFromUri(uri);
            var dot = name.LastIndexOf('.');
            return dot > 0 && dot < name.Length - 1 ? name[(dot + 1)..] : null;
        }
        catch { return null; }
    }

    private void StopRootIfPresent(string corrKey)
    {
        if (_traceChains.TryRemove(corrKey, out var chain))
        {
            try { chain.RootActivity?.Dispose(); } catch { }
        }
    }

    private async Task<bool> HasPersistedSnapshotAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await WithStorageAsync(store =>
            {
                foreach (var _ in store.RawQuery("SELECT 1 FROM node WHERE kind = 'document' LIMIT 1"))
                    return true;
                return false;
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ReportError(ex);
            return false;
        }
    }

    private async Task PruneMissingDocumentsAsync(ISet<string> liveContainers, CancellationToken cancellationToken)
    {
        List<RepoUri> staleDocuments;

        try
        {
            staleDocuments = await WithStorageAsync(store =>
            {
                var missing = new List<RepoUri>();
                foreach (var row in store.RawQuery("SELECT uri FROM node WHERE kind = 'document'"))
                {
                    if (!row.TryGetValue("uri", out var value) || value is not string uriText || string.IsNullOrWhiteSpace(uriText))
                        continue;

                    if (liveContainers.Contains(uriText))
                        continue;

                    if (!RepoUri.TryParse(uriText, out var parsed) || parsed is null)
                        continue;

                    if (!string.Equals(parsed.Scheme, "file", StringComparison.OrdinalIgnoreCase))
                        continue;

                    missing.Add(parsed);
                }

                return missing;
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ReportError(ex);
            return;
        }

        foreach (var uri in staleDocuments)
        {
            try
            {
                await WithStorageAsync(s => s.DeleteDocumentByUri(uri), _stopping.Token).ConfigureAwait(false);
                var placeholder = CreatePlaceholderFileInfo(uri);
                RaiseEvent(new IRepositoryIndexer.ItemDeletedEvent(placeholder, uri));
                var key = uri.AbsoluteUri.ToLowerInvariant();
                StopRootIfPresent(key);
                _recentByUri.TryRemove(key, out _);
            }
            catch (Exception ex)
            {
                ReportError(ex);
            }
        }
    }

    private static IFileInfo CreatePlaceholderFileInfo(RepoUri uri)
    {
        string? name = null;
        if (uri.IsFile)
        {
            name = Path.GetFileName(uri.LocalPath);
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            var segments = uri.Segments;
            if (segments.Length > 0)
                name = segments[^1].Trim('/');
        }

        if (string.IsNullOrWhiteSpace(name))
            name = uri.AbsoluteUri;

        return new Microsoft.Extensions.FileProviders.NotFoundFileInfo(name);
    }

    private sealed class NullServiceProvider : IServiceProvider
    {
        public static readonly NullServiceProvider Instance = new();
        private NullServiceProvider() { }
        public object? GetService(Type serviceType) => null;
    }

    // Equality: de-dup by RepoUri.AbsoluteUri (case-insensitive)
    private sealed class ArtifactUriComparer : IEqualityComparer<DiscoveredArtifact>
    {
        public bool Equals(DiscoveredArtifact? x, DiscoveredArtifact? y)
            => string.Equals(x?.RepoUri.AbsoluteUri, y?.RepoUri.AbsoluteUri, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode(DiscoveredArtifact obj)
            => StringComparer.OrdinalIgnoreCase.GetHashCode(obj.RepoUri.AbsoluteUri);
    }

    private async Task<bool> TryEnqueueClassificationAsync(DiscoveredArtifact artifact)
    {
        if (_classificationQueue is null)
            throw new InvalidOperationException("The indexer has not been started.");
        try
        {
            var enqueued = await _classificationQueue.EnqueueAsync(artifact, _stopping.Token).ConfigureAwait(false);
            if (enqueued) Interlocked.Increment(ref _classificationScheduled);
            return enqueued;
        }
        catch (OperationCanceledException) { return false; }
    }

    private async Task<bool> TryEnqueueParsingAsync(DiscoveredArtifact artifact)
    {
        if (_parsingQueue is null)
            throw new InvalidOperationException("The indexer has not been started.");
        try
        {
            var enqueued = await _parsingQueue.EnqueueAsync(artifact, _stopping.Token).ConfigureAwait(false);
            if (enqueued) Interlocked.Increment(ref _parsingScheduled);
            return enqueued;
        }
        catch (OperationCanceledException) { return false; }
    }

    private async Task<bool> TryEnqueueEnrichmentAsync(string containerUri)
    {
        if (_enrichmentQueue is null) return false;
        try
        {
            var enqueued = await _enrichmentQueue.EnqueueAsync(containerUri, _stopping.Token).ConfigureAwait(false);
            if (enqueued) Interlocked.Increment(ref _enrichmentScheduled);
            return enqueued;
        }
        catch (OperationCanceledException) { return false; }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await WithStorageAsync(store => store.EnsureSchema(), cancellationToken).ConfigureAwait(false);
        var hasPersistedSnapshot = await HasPersistedSnapshotAsync(cancellationToken).ConfigureAwait(false);

        // Right-size concurrency
        var cpu = Environment.ProcessorCount;
        var clsReaders = Math.Min(cpu * 2, 16);
        var prsReaders = Math.Min(cpu, 8);

        _classificationQueue = new("classification", 20000, clsReaders, ClassifyFileAsync, cancellationToken, _meter, new ArtifactUriComparer());
        _parsingQueue       = new("parsing",        20000, prsReaders, ParseAndStoreFileAsync, cancellationToken, _meter, new ArtifactUriComparer());

        // Defer or keep small; can be scaled higher post-initial sweep if needed
        var enrichReaders = Math.Max(cpu / 2, 1);
        _enrichmentQueue = new("enrichment", 4000, enrichReaders, EnrichDocumentAsync, cancellationToken, _meter, StringComparer.OrdinalIgnoreCase);

        // Initial enumerate: backpressure by awaiting enqueue. Run in reindex fast-path.
        if (!hasPersistedSnapshot)
        {
            using (EnterReindexScope())
            {
                await foreach (var entry in fileSystem.EnumerateAsync(cancellationToken))
                {
                    var artifact = new DiscoveredArtifact { File = entry.File, RepoUri = entry.Uri };
                    if (!uriFilter.IncludeFile(artifact.RepoUri))
                        continue;

                    _metrics.IncrementDiscover();
                    RaiseEvent(new IRepositoryIndexer.ItemDiscoveredEvent(entry.File, artifact.RepoUri));
                    await TryEnqueueClassificationAsync(artifact).ConfigureAwait(false);
                }
            }
        }
        else
        {
            var liveContainers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            await foreach (var entry in fileSystem.EnumerateAsync(cancellationToken))
            {
                var artifact = new DiscoveredArtifact { File = entry.File, RepoUri = entry.Uri };
                if (!uriFilter.IncludeFile(artifact.RepoUri))
                    continue;

                _metrics.IncrementDiscover();
                RaiseEvent(new IRepositoryIndexer.ItemDiscoveredEvent(entry.File, artifact.RepoUri));
                liveContainers.Add(artifact.RepoUri.AbsoluteUri);
                await ScheduleIfChangedAsync(entry.File, entry.Uri, ct: cancellationToken).ConfigureAwait(false);
            }

            await PruneMissingDocumentsAsync(liveContainers, cancellationToken).ConfigureAwait(false);
        }

        _watcher.Subscribe(new FileSystemChangeObserver(async change =>
        {
            if (!uriFilter.IncludeFile(change.CurrentUri))
                return;

            var changedKey = change.CurrentUri.AbsoluteUri.ToLowerInvariant();
            _classifyDebouncer.Push(changedKey, async () =>
            {
                _metrics.RecordFsEvent(change.Kind.ToString());

                switch (change.Kind)
                {
                    case ResourceEvent.Created:
                        RaiseEvent(new IRepositoryIndexer.ItemDiscoveredEvent(change.File, change.CurrentUri));
                        await TryEnqueueClassificationAsync(new DiscoveredArtifact { File = change.File, RepoUri = change.CurrentUri }).ConfigureAwait(false);
                        break;

                    case ResourceEvent.Updated:
                        RaiseEvent(new IRepositoryIndexer.ItemUpdatedEvent(change.File, change.CurrentUri));
                        await TryEnqueueClassificationAsync(new DiscoveredArtifact { File = change.File, RepoUri = change.CurrentUri }).ConfigureAwait(false);
                        break;

                    case ResourceEvent.Deleted:
                        RaiseEvent(new IRepositoryIndexer.ItemDeletedEvent(change.File, change.CurrentUri));
                        // Requires IGraphStore.DeleteDocumentByUri. If missing, implement or emulate via ReplaceDocumentContent with empty children.
                        await WithStorageAsync(s => s.DeleteDocumentByUri(change.CurrentUri), _stopping.Token).ConfigureAwait(false);
                        StopRootIfPresent(changedKey);
                        break;

                    case ResourceEvent.Moved:
                        RaiseEvent(new IRepositoryIndexer.ItemMovedEvent(change.File, change.CurrentUri, change.PreviousUri!));
                        // Preferred: atomic move in store. If unavailable, delete old then index new.
                        await WithStorageAsync(s => s.MoveDocumentUri(change.PreviousUri!, change.CurrentUri), _stopping.Token).ConfigureAwait(false);
                        await TryEnqueueClassificationAsync(new DiscoveredArtifact { File = change.File, RepoUri = change.CurrentUri }).ConfigureAwait(false);
                        break;

                    default:
                        throw new ArgumentOutOfRangeException(nameof(change));
                }
            });
        }, ReportError));
        await _watcher.StartAsync(cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_isDisposed) return;

        await _stopping.CancelAsync();

        if (_classificationQueue is not null) await _classificationQueue.DisposeAsync();
        if (_parsingQueue is not null)       await _parsingQueue.DisposeAsync();
        if (_enrichmentQueue is not null)    await _enrichmentQueue.DisposeAsync();

        await _watcher.StopAsync(CancellationToken.None);
        await _watcher.DisposeAsync();

        List<IObserver<IndexerEvent>> snapshot;
        lock (_observerLock)
        {
            snapshot = _observers.ToList();
            _observers.Clear();
        }
        foreach (var o in snapshot)
        {
            try { o.OnCompleted(); } catch { }
        }
    }

    public IDisposable Subscribe(IObserver<IndexerEvent> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        lock (_observerLock) { _observers.Add(observer); }
        return new Unsubscriber(_observers, observer);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None);
        _isDisposed = true;
        _storageGate.Dispose();
        if (_ownsMetrics) _metrics.Dispose();
        if (_ownsMeter)   _meter.Dispose();
        GC.SuppressFinalize(this);
    }

    public async Task QueueForIndexingAsync(IEnumerable<IFileInfo> files, bool skipUnchanged = true)
    {
        if (_classificationQueue is null)
            throw new InvalidOperationException("The indexer has not been started.");

        foreach (var file in files)
        {
            if (string.IsNullOrEmpty(file.PhysicalPath))
                throw new NotSupportedException("QueueForIndexingAsync(IFileInfo) cannot infer RepoUri. Use QueueForIndexingAsync(RepoUri).");

            var absPath = Path.GetFullPath(file.PhysicalPath!);
            var uri = new Uri(absPath);                 // file:///... on all OSes
            var repoUri = RepoUri.Parse(uri.AbsoluteUri);

            await ScheduleIfChangedAsync(file, repoUri, !skipUnchanged, _stopping.Token).ConfigureAwait(false);
        }
    }

    public async Task QueueForIndexingAsync(IEnumerable<RepoUri> uris, bool skipUnchanged = true)
    {
        if (_classificationQueue is null)
            throw new InvalidOperationException("The indexer has not been started.");

        foreach (var uri in uris)
        {
            var file = fileSystem.GetFile(uri);
            await ScheduleIfChangedAsync(file, uri, !skipUnchanged, _stopping.Token).ConfigureAwait(false);
        }
    }

    public async Task WaitForIdle(CancellationToken cancellationToken = default)
    {
        if (_classificationQueue is null || _parsingQueue is null) return;

        var readiness = new List<Task> { _classificationQueue.WorkersReadyAsync(), _parsingQueue.WorkersReadyAsync() };
        if (_enrichmentQueue is not null) readiness.Add(_enrichmentQueue.WorkersReadyAsync());
        try { await Task.WhenAll(readiness).ConfigureAwait(false); } catch { }

        while (true)
        {
            var waits = new List<Task>();
            if (_classificationQueue.Depth > 0) waits.Add(_classificationQueue.WhenIdleAsync());
            if (_parsingQueue.Depth        > 0) waits.Add(_parsingQueue.WhenIdleAsync());
            if (_enrichmentQueue is not null && _enrichmentQueue.Depth > 0) waits.Add(_enrichmentQueue.WhenIdleAsync());

            if (waits.Count > 0) await Task.WhenAll(waits).ConfigureAwait(false);

            if (_dbWriter is not null)
            {
                try { await _dbWriter.FlushAsync(cancellationToken).ConfigureAwait(false); }
                catch (Exception ex) { ReportError(ex); }
            }

            var classificationPending = _classificationQueue.Depth > 0;
            var parsingPending        = _parsingQueue.Depth > 0;
            var enrichmentPending     = _enrichmentQueue is not null && _enrichmentQueue.Depth > 0;
            if (!classificationPending && !parsingPending && !enrichmentPending) break;

            await Task.Yield();
        }
    }

    public async Task WaitForStagesIdleAsync(PipelineStage stages, CancellationToken cancellationToken = default)
    {
        if ((stages & PipelineStage.Discovery) != 0 && _classificationQueue is not null)
        {
            var t = _classificationQueue.WhenIdleAsync();
            await t.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        if ((stages & PipelineStage.Parsing) != 0 && _parsingQueue is not null)
        {
            var t = _parsingQueue.WhenIdleAsync();
            await t.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        if ((stages & PipelineStage.Analysis) != 0 && _enrichmentQueue is not null)
        {
            var t = _enrichmentQueue.WhenIdleAsync();
            await t.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        if ((stages & PipelineStage.Writer) != 0)
            await WaitForWriterIdle(cancellationToken).ConfigureAwait(false);
    }

    public async Task WaitForAnyStageIdleAsync(PipelineStage stages, CancellationToken cancellationToken = default)
    {
        var waits = new List<Task>();
        if ((stages & PipelineStage.Discovery) != 0 && _classificationQueue is not null) waits.Add(_classificationQueue.WhenIdleAsync());
        if ((stages & PipelineStage.Parsing)   != 0 && _parsingQueue is not null)       waits.Add(_parsingQueue.WhenIdleAsync());
        if ((stages & PipelineStage.Analysis)  != 0 && _enrichmentQueue is not null)    waits.Add(_enrichmentQueue.WhenIdleAsync());
        if ((stages & PipelineStage.Writer)    != 0)                                     waits.Add(WaitForWriterIdle(cancellationToken));

        if (waits.Count == 0) return;

        var awaited = waits.Select(t => t.WaitAsync(cancellationToken)).ToArray();
        await Task.WhenAny(awaited).ConfigureAwait(false);
    }

    public PipelineSnapshot GetPipelineSnapshot()
    {
        var captured = DateTimeOffset.UtcNow;
        var discovery = new PipelineStageSnapshot(
            PipelineStage.Discovery,
            _classificationQueue?.Depth ?? 0,
            _classificationQueue?.MaxDepth ?? 0,
            Volatile.Read(ref _classificationScheduled),
            Volatile.Read(ref _classificationCompleted));
        var parsing = new PipelineStageSnapshot(
            PipelineStage.Parsing,
            _parsingQueue?.Depth ?? 0,
            _parsingQueue?.MaxDepth ?? 0,
            Volatile.Read(ref _parsingScheduled),
            Volatile.Read(ref _parsingCompleted));
        var analysis = new PipelineStageSnapshot(
            PipelineStage.Analysis,
            _enrichmentQueue?.Depth ?? 0,
            _enrichmentQueue?.MaxDepth ?? 0,
            Volatile.Read(ref _enrichmentScheduled),
            Volatile.Read(ref _enrichmentCompleted));
        PipelineStageSnapshot? writerSnapshot = null;
        if (_dbWriter is not null)
        {
            try
            {
                var status = _dbWriter.GetStatus();
                writerSnapshot = new PipelineStageSnapshot(
                    PipelineStage.Writer,
                    status.PendingCount,
                    _writerCapacity,
                    status.PendingCount + status.TotalProcessed,
                    status.TotalProcessed);
            }
            catch (Exception ex) { ReportError(ex); }
        }

        return new PipelineSnapshot(captured, discovery, parsing, analysis, writerSnapshot, IsReindexing);
    }

    public bool IsReindexing => Volatile.Read(ref _activeReindexScopes) > 0;

    public IDisposable EnterReindexScope()
    {
        Interlocked.Increment(ref _activeReindexScopes);
        return new ReindexScope(this);
    }

    private void LeaveReindexScope() => Interlocked.Decrement(ref _activeReindexScopes);

    public int ClassificationQueueDepth => _classificationQueue?.Depth ?? 0;
    public int ParsingQueueDepth => _parsingQueue?.Depth ?? 0;
    public int EnrichmentQueueDepth => _enrichmentQueue?.Depth ?? 0;

    public async Task WaitForWriterIdle(CancellationToken cancellationToken = default)
    {
        if (_dbWriter is not null)
        {
            try { await _dbWriter.FlushAsync(cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) { ReportError(ex); }
        }
    }

    private async Task ParseAndStoreFileAsync(DiscoveredArtifact file)
    {
        var uriKey = file.RepoUri.AbsoluteUri;
        if (!_inflightParses.TryAdd(uriKey, 0))
            return;

        try
        {
            var corrKey = file.RepoUri.AbsoluteUri.ToLowerInvariant();
            ActivityContext parent = default;
            if (_traceChains.TryGetValue(corrKey, out var chain) && (chain.Classify is { } || chain.Hash is { }))
                parent = chain.Classify ?? chain.Hash ?? default;

            using var parseActivity = Activity.StartActivity(
                "repoql.parse",
                ActivityKind.Internal,
                parent,
                tags:
                [
                    new KeyValuePair<string, object?>("url.full", file.RepoUri.AbsoluteUri),
                    new KeyValuePair<string, object?>("repoql.uri", file.RepoUri.AbsoluteUri)
                ],
                links: null);

            if (parseActivity is not null)
            {
                try { parseActivity.SetTag("file.size", file.File.Length); } catch { }
                try { _traceChains.AddOrUpdate(corrKey, _ => new TraceChain { Parse = parseActivity.Context }, (_, c) => { c.Parse = parseActivity.Context; return c; }); } catch { }
            }

            var descriptor = await ResolveDescriptorAsync(file, parseActivity).ConfigureAwait(false);
            var document = await descriptor.Loader.LoadAsync(file, _stopping.Token).ConfigureAwait(false);
            parseActivity?.SetTag("content.type", document.MediaType.ToString());

            var sw = Stopwatch.StartNew();

            if (descriptor.Materializer is null)
                throw new InvalidOperationException($"Format '{document.MediaType}' does not provide a materializer.");

            var records = descriptor.Materializer.Materialize(document);
            _metrics.IncrementParse();
            try
            {
                _metrics.NodesExtracted.Add(records.Nodes.Length);
                _metrics.NodesPerDocument.Record(records.Nodes.Length);
                parseActivity?.SetTag("repoql.nodes.count", records.Nodes.Length);
                parseActivity?.SetTag("repoql.spans.count", records.Spans.Length);
                parseActivity?.SetTag("repoql.edges.count", records.Edges.Length);
            }
            catch (Exception ex) { ReportError(ex); }

            if (_dbWriter is not null)
            {
                var operationUri = file.RepoUri;
                var op = new WriteOperation
                {
                    Id = Guid.NewGuid(),
                    Type = WriteOperationType.ReplaceDocument,
                    Uri = operationUri,
                    ParsedData = records,
                    ParentContext = parseActivity?.Context,
                    OnCommitted = (_, result) =>
                    {
                        if (!result.Success) return Task.CompletedTask;
                        try { _traceChains.AddOrUpdate(corrKey, _ => new TraceChain { Parse = parseActivity!.Context }, (_, c) => { c.Parse = parseActivity!.Context; return c; }); } catch { }
                        _documentCache[operationUri.AbsoluteUri.ToLowerInvariant()] = document;
                        ScheduleEnrichment(operationUri);
                        if (_enrichmentQueue is null) { try { StopRootIfPresent(corrKey); } catch { } }
                        return Task.CompletedTask;
                    }
                };
                await _dbWriter.EnqueueAsync(op, _stopping.Token).ConfigureAwait(false);
            }
            else
            {
                await WriteDirectAsync(file, records).ConfigureAwait(false);
                _documentCache[file.RepoUri.AbsoluteUri.ToLowerInvariant()] = document;
                ScheduleEnrichment(file.RepoUri);
                if (_enrichmentQueue is null) { try { StopRootIfPresent(corrKey); } catch { } }
            }

            _metrics.IncrementIndex();
            try
            {
                var bytes = file.File.Length;
                _metrics.RecordFileProcessed(document.MediaType.ToString(), "indexed", bytes, sw.Elapsed.TotalMilliseconds);
            }
            catch (Exception ex) { ReportError(ex); }

            RaiseEvent(new IRepositoryIndexer.ItemIndexedEvent(file.File, file.RepoUri, document.MediaType));
            return;
        }
        catch (Exception ex) { ReportError(ex); }
        finally
        {
            _inflightParses.TryRemove(uriKey, out _);
            var key = file.RepoUri.AbsoluteUri.ToLowerInvariant();
            _pendingDigestByUri.TryRemove(key, out _);
            if (!_stopping.IsCancellationRequested)
                Interlocked.Increment(ref _parsingCompleted);
        }
    }

    private async Task ClassifyFileAsync(DiscoveredArtifact item)
    {
        try
        {
            var corrKey = item.RepoUri.AbsoluteUri.ToLowerInvariant();

            // Deleted/missing: treat as removal, do not hash or parse
            if (!item.File.Exists)
            {
                RaiseEvent(new IRepositoryIndexer.ItemDeletedEvent(item.File, item.RepoUri));
                await WithStorageAsync(s => s.DeleteDocumentByUri(item.RepoUri), _stopping.Token).ConfigureAwait(false);
                StopRootIfPresent(corrKey);
                return;
            }

            // Reindex fast-path: skip hash + DB checks; classify and parse
            if (IsReindexing)
            {
                var fastType = classifier.GetMediaType(item.File);
                RaiseEvent(new IRepositoryIndexer.ItemClassifiedEvent(item.File, item.RepoUri, fastType));
                item.MediaType = fastType;
                await TryEnqueueParsingAsync(item).ConfigureAwait(false);
                return;
            }

            ActivityContext parent = default;
            // Hash if not already present (e.g., watcher paths)
            if (item.Hash is null)
            {
                using var hashAct = Activity.StartActivity("repoql.hash", ActivityKind.Internal, parent,
                    tags:
                    [
                        new KeyValuePair<string, object?>("url.full", item.RepoUri.AbsoluteUri),
                        new KeyValuePair<string, object?>("repoql.uri", item.RepoUri.AbsoluteUri)
                    ]);
                item.Hash = await hasher.HashAsync(item.File, _stopping.Token);
                _metrics.IncrementHash();
                hashAct?.SetTag("file.size", item.File.Length);
                hashAct?.SetTag("file.hash", "xxh64:" + Convert.ToHexString(item.Hash).ToLowerInvariant());
                try { _traceChains.AddOrUpdate(corrKey, _ => new TraceChain { Hash = hashAct!.Context }, (_, c) => { c.Hash = hashAct!.Context; return c; }); } catch { }
            }

            // Classify
            var type = classifier.GetMediaType(item.File);
            using (var classify = Activity.StartActivity("repoql.classify", ActivityKind.Internal, parent,
                       tags: new TagList
                       {
                           new KeyValuePair<string, object?>("url.full", item.RepoUri.AbsoluteUri),
                           new KeyValuePair<string, object?>("repoql.uri", item.RepoUri.AbsoluteUri),
                           new KeyValuePair<string, object?>("content.type", type.ToString())
                       }))
            {
                if (classify is not null)
                {
                    try { _traceChains.AddOrUpdate(corrKey, _ => new TraceChain { Classify = classify.Context }, (_, c) => { c.Classify = classify.Context; return c; }); } catch { }
                }
            }

            RaiseEvent(new IRepositoryIndexer.ItemClassifiedEvent(item.File, item.RepoUri, type));
            item.MediaType = type;

            // Recent dedup guard (short window)
            var digest = "xxh64:" + Convert.ToHexString(item.Hash).ToLowerInvariant();
            var uriKey = item.RepoUri.AbsoluteUri.ToLowerInvariant();
            if (_recentByUri.TryGetValue(uriKey, out var seen)
                && string.Equals(seen.Digest, digest, StringComparison.Ordinal)
                && (DateTimeOffset.UtcNow - seen.At) < TimeSpan.FromSeconds(5))
            {
                try { _metrics.RecordFileProcessed(type.ToString(), "skipped_recent", item.File.Length, 0); } catch { }
                StopRootIfPresent(corrKey);
                return;
            }

            // DB short-circuit
            try
            {
                var existingArtifact = await WithStorageAsync(store =>
                {
                    var doc = store.GetDocumentByUri(item.RepoUri);
                    return doc?.ArtifactId is Guid aid ? store.GetArtifact(aid) : null;
                }, _stopping.Token).ConfigureAwait(false);

                if (existingArtifact is not null && string.Equals(existingArtifact.Digest, digest, StringComparison.Ordinal))
                {
                    try { _metrics.RecordFileProcessed(type.ToString(), "skipped", item.File.Length, 0); } catch { }
                    StopRootIfPresent(corrKey);
                    return;
                }
            }
            catch (Exception ex) { ReportError(ex); }

            _recentByUri[uriKey] = (digest, DateTimeOffset.UtcNow);
            await TryEnqueueParsingAsync(item).ConfigureAwait(false);
        }
        catch (Exception ex) { ReportError(ex); }
        finally
        {
            if (!_stopping.IsCancellationRequested)
                Interlocked.Increment(ref _classificationCompleted);
        }
    }

    private async Task<FormatDescriptor> ResolveDescriptorAsync(DiscoveredArtifact artifact, Activity? activity)
    {
        // Use the classified media type if available to avoid N× probing.
        if (artifact.MediaType is not null && formatRegistry.TryResolveByMedia(artifact.MediaType, out var byMedia))
        {
            activity?.SetTag("repoql.loader", byMedia.Loader.GetType().Name);
            return byMedia;
        }

        foreach (var candidate in formatRegistry.Formats)
        {
            if (!await candidate.Loader.CanLoadAsync(artifact, _stopping.Token).ConfigureAwait(false))
                continue;
            activity?.SetTag("repoql.loader", candidate.Loader.GetType().Name);
            return candidate;
        }

        throw new InvalidOperationException($"No format loader accepted artifact '{artifact.File.Name}'.");
    }

    private void ScheduleEnrichment(RepoUri uri)
    {
        if (_enrichmentQueue is null || _stopping.IsCancellationRequested) return;

        var value = uri.AbsoluteUri;
        _ = Task.Run(async () =>
        {
            try { await TryEnqueueEnrichmentAsync(value).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { ReportError(ex); }
        });
    }

    private async Task EnrichDocumentAsync(string containerUri)
    {
        try
        {
            var sw = Stopwatch.StartNew();
            var repoUri = RepoUri.Parse(containerUri);
            var corrKey = repoUri.AbsoluteUri.ToLowerInvariant();
            ActivityContext parent = default;
            if (_traceChains.TryGetValue(corrKey, out var chain))
                parent = chain.Parse ?? chain.Classify ?? chain.Hash ?? default;

            using var enrich = Activity.StartActivity(
                "repoql.enrich",
                ActivityKind.Internal,
                parent,
                tags:
                [
                    new KeyValuePair<string, object?>("url.full", repoUri.AbsoluteUri),
                    new KeyValuePair<string, object?>("repoql.uri", repoUri.AbsoluteUri)
                ],
                links: null);

            var documentNode = await WithStorageAsync(store => store.GetDocumentByUri(repoUri), _stopping.Token).ConfigureAwait(false);
            if (documentNode is null) return;

            var cacheKey = repoUri.AbsoluteUri.ToLowerInvariant();
            if (!_documentCache.TryRemove(cacheKey, out var document) || document is null)
            {
                document = await analysisWorkspace.LoadAsync(repoUri, _stopping.Token).ConfigureAwait(false);
            }
            if (document is null) return;

            if (!formatRegistry.TryResolveByMedia(document.MediaType, out var descriptor)) return;

            var settings = settingsProvider?.Resolve(containerUri, document.MediaType, documentNode) ?? new AnalyzerSettings();
            var context = new AnalyzerContext(settings, _repositoryRoot, formatRegistry, analysisWorkspace);

            var results = new List<AnalysisResult>();
            await foreach (var result in descriptor.Analyzer.AnalyzeAsync(document, context, _stopping.Token).ConfigureAwait(false))
                results.Add(result);

            var sources = new HashSet<string>(StringComparer.Ordinal);
            foreach (var result in results)
            {
                if (!string.IsNullOrWhiteSpace(result.Source))
                    sources.Add(result.Source);
            }

            if (descriptor.Analyzer is IAnnotationSourceProvider sourceProvider)
            {
                foreach (var src in sourceProvider.GetAnalyzerSources(document, context))
                {
                    if (!string.IsNullOrWhiteSpace(src))
                        sources.Add(src);
                }
            }

            await _analysisWriter
                .WriteAsync(containerUri, results, sources.Count > 0 ? sources : null, _stopping.Token)
                .ConfigureAwait(false);

            _metrics.EnrichmentDuration.Record(sw.Elapsed.TotalMilliseconds);
            if (enrich is not null)
            {
                try { _traceChains.AddOrUpdate(corrKey, _ => new TraceChain { Enrich = enrich.Context }, (_, c) => { c.Enrich = enrich.Context; return c; }); } catch { }
                StopRootIfPresent(corrKey);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ReportError(ex); }
        finally
        {
            if (!_stopping.IsCancellationRequested)
            {
                Interlocked.Increment(ref _enrichmentCompleted);
                _metrics.IncrementEnrich();
            }
        }
    }

    private async Task ScheduleIfChangedAsync(IFileInfo file, RepoUri uri, bool forceScheduling = false, CancellationToken ct = default)
    {
        try
        {
            var key = uri.AbsoluteUri.ToLowerInvariant();
            var chain = _traceChains.GetOrAdd(key, _ => new TraceChain());

            if (chain.RootActivity is null)
            {
                var fileName = FileNameFromUri(uri);
                var root = Activity.StartActivity(fileName, ActivityKind.Internal, null, tags:
                [
                    new KeyValuePair<string, object?>("url.full", uri.AbsoluteUri),
                    new KeyValuePair<string, object?>("file.name", fileName),
                    new KeyValuePair<string, object?>("file.extension", FileExtFromUri(uri) ?? string.Empty),
                    new KeyValuePair<string, object?>("repoql.uri", uri.AbsoluteUri)
                ]);
                chain.RootActivity = root;
            }

            using var hashAct = Activity.StartActivity(
                "repoql.hash",
                ActivityKind.Internal,
                chain.RootActivity?.Context ?? default,
                tags:
                [
                    new KeyValuePair<string, object?>("url.full", uri.AbsoluteUri),
                    new KeyValuePair<string, object?>("repoql.uri", uri.AbsoluteUri)
                ]);
            var hash = await hasher.HashAsync(file, ct).ConfigureAwait(false);
            _metrics.IncrementHash();
            var digest = "xxh64:" + Convert.ToHexString(hash).ToLowerInvariant();
            hashAct?.SetTag("file.size", file.Length);
            hashAct?.SetTag("file.hash", digest);

            if (_pendingDigestByUri.TryGetValue(key, out var pend) && string.Equals(pend, digest, StringComparison.Ordinal))
            {
                try { _metrics.RecordFileProcessed("unknown/unknown", "skipped_pending", file.Length, 0); } catch { }
                try { chain.RootActivity?.SetTag("repoql.status", "skipped_pending"); } catch { }
                hashAct?.SetTag("repoql.status", "skipped_pending");
                StopRootIfPresent(key);
                return;
            }

            if (!forceScheduling)
            {
                try
                {
                    var existingArtifact = await WithStorageAsync(store =>
                    {
                        var existing = store.GetDocumentByUri(uri);
                        return existing?.ArtifactId is Guid aid ? store.GetArtifact(aid) : null;
                    }, ct).ConfigureAwait(false);

                    if (existingArtifact is not null && string.Equals(existingArtifact.Digest, digest, StringComparison.Ordinal))
                    {
                        _metrics.RecordFileProcessed("unknown/unknown", "skipped_same", file.Length, 0);
                        chain.RootActivity?.SetTag("repoql.status", "skipped_same");
                        hashAct?.SetTag("repoql.status", "skipped_same");
                        StopRootIfPresent(key);
                        return;
                    }
                }
                catch (Exception ex) { ReportError(ex); }
            }
            else
            {
                chain.RootActivity?.SetTag("repoql.force_index", "true");
            }

            _pendingDigestByUri[key] = digest;
            if (hashAct is not null) chain.Hash = hashAct.Context;

            var artifact = new DiscoveredArtifact { File = file, RepoUri = uri, Hash = hash };
            await TryEnqueueClassificationAsync(artifact).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ReportError(ex); }
    }

    /// <summary>Raises an indexer event to all observers. Notify outside the lock to avoid re-entrancy stalls.</summary>
    private void RaiseEvent(IndexerEvent indexerEvent)
    {
        List<IObserver<IndexerEvent>> snapshot;
        lock (_observerLock) { snapshot = _observers.ToList(); }

        foreach (var observer in snapshot)
        {
            try { observer.OnNext(indexerEvent); } catch { }
        }

        try
        {
            var etype = indexerEvent switch
            {
                IRepositoryIndexer.ItemDiscoveredEvent => "discovered",
                IRepositoryIndexer.ItemUpdatedEvent => "updated",
                IRepositoryIndexer.ItemDeletedEvent => "deleted",
                IRepositoryIndexer.ItemMovedEvent => "moved",
                IRepositoryIndexer.ItemClassifiedEvent => "classified",
                IRepositoryIndexer.ItemIndexedEvent => "indexed",
                _ => "event"
            };
            _recentEvents.Enqueue(new DebugEvent(DateTime.UtcNow, etype, indexerEvent.CurrentUri.AbsoluteUri));
            TrimRecent(_recentEvents);
        }
        catch { }
    }

    private void ReportError(Exception exception)
    {
        Logger.LogWarning(exception, exception.Message);

        List<IObserver<IndexerEvent>> snapshot;
        lock (_observerLock) { snapshot = _observers.ToList(); }

        foreach (var observer in snapshot)
        {
            try { observer.OnError(exception); } catch { }
        }

        try
        {
            _recentErrors.Enqueue(exception);
            TrimRecent(_recentErrors);
        }
        catch { }
    }

    private class FileSystemChangeObserver(Func<ResourceChange, Task> onResourceChange, Action<Exception> onError)
        : IObserver<ResourceChange>
    {
        public void OnCompleted() { }
        public void OnError(Exception error) => onError(error);
        public void OnNext(ResourceChange value) => _ = onResourceChange(value);
    }

    private class Unsubscriber(List<IObserver<IndexerEvent>> observers, IObserver<IndexerEvent> observer) : IDisposable
    {
        public void Dispose()
        {
            if (observers.Contains(observer))
                observers.Remove(observer);
        }
    }

    private sealed class ReindexScope(RepositoryIndexer owner) : IDisposable
    {
        private RepositoryIndexer? _owner = owner;
        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            owner?.LeaveReindexScope();
        }
    }

    private static void TrimRecent<T>(ConcurrentQueue<T> q)
    {
        while (q.Count > RecentCapacity) q.TryDequeue(out _);
    }

    private sealed record DebugEvent(DateTime AtUtc, string Type, string Uri);

    // -------- Debugger View --------
    internal sealed class RepositoryIndexerDebugView(RepositoryIndexer owner)
    {
        public IReadOnlyList<DocInfo> Documents
        {
            get
            {
                var sql = @"SELECT n.uri,
                                   n.properties->>'documentationCategory' AS category,
                                   n.properties->>'description'           AS description,
                                   a.byte_size,
                                   a.media_type
                            FROM node n
                            LEFT JOIN artifact a ON a.id = n.artifact_id
                            WHERE n.kind = 'document'
                            ORDER BY lower(n.uri)";
                var rows = owner.WithStorage(store => store.RawQuery(sql).ToList());
                var list = new List<DocInfo>(rows.Count);
                foreach (var row in rows)
                {
                    list.Add(new DocInfo(
                        row.TryGetValue("uri", out var u) ? u?.ToString() ?? string.Empty : string.Empty,
                        row.TryGetValue("category", out var c) ? c?.ToString() : null,
                        row.TryGetValue("description", out var d) ? d?.ToString() : null,
                        row.TryGetValue("byte_size", out var b) && b is long bl ? bl : (long?)null,
                        row.TryGetValue("media_type", out var m) ? m?.ToString() : null
                    ));
                }
                return list;
            }
        }

        public IReadOnlyList<KindCount> NodeKinds
        {
            get
            {
                var sql = "SELECT kind, COUNT(*) AS count FROM node GROUP BY kind ORDER BY count DESC";
                var rows = owner.WithStorage(store => store.RawQuery(sql).ToList());
                var list = new List<KindCount>(rows.Count);
                foreach (var row in rows)
                {
                    list.Add(new KindCount(
                        row.TryGetValue("kind", out var k) ? k?.ToString() ?? string.Empty : string.Empty,
                        row.TryGetValue("count", out var c) && c is long l ? l : 0
                    ));
                }
                return list;
            }
        }

        public long ArtifactCount => Count("artifact");
        public long NodeCount => Count("node");
        public long EdgeCount => Count("edge");
        public long SpanCount => Count("span");
        public long AnnotationCount => Count("annotation");

        public QueueStatus Queues
        {
            get
            {
                var cls = owner._classificationQueue?.Depth ?? 0;
                var prs = owner._parsingQueue?.Depth ?? 0;
                return new QueueStatus(cls, prs);
            }
        }

        public WriterStatus? Writer => owner._dbWriter?.GetStatus();

        public IReadOnlyList<RecentEvent> RecentEvents
            => [.. owner._recentEvents.Select(e => new RecentEvent(e.AtUtc, e.Type, e.Uri))];

        public IReadOnlyList<string> RecentErrors
            => [.. owner._recentErrors.Select(e => e.ToString())];

        public IReadOnlyList<string> PendingDigests => [.. owner._pendingDigestByUri.Keys];
        public IReadOnlyList<string> InflightParses => [.. owner._inflightParses.Keys];
        public IReadOnlyList<RecentByUriItem> RecentByUri
            => [.. owner._recentByUri.Select(kv => new RecentByUriItem(kv.Key, kv.Value.Digest, kv.Value.At))];

        private long Count(string table)
        {
            return owner.WithStorage(store =>
            {
                foreach (var row in store.RawQuery($"SELECT COUNT(*) AS c FROM {table}"))
                    return row.TryGetValue("c", out var v) && v is long l ? l : 0;
                return 0;
            });
        }

        [DebuggerDisplay("{Kind}: {Uri}")]
        public sealed record DocInfo(string Uri, string? Category, string? Description, long? ByteSize, string? MediaType)
        {
            public string Kind => "document";
        }

        [DebuggerDisplay("{Kind} = {Count}")]
        public sealed record KindCount(string Kind, long Count);

        [DebuggerDisplay("cls={ClassificationDepth}, prs={ParsingDepth}")]
        public sealed record QueueStatus(int ClassificationDepth, int ParsingDepth);

        [DebuggerDisplay("{AtUtc:HH:mm:ss} {Type}: {Uri}")]
        public sealed record RecentEvent(DateTime AtUtc, string Type, string Uri);

        [DebuggerDisplay("{Uri} {Digest} {AtUtc:HH:mm:ss}")]
        public sealed record RecentByUriItem(string Uri, string Digest, DateTimeOffset AtUtc);
    }
}
