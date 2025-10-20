using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RepoQL.Contracts;
using RepoQL.Contracts.Analysis;
using RepoQL.Contracts.Data;
using RepoQL.Contracts.Models;
using RepoQL.Core.Analysis;
using RepoQL.Core.Metrics;
using RepoQL.FileSystem;
using RepoQL.FileSystem.Abstractions;
using IFileInfo = Microsoft.Extensions.FileProviders.IFileInfo;
using IFileSystemWatcher = RepoQL.FileSystem.Abstractions.IFileSystemWatcher;

namespace RepoQL.Core;

public record IndexerEvent(IFileInfo FileInfo, RepoUri CurrentUri);

[DebuggerTypeProxy(typeof(RepositoryIndexerDebugView))]
public class RepositoryIndexer(
    IndexingMetrics metrics,
    Meter meter,
    IMultiFileSystem fileSystem,
    IGraphStore storage,
    IFileClassifier classifier,
    IFormatRegistry formatRegistry,
    IAnalysisWorkspace analysisWorkspace,
    IUriFilter uriFilter,
    IHasher hasher,
    IDatabaseWriter? dbWriter = null,
    IAnalysisResultWriter? analysisWriter = null,
    IAnalyzerSettingsProvider? settingsProvider = null,
    string? repositoryRoot = null,
    ILogger<RepositoryIndexer>? logger = null) : IRepositoryIndexer
{
    private ILogger<RepositoryIndexer> Logger { get; } = logger ?? NullLogger<RepositoryIndexer>.Instance;
    private static readonly ActivitySource Activity = new("RepoQL.Indexing");
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
    private readonly Lock _observerLock = new();
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
    private readonly int _writerCapacity = dbWriter is RepoQL.Data.DuckDB.SingleThreadedDatabaseWriter single ? single.QueueCapacity : 0;
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
        catch
        {
            return uri.AbsoluteUri;
        }
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

    private sealed class NullServiceProvider : IServiceProvider
    {
        public static readonly NullServiceProvider Instance = new();
        private NullServiceProvider() { }
        public object? GetService(Type serviceType) => null;
    }

    private async Task<bool> TryEnqueueClassificationAsync(DiscoveredArtifact artifact)
    {
        if (_classificationQueue is null)
            throw new InvalidOperationException("The indexer has not been started.");
        try
        {
            var enqueued = await _classificationQueue.EnqueueAsync(artifact, _stopping.Token).ConfigureAwait(false);
            if (enqueued)
                Interlocked.Increment(ref _classificationScheduled);
            return enqueued;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private async Task<bool> TryEnqueueParsingAsync(DiscoveredArtifact artifact)
    {
        if (_parsingQueue is null)
            throw new InvalidOperationException("The indexer has not been started.");
        try
        {
            var enqueued = await _parsingQueue.EnqueueAsync(artifact, _stopping.Token).ConfigureAwait(false);
            if (enqueued)
                Interlocked.Increment(ref _parsingScheduled);
            return enqueued;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private async Task<bool> TryEnqueueEnrichmentAsync(string containerUri)
    {
        if (_enrichmentQueue is null)
            return false;
        try
        {
            var enqueued = await _enrichmentQueue.EnqueueAsync(containerUri, _stopping.Token).ConfigureAwait(false);
            if (enqueued)
                Interlocked.Increment(ref _enrichmentScheduled);
            return enqueued;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await WithStorageAsync(store => store.EnsureSchema(), cancellationToken).ConfigureAwait(false);

        _classificationQueue = new("classification", 10000, 3, ClassifyFileAsync, cancellationToken, meter);
        _parsingQueue = new("parsing", 10000, 3, ParseAndStoreFileAsync, cancellationToken, meter);

        {
            var workerCount = Math.Max(Environment.ProcessorCount / 2, 1);
            _enrichmentQueue = new("enrichment", 4000, workerCount, EnrichDocumentAsync, cancellationToken, meter);
        }

        await foreach (var entry in fileSystem.EnumerateAsync(cancellationToken))
        {
            var artifact = new DiscoveredArtifact
            {
                File = entry.File,
                RepoUri = entry.Uri
            };
            if (!uriFilter.IncludeFile(artifact.RepoUri))
                continue;
            metrics.IncrementDiscover();
            RaiseEvent(new IRepositoryIndexer.ItemDiscoveredEvent(entry.File, artifact.RepoUri));

            // Initial enumeration should enqueue immediately so callers can wait deterministically
            // via WaitForIdle(). Debouncing is applied only to watcher events below.
            var a = artifact; // capture
            _ = TryEnqueueClassificationAsync(a);
        }

        _watcher.Subscribe(new FileSystemChangeObserver(change =>
        {
            if (!uriFilter.IncludeFile(change.CurrentUri))
                return;

            // Queue for classification/indexing
            var artifact = new DiscoveredArtifact
            {
                File = change.File,
                RepoUri = change.CurrentUri
            };
            var changedKey = artifact.RepoUri.AbsoluteUri.ToLowerInvariant();
            _classifyDebouncer.Push(changedKey, () =>
            {
                // Raise appropriate event based on the change type
                // Record FS event metric
                metrics.RecordFsEvent(change.Kind.ToString());
                switch (change.Kind)
                {
                    case ResourceEvent.Created:
                        RaiseEvent(new IRepositoryIndexer.ItemDiscoveredEvent(change.File, change.CurrentUri));
                        break;
                    case ResourceEvent.Updated:
                        RaiseEvent(new IRepositoryIndexer.ItemUpdatedEvent(change.File, change.CurrentUri));
                        break;
                    case ResourceEvent.Deleted:
                        RaiseEvent(new IRepositoryIndexer.ItemDeletedEvent(change.File, change.CurrentUri));
                        break;
                    case ResourceEvent.Moved:
                        RaiseEvent(new IRepositoryIndexer.ItemMovedEvent(change.File, change.CurrentUri,
                            change.PreviousUri!));
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(change));
                }
                var a = artifact; // capture
                _ = TryEnqueueClassificationAsync(a);
            });
        }, ReportError));
        await _watcher.StartAsync(cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_isDisposed)
            return;

        // Stop accepting new work
        await _stopping.CancelAsync();

        // Wait for classification worker to complete
        if (_classificationQueue is not null)
            await _classificationQueue.DisposeAsync();

        // Wait for parsing worker to complete
        if (_parsingQueue is not null)
            await _parsingQueue.DisposeAsync();

        if (_enrichmentQueue is not null)
            await _enrichmentQueue.DisposeAsync();

        // Stop and dispose the watcher
        await _watcher.StopAsync(CancellationToken.None);
        await _watcher.DisposeAsync();

        // Notify all observers that the sequence is complete
        lock (_observerLock)
        {
            foreach (var observer in _observers.ToList())
            {
                try
                {
                    observer.OnCompleted();
                }
                catch
                {
                    // Swallow exceptions
                }
            }

            _observers.Clear();
        }
    }

    public IDisposable Subscribe(IObserver<IndexerEvent> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);

        lock (_observerLock)
        {
            _observers.Add(observer);
        }

        return new Unsubscriber(_observers, observer);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None);
        _isDisposed = true;
        // Dispose the cancellation token source
        _stopping.Dispose();
        _storageGate.Dispose();
        GC.SuppressFinalize(this);
    }

    public async Task QueueForIndexingAsync(IEnumerable<IFileInfo> files, bool skipUnchanged = true)
    {
        if (_classificationQueue is null)
            throw new InvalidOperationException("The indexer has not been started.");
        foreach (var file in files)
        {
            // Best-effort: require callers to use the RepoUri overload for non-physical files.
            // For physical files, attempt to build a canonical file:/// URI using absolute path.
            RepoUri repoUri;
            var physPath = file.PhysicalPath;
            if (!string.IsNullOrEmpty(physPath))
            {
                // Normalize to file:/// absolute path under repo root is unknown here,
                // so use file:/// with absolute path segments.
                var abs = System.IO.Path.GetFullPath(physPath).Replace('\\', '/');
                if (!abs.StartsWith('/')) abs = "/" + abs;
                repoUri = RepoUri.Parse($"file://{abs}");
            }
            else
            {
                throw new NotSupportedException("QueueForIndexingAsync(IFileInfo) cannot infer RepoUri. Use QueueForIndexingAsync(RepoUri) instead.");
            }
            await ScheduleIfChangedAsync(file, repoUri, !skipUnchanged, _stopping.Token);
        }
    }

    public async Task QueueForIndexingAsync(IEnumerable<RepoUri> uris, bool skipUnchanged = true)
    {
        if (_classificationQueue is null)
            throw new InvalidOperationException("The indexer has not been started.");
        foreach (var uri in uris)
        {
            var file = fileSystem.GetFile(uri);
            await ScheduleIfChangedAsync(file, uri, !skipUnchanged, _stopping.Token);
        }
    }

    public async Task WaitForIdle(CancellationToken cancellationToken = default)
    {
        // If not started, nothing to wait for
        if (_classificationQueue is null || _parsingQueue is null)
            return;

        var readinessTasks = new List<Task>
        {
            _classificationQueue.WorkersReadyAsync(),
            _parsingQueue.WorkersReadyAsync()
        };
        if (_enrichmentQueue is not null)
            readinessTasks.Add(_enrichmentQueue.WorkersReadyAsync());

        try { await Task.WhenAll(readinessTasks).ConfigureAwait(false); } catch { }

        while (true)
        {
            var waitTasks = new List<Task>();
            if (_classificationQueue.Depth > 0)
                waitTasks.Add(_classificationQueue.WhenIdleAsync());
            if (_parsingQueue.Depth > 0)
                waitTasks.Add(_parsingQueue.WhenIdleAsync());
            if (_enrichmentQueue is not null && _enrichmentQueue.Depth > 0)
                waitTasks.Add(_enrichmentQueue.WhenIdleAsync());

            if (waitTasks.Count > 0)
                await Task.WhenAll(waitTasks).ConfigureAwait(false);

            if (_dbWriter is not null)
            {
                try { await _dbWriter.FlushAsync(cancellationToken).ConfigureAwait(false); }
                catch (Exception ex) { ReportError(ex); }
            }

            var classificationPending = _classificationQueue.Depth > 0;
            var parsingPending = _parsingQueue.Depth > 0;
            var enrichmentPending = _enrichmentQueue is not null && _enrichmentQueue.Depth > 0;

            if (!classificationPending && !parsingPending && !enrichmentPending)
                break;

            await Task.Yield();
        }
    }

    public async Task WaitForStagesIdleAsync(PipelineStage stages, CancellationToken cancellationToken = default)
    {
        if ((stages & PipelineStage.Discovery) != 0 && _classificationQueue is not null)
        {
            var task = _classificationQueue.WhenIdleAsync();
            await task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        if ((stages & PipelineStage.Parsing) != 0 && _parsingQueue is not null)
        {
            var task = _parsingQueue.WhenIdleAsync();
            await task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        if ((stages & PipelineStage.Analysis) != 0 && _enrichmentQueue is not null)
        {
            var task = _enrichmentQueue.WhenIdleAsync();
            await task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        if ((stages & PipelineStage.Writer) != 0)
        {
            await WaitForWriterIdle(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task WaitForAnyStageIdleAsync(PipelineStage stages, CancellationToken cancellationToken = default)
    {
        var waits = new List<Task>();
        if ((stages & PipelineStage.Discovery) != 0 && _classificationQueue is not null)
            waits.Add(_classificationQueue.WhenIdleAsync());
        if ((stages & PipelineStage.Parsing) != 0 && _parsingQueue is not null)
            waits.Add(_parsingQueue.WhenIdleAsync());
        if ((stages & PipelineStage.Analysis) != 0 && _enrichmentQueue is not null)
            waits.Add(_enrichmentQueue.WhenIdleAsync());
        if ((stages & PipelineStage.Writer) != 0)
            waits.Add(WaitForWriterIdle(cancellationToken));

        if (waits.Count == 0)
            return;

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
            catch (Exception ex)
            {
                ReportError(ex);
            }
        }

        return new PipelineSnapshot(captured, discovery, parsing, analysis, writerSnapshot, IsReindexing);
    }

    public bool IsReindexing => Volatile.Read(ref _activeReindexScopes) > 0;

    public IDisposable EnterReindexScope()
    {
        Interlocked.Increment(ref _activeReindexScopes);
        return new ReindexScope(this);
    }

    private void LeaveReindexScope()
    {
        Interlocked.Decrement(ref _activeReindexScopes);
    }

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
            // Parent to previous step (classify preferred, else hash) to keep one distributed trace
            var corrKey = file.RepoUri.AbsoluteUri.ToLowerInvariant();
            ActivityContext parent = default;
            if (_traceChains.TryGetValue(corrKey, out var chain) && (chain.Classify is { } || chain.Hash is { }))
            {
                parent = chain.Classify ?? chain.Hash ?? default;
            }
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
                // Record file size if available and capture parse context for downstream parenting
                try { parseActivity.SetTag("file.size", file.File.Length); } catch { }
                try
                {
                    var c = _traceChains.GetOrAdd(corrKey, _ => new TraceChain());
                    c.Parse = parseActivity.Context;
                }
                catch { }
            }
            var descriptor = await ResolveDescriptorAsync(file, parseActivity).ConfigureAwait(false);
            var document = await descriptor.Loader.LoadAsync(file, _stopping.Token).ConfigureAwait(false);
            parseActivity?.SetTag("content.type", document.MediaType.ToString());

            var sw = Stopwatch.StartNew();
            if (descriptor.Materializer is null)
                throw new InvalidOperationException($"Format '{document.MediaType}' does not provide a materializer.");

            var records = descriptor.Materializer.Materialize(document);
            metrics.IncrementParse();
            try
            {
                metrics.NodesExtracted.Add(records.Nodes.Length);
                metrics.NodesPerDocument.Record(records.Nodes.Length);
                parseActivity?.SetTag("repoql.nodes.count", records.Nodes.Length);
                parseActivity?.SetTag("repoql.spans.count", records.Spans.Length);
                parseActivity?.SetTag("repoql.edges.count", records.Edges.Length);
            }
            catch (Exception ex)
            {
                ReportError(ex);
            }

            // Prefer single-writer path when available
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
                        if (!result.Success)
                            return Task.CompletedTask;
                        try
                        {
                            if (parseActivity is not null)
                            {
                                var c = _traceChains.GetOrAdd(corrKey, _ => new TraceChain());
                                c.Parse = parseActivity.Context;
                            }
                        }
                        catch { }
                        _documentCache[operationUri.AbsoluteUri.ToLowerInvariant()] = document;
                        ScheduleEnrichment(operationUri);
                        if (_enrichmentQueue is null)
                        {
                            try { StopRootIfPresent(corrKey); } catch { }
                        }
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
                if (_enrichmentQueue is null)
                {
                    try { StopRootIfPresent(corrKey); } catch { }
                }
            }

            metrics.IncrementIndex();

            try
            {
                var bytes = file.File.Length;
                metrics.RecordFileProcessed(document.MediaType.ToString(), "indexed", bytes, sw.Elapsed.TotalMilliseconds);
            }
            catch (Exception ex)
            {
                ReportError(ex);
            }

            RaiseEvent(new IRepositoryIndexer.ItemIndexedEvent(file.File, file.RepoUri, document.MediaType));

            return;
        }
        catch (Exception ex)
        {
            ReportError(ex);
        }
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
            if (!item.File.Exists)
            {
                // Sometimes it takes a moment if the file is new
                await Task.Yield();
            }
            // Hash is computed before enqueue; ensure present
            if (item.Hash is null)
            {
                item.Hash = await hasher.HashAsync(item.File, _stopping.Token);
                metrics.IncrementHash();
            }

            // Classify the item
            var type = classifier.GetMediaType(item.File);

            // Create classify activity with parent=hash (if present) to form a proper chain
            var corrKey = item.RepoUri.AbsoluteUri.ToLowerInvariant();
            ActivityContext parent = default;
            if (_traceChains.TryGetValue(corrKey, out var existing) && existing.Hash is { } hc)
                parent = hc;
            using (var classify = Activity.StartActivity(
                "repoql.classify",
                ActivityKind.Internal,
                parent,
                tags:
                [
                    new KeyValuePair<string, object?>("url.full", item.RepoUri.AbsoluteUri),
                    new KeyValuePair<string, object?>("repoql.uri", item.RepoUri.AbsoluteUri),
                    new KeyValuePair<string, object?>("content.type", type.ToString())
                ],
                links: null))
            {
                if (classify is not null)
                {
                    var chain = _traceChains.GetOrAdd(corrKey, _ => new TraceChain());
                    chain.Classify = classify.Context;
                    try
                    {
                        // Also tag the root with detected media info
                        var baseType = $"{type.Type}/{type.Subtype}{(type.Suffix is null ? string.Empty : "+" + type.Suffix)}";
                        chain.RootActivity?.SetTag("content.type", baseType);
                    }
                    catch { }
                }
            }

            // Raise item classified event
            RaiseEvent(new IRepositoryIndexer.ItemClassifiedEvent(item.File, item.RepoUri, type));
            item.MediaType = type;

            // Recent dedup: if this exact (uri,digest) was handled very recently, skip
            var digest = "xxh64:" + Convert.ToHexString(item.Hash).ToLowerInvariant();
            var uriKey = item.RepoUri.AbsoluteUri.ToLowerInvariant();
            if (_recentByUri.TryGetValue(uriKey, out var seen)
                && string.Equals(seen.Digest, digest, StringComparison.Ordinal)
                && (DateTimeOffset.UtcNow - seen.At) < TimeSpan.FromSeconds(5))
            {
                // Already processed very recently; record metric and skip
                try { metrics.RecordFileProcessed(type.ToString(), "skipped_recent", item.File.Length, 0); } catch { }
                StopRootIfPresent(corrKey);
                return;
            }

            // Short-circuit: if document exists and artifact digest matches, skip parsing
            try
            {
                var existingArtifact = await WithStorageAsync(store =>
                {
                    var doc = store.GetDocumentByUri(item.RepoUri);
                    return doc?.ArtifactId is Guid aid ? store.GetArtifact(aid) : null;
                }, _stopping.Token).ConfigureAwait(false);
                if (existingArtifact is not null && string.Equals(existingArtifact.Digest, digest, StringComparison.Ordinal))
                {
                    // Already up-to-date: record metrics only (do not emit duplicate Indexed event)
                    try
                    {
                        var bytes = item.File.Length;
                        metrics.RecordFileProcessed(type.ToString(), "skipped", bytes, 0);
                    }
                    catch { }
                    StopRootIfPresent(corrKey);
                    return;
                }
            }
            catch (Exception ex)
            {
                // Surface unexpected short-circuit failures to observers
                ReportError(ex);
            }

            // Mark as recently scheduled and enqueue for parsing
            _recentByUri[uriKey] = (digest, DateTimeOffset.UtcNow);
            await TryEnqueueParsingAsync(item).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ReportError(ex);
        }
        finally
        {
            if (!_stopping.IsCancellationRequested)
                Interlocked.Increment(ref _classificationCompleted);
        }
    }

    private async Task<FormatDescriptor> ResolveDescriptorAsync(DiscoveredArtifact artifact, Activity? activity)
    {
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
        if (_enrichmentQueue is null)
            return;

        if (_stopping.IsCancellationRequested)
            return;

        var value = uri.AbsoluteUri;
        _ = Task.Run(async () =>
        {
            try
            {
                await TryEnqueueEnrichmentAsync(value).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // shutdown in progress
            }
            catch (Exception ex)
            {
                ReportError(ex);
            }
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
            {
                parent = chain.Parse ?? chain.Classify ?? chain.Hash ?? default;
            }

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
            if (documentNode is null)
                return;

            var cacheKey = repoUri.AbsoluteUri.ToLowerInvariant();
            DocumentModel? document;
            if (!_documentCache.TryRemove(cacheKey, out document) || document is null)
            {
                document = await analysisWorkspace.LoadAsync(repoUri, _stopping.Token).ConfigureAwait(false);
            }

            if (document is null)
                return;

            if (!formatRegistry.TryResolveByMedia(document.MediaType, out var descriptor))
                return;

            var settings = settingsProvider?.Resolve(containerUri, document.MediaType, documentNode) ?? new AnalyzerSettings();
            var context = new AnalyzerContext(settings, _repositoryRoot, formatRegistry, analysisWorkspace);

            var results = new List<AnalysisResult>();
            await foreach (var result in descriptor.Analyzer.AnalyzeAsync(document, context, _stopping.Token).ConfigureAwait(false))
            {
                results.Add(result);
            }

            if (results.Count > 0)
            {
                await _analysisWriter.WriteAsync(containerUri, results, _stopping.Token).ConfigureAwait(false);
            }

            metrics.EnrichmentDuration.Record(sw.Elapsed.TotalMilliseconds);
            if (enrich is not null)
            {
                var c = _traceChains.GetOrAdd(corrKey, _ => new TraceChain());
                c.Enrich = enrich.Context;
                StopRootIfPresent(corrKey);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ReportError(ex);
        }
        finally
        {
            if (!_stopping.IsCancellationRequested)
            {
                Interlocked.Increment(ref _enrichmentCompleted);
                metrics.IncrementEnrich();
            }
        }
    }

    private async Task ScheduleIfChangedAsync(IFileInfo file, RepoUri uri, bool forceScheduling = false, CancellationToken ct = default)
    {
        try
        {
            var key = uri.AbsoluteUri.ToLowerInvariant();
            var chain = _traceChains.GetOrAdd(key, _ => new TraceChain());
            // Ensure a root activity exists (name is just the file name, no path)
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
            metrics.IncrementHash();
            var digest = "xxh64:" + Convert.ToHexString(hash).ToLowerInvariant();
            hashAct?.SetTag("file.size", file.Length);
            hashAct?.SetTag("file.hash", digest);

            // If same digest already pending, skip
            if (_pendingDigestByUri.TryGetValue(key, out var pend) && string.Equals(pend, digest, StringComparison.Ordinal))
            {
                try { metrics.RecordFileProcessed("unknown/unknown", "skipped_pending", file.Length, 0); } catch { }
                try { chain.RootActivity?.SetTag("repoql.status", "skipped_pending"); } catch { }
                hashAct?.SetTag("repoql.status", "skipped_pending");
                StopRootIfPresent(key);
                return;
            }

            // DB short-circuit: skip if existing doc digest matches
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
                        metrics.RecordFileProcessed("unknown/unknown", "skipped_same", file.Length, 0);
                        chain.RootActivity?.SetTag("repoql.status", "skipped_same");
                        hashAct?.SetTag("repoql.status", "skipped_same");
                        StopRootIfPresent(key);
                        return;
                    }
                }
                catch (Exception ex)
                {
                    ReportError(ex);
                }
            }
            else
            {
                chain.RootActivity?.SetTag("repoql.force_index", "true");
            }

            // Mark pending and enqueue for classification with hash pre-set
            _pendingDigestByUri[key] = digest;
            // Record hash context for later span links
            if (hashAct is not null)
            {
                chain.Hash = hashAct.Context;
            }
            var artifact = new DiscoveredArtifact { File = file, RepoUri = uri, Hash = hash };
            await TryEnqueueClassificationAsync(artifact).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ReportError(ex); }
    }

    /// <summary>
    ///     Raises an indexer event to all subscribed observers.
    /// </summary>
    /// <param name="indexerEvent">The event to raise to all observers.</param>
    private void RaiseEvent(IndexerEvent indexerEvent)
    {
        lock (_observerLock)
        {
            foreach (var observer in _observers.ToList())
            {
                try
                {
                    observer.OnNext(indexerEvent);
                }
                catch
                {
                    // Swallow exceptions to prevent one observer from affecting others
                }
            }
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
        lock (_observerLock)
        {
            foreach (var observer in _observers.ToList())
            {
                try
                {
                    observer.OnError(exception);
                }
                catch
                {
                    // Swallow exceptions to prevent one observer from affecting others
                }
            }
        }
        try
        {
            _recentErrors.Enqueue(exception);
            TrimRecent(_recentErrors);
        }
        catch { }
    }

    private class FileSystemChangeObserver(Action<ResourceChange> onResourceChange, Action<Exception> onError)
        : IObserver<ResourceChange>
    {
        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
            onError(error);
        }

        public void OnNext(ResourceChange value)
        {
            onResourceChange(value);
        }
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
        while (q.Count > RecentCapacity)
        {
            q.TryDequeue(out _);
        }
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
