using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RepoQL.Contracts;
using RepoQL.Contracts.Analysis;
using RepoQL.Contracts.Data;
using RepoQL.Contracts.Models;
using RepoQL.Core;
using RepoQL.Core.Analysis;
using RepoQL.Data.DuckDB;
using RepoQL.FileSystem;
using RepoQL.FileSystem.Abstractions;
using RepoQL.FileSystem.InMemory;
using RepoQL.Indexing;
using RepoQL.Indexing.FileSystems;
using RepoQL.Indexing.Hosting;
using RepoQL.Indexing.Indexing;
using RepoQL.Indexing.Indexing.Commit;
using RepoQL.Indexing.Indexing.Pipelines;
using RepoQL.Indexing.Indexing.Pipelines.Analysis;
using RepoQL.Indexing.Indexing.Pipelines.Classification;
using RepoQL.Indexing.Indexing.Pipelines.Discovery;
using RepoQL.Indexing.Indexing.Pipelines.Parsing;
using RepoQL.Indexing.Indexing.PostProcessing;
using RepoQL.Indexing.Indexing.State;
using RepoQL.Metrics;
using MetricsIndexingMetrics = RepoQL.Metrics.IndexingMetrics;
using System.Threading;

namespace RepoQL.Testing.Scaffolding;

/// <summary>
/// Builds an in-memory repository backed by DuckDB storage and the new RepoQL indexing stack for tests.
/// </summary>
public sealed class IndexedRepoBuilder : IAsyncDisposable
{
    private readonly CompositeFileSystem _compositeFileSystem;
    private readonly IndexingCoordinator _coordinator;
    private readonly RepoqlHost _host;
    private readonly IDatabaseWriter _writer;
    private readonly bool _ownsWriter;
    private readonly SingleThreadedDatabaseWriter? _singleThreadedWriter;
    private readonly IAnalysisResultWriter? _analysisWriter;
    private readonly bool _deleteDatabaseOnDispose;
    private readonly string _databasePath;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IReadOnlyList<FormatSqlScript> _formatScripts;
    private DuckDbGraphStore _store;
    private readonly ConcurrentDictionary<string, RepoUri> _trackedUris = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    private IndexedRepoBuilder(
        MemoryFileSystem fileSystem,
        CompositeFileSystem composite,
        DuckDbGraphStore store,
        MetricsIndexingMetrics metrics,
        FormatRegistry formatRegistry,
        AnalysisWorkspace workspace,
        IndexingEngine engine,
        IndexingCoordinator coordinator,
        RepoqlHost host,
        IDatabaseWriter writer,
        bool ownsWriter,
        SingleThreadedDatabaseWriter? singleThreadedWriter,
        IHasher hasher,
        IAnalysisResultWriter? analysisWriter,
        string databasePath,
        bool deleteDatabaseOnDispose,
        ILoggerFactory loggerFactory,
        IReadOnlyList<FormatSqlScript> formatScripts)
    {
        FileSystem = fileSystem;
        _compositeFileSystem = composite;
        _store = store;
        Metrics = metrics;
        FormatRegistry = formatRegistry;
        Workspace = workspace;
        Engine = engine;
        _coordinator = coordinator;
        _host = host;
        _writer = writer;
        _ownsWriter = ownsWriter;
        _singleThreadedWriter = singleThreadedWriter;
        Hasher = hasher;
        _analysisWriter = analysisWriter;
        _databasePath = databasePath;
        _deleteDatabaseOnDispose = deleteDatabaseOnDispose;
        _loggerFactory = loggerFactory;
        _formatScripts = formatScripts;
    }

    public MemoryFileSystem FileSystem { get; }

    public DuckDbGraphStore Store => _store;

    public FormatRegistry FormatRegistry { get; }

    public AnalysisWorkspace Workspace { get; }

    public IndexingEngine Engine { get; }

    public MetricsIndexingMetrics Metrics { get; }

    public IHasher Hasher { get; }

    public IMultiFileSystem FileHub => _compositeFileSystem;

    public string DatabasePath => _databasePath;

    public static async Task<IndexedRepoBuilder> CreateAsync(
        Action<IndexedRepoOptions>? configure = null,
        CancellationToken cancellationToken = default)
    {
        var options = new IndexedRepoOptions();
        configure?.Invoke(options);

        if (options.Formats.Count == 0)
            throw new InvalidOperationException("IndexedRepoOptions must register at least one format descriptor.");

        var hasher = options.ResolveHasher();
        var classifier = options.ResolveClassifier();
        var fileSystem = new MemoryFileSystem(options.Root);
        var composite = new CompositeFileSystem(
            CompositeFileSystemMount.CreatePrimary(fileSystem),
            options.AdditionalMounts);
        var metrics = new MetricsIndexingMetrics();
        var formatRegistry = new FormatRegistry(options.Formats);
        var workspace = new AnalysisWorkspace(composite, classifier, hasher, formatRegistry);
        var loggerFactory = options.LoggerFactory ?? NullLoggerFactory.Instance;
        var repositoryRoot = string.IsNullOrWhiteSpace(options.RepositoryRoot)
            ? Directory.GetCurrentDirectory()
            : options.RepositoryRoot;

        var databasePath = ResolveDatabasePath(options);
        var deleteOnDispose = options.DeleteDatabaseOnDispose || string.IsNullOrWhiteSpace(options.DatabasePath);

        DuckDbGraphStore? store = null;
        IAnalysisResultWriter? analysisWriter = null;
        SingleThreadedDatabaseWriter? singleWriter = null;
        RepoqlHost? host = null;
        IndexingCoordinator? coordinator = null;
        IndexingEngine? engine = null;
        bool ownsWriter = false;

        try
        {
            var formatScripts = formatRegistry.Formats
                .Select(f => f.Loader)
                .OfType<IFormatSchemaProvider>()
                .SelectMany(p => p.GetSchemaScripts())
                .Where(s => !string.IsNullOrWhiteSpace(s.Sql))
                .ToList();

            store = new DuckDbGraphStore(
                databasePath,
                metrics,
                logger: loggerFactory.CreateLogger<DuckDbGraphStore>(),
                formatSchemaScripts: formatScripts);
            store.EnsureSchema();

            analysisWriter = options.CreateAnalysisWriter?.Invoke(store);

            var connectionFactory = new DuckDBConnectionFactory($"Data Source={databasePath}");

            IDatabaseWriter writer;
            if (options.DatabaseWriter is not null)
            {
                writer = options.DatabaseWriter;
            }
            else
            {
                var graphStoreFactory = new DuckDbGraphStoreFactory(
                    metrics,
                    embeddingProvider: null,
                    loggerFactory.CreateLogger<DuckDbGraphStore>());
                singleWriter = new SingleThreadedDatabaseWriter(
                    connectionFactory,
                    graphStoreFactory,
                    metrics,
                    loggerFactory.CreateLogger<SingleThreadedDatabaseWriter>());
                await singleWriter.StartAsync(cancellationToken).ConfigureAwait(false);
                writer = singleWriter;
                ownsWriter = true;
            }

            var artifactPruner = new StorageBackedArtifactPruner(
                connectionFactory,
                () => coordinator?.IsReindexing ?? false,
                loggerFactory.CreateLogger<StorageBackedArtifactPruner>());
            var catalog = new DocumentCatalog(NullDocumentCatalogDataSource.Instance);
            var committer = new IndexingCommitter(
                writer,
                catalog,
                loggerFactory.CreateLogger<IndexingCommitter>());

            var classificationPipeline = new ClassificationPipeline(
                new[]
                {
                    new WorkspaceClassifier(classifier, loggerFactory.CreateLogger<WorkspaceClassifier>())
                },
                loggerFactory.CreateLogger<ClassificationPipeline>());

            var parsingPipeline = new ParsingPipeline(
                new[]
                {
                    new FormatRegistryParser(formatRegistry, loggerFactory.CreateLogger<FormatRegistryParser>())
                },
                loggerFactory.CreateLogger<ParsingPipeline>());

            var singleFilePipeline = new SingleFileAnalysisPipeline(
                new[]
                {
                    new FormatRegistryAnalyzer(
                        formatRegistry,
                        workspace,
                        options.SettingsProvider,
                        repositoryRoot,
                        loggerFactory.CreateLogger<FormatRegistryAnalyzer>())
                },
                loggerFactory.CreateLogger<SingleFileAnalysisPipeline>());

            var multiFilePipeline = new MultiFileAnalysisPipeline(
                Array.Empty<IAsyncPipeline<IAnnotatedArtifact, Annotation[]>>(),
                loggerFactory.CreateLogger<MultiFileAnalysisPipeline>());

            var indexRebuildPipeline = new IndexRebuildPipeline(
                Array.Empty<IAsyncPipeline<IAnnotatedArtifact, string>>(),
                loggerFactory.CreateLogger<IndexRebuildPipeline>());

            engine = new IndexingEngine(
                writer,
                options.Filter,
                classificationPipeline,
                parsingPipeline,
                singleFilePipeline,
                multiFilePipeline,
                indexRebuildPipeline,
                catalog,
                committer,
                artifactPruner,
                NullVectorIndexCoordinator.Instance,
                options.EngineOptions,
                loggerFactory.CreateLogger<IndexingEngine>());

            coordinator = new IndexingCoordinator(
                composite,
                engine,
                writer,
                loggerFactory.CreateLogger<IndexingCoordinator>());

            var hostOptions = new RepoqlHostOptions
            {
                RunFullScanOnStartup = options.RunFullScanOnStartup,
                EnableWatching = options.EnableWatching
            };

            host = new RepoqlHost(
                composite,
                engine,
                Options.Create(hostOptions),
                loggerFactory.CreateLogger<RepoqlHost>());
            await host.StartAsync(cancellationToken).ConfigureAwait(false);

            return new IndexedRepoBuilder(
                fileSystem,
                composite,
                store,
                metrics,
                formatRegistry,
                workspace,
                engine,
                coordinator,
                host,
                writer,
                ownsWriter,
                singleWriter,
                hasher,
                analysisWriter,
                databasePath,
                deleteOnDispose,
                loggerFactory,
                formatScripts);
        }
        catch
        {
            if (host is not null)
            {
                try { await host.StopAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
                host.Dispose();
            }

            switch (analysisWriter)
            {
                case IAsyncDisposable asyncWriter:
                    await asyncWriter.DisposeAsync().ConfigureAwait(false);
                    break;
                case IDisposable disposableWriter:
                    disposableWriter.Dispose();
                    break;
            }

            store?.Dispose();
            metrics.Dispose();

            if (singleWriter is not null)
            {
                try { await singleWriter.StopAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
                await singleWriter.DisposeAsync().ConfigureAwait(false);
            }

            if (deleteOnDispose && File.Exists(databasePath))
            {
                try { File.Delete(databasePath); } catch { }
            }

            throw;
        }
    }

    public RepoUri AddOrUpdateText(string relativePath, string content)
    {
        FileSystem.AddOrUpdateText(relativePath, content ?? string.Empty);
        return Track(relativePath);
    }

    public RepoUri AddOrUpdateBytes(string relativePath, byte[] bytes)
    {
        FileSystem.AddOrUpdate(FileSystem.DefaultRoot, Normalize(relativePath), bytes ?? Array.Empty<byte>());
        return Track(relativePath);
    }

    public bool Delete(string relativePath)
    {
        var normalized = Normalize(relativePath);
        var removed = FileSystem.Delete(FileSystem.DefaultRoot, normalized);
        if (removed)
        {
            var uri = RepoUri.Parse($"mem://{FileSystem.DefaultRoot}/{normalized}");
            _trackedUris.TryRemove(uri.AbsoluteUri, out _);
        }

        return removed;
    }

    public async Task IndexAsync(bool skipUnchanged = false, CancellationToken cancellationToken = default)
    {
        if (_trackedUris.IsEmpty)
            return;

        await EnqueueUrisAsync(_trackedUris.Values, skipUnchanged, cancellationToken).ConfigureAwait(false);
        await _coordinator.WaitForIdleAsync(cancellationToken).ConfigureAwait(false);
        RefreshReadStore();
    }

    public async Task ReindexAsync(bool clear = false, CancellationToken cancellationToken = default)
    {
        await foreach (var _ in _coordinator
                           .ReindexAsync(new ReindexRequestOptions(clear), cancellationToken)
                           .ConfigureAwait(false))
        {
        }

        RefreshReadStore();
    }

    public async Task IndexUriAsync(RepoUri uri, bool skipUnchanged = false, CancellationToken cancellationToken = default)
    {
        await EnqueueUrisAsync(new[] { uri }, skipUnchanged, cancellationToken).ConfigureAwait(false);
        await _coordinator.WaitForIdleAsync(cancellationToken).ConfigureAwait(false);
        RefreshReadStore();
    }

    public Task WaitForIdleAsync(CancellationToken cancellationToken = default)
    {
        var task = _coordinator.WaitForIdleAsync(cancellationToken);
        return AwaitAndRefreshAsync(task);
    }

    public Task WaitForStagesIdleAsync(PipelineStage stages, CancellationToken cancellationToken = default)
    {
        if (stages == PipelineStage.None)
            return Task.CompletedTask;

        var mapped = MapStages(stages);
        if (mapped.Count == 0)
            return Task.CompletedTask;

        var waitTask = _coordinator.WaitForPipelineAsync(mapped, waitAll: true, cancellationToken);
        return AwaitAndRefreshAsync(waitTask);
    }

    public RepoUri GetUri(string relativePath)
        => RepoUri.Parse($"mem://{FileSystem.DefaultRoot}/{Normalize(relativePath)}");

    public IReadOnlyCollection<RepoUri> KnownUris => _trackedUris.Values.ToArray();

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;

        try
        {
            await _host.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
        }

        _host.Dispose();

        switch (_analysisWriter)
        {
            case IAsyncDisposable asyncWriter:
                await asyncWriter.DisposeAsync().ConfigureAwait(false);
                break;
            case IDisposable disposableWriter:
                disposableWriter.Dispose();
                break;
        }

        if (_ownsWriter && _singleThreadedWriter is not null)
        {
            try { await _singleThreadedWriter.StopAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
            await _singleThreadedWriter.DisposeAsync().ConfigureAwait(false);
        }
        else
        {
            await _writer.DisposeAsync().ConfigureAwait(false);
        }

        Metrics.Dispose();
        Store.Dispose();

        if (_deleteDatabaseOnDispose && File.Exists(_databasePath))
        {
            try { File.Delete(_databasePath); } catch { }
        }
    }

    private async Task AwaitAndRefreshAsync(Task waitTask)
    {
        await waitTask.ConfigureAwait(false);
        RefreshReadStore();
    }

    private async Task EnqueueUrisAsync(IEnumerable<RepoUri> uris, bool skipUnchanged, CancellationToken cancellationToken)
    {
        var itemOptions = skipUnchanged ? IndexItemOptions.Default : IndexItemOptions.OnlyIfNotExcluded;

        foreach (var uri in uris)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_compositeFileSystem.TryResolve(uri, out var store))
                throw new InvalidOperationException($"No file system registered for URI {uri}.");

            var file = store.GetFile(uri);
            if (!file.Exists)
                continue;

            var artifact = new RawArtifact(file, store);
            await Engine.EnqueueItemAsync(artifact, itemOptions, cancellationToken).ConfigureAwait(false);
        }
    }

    private RepoUri Track(string relativePath)
    {
        var normalized = Normalize(relativePath);
        var uri = RepoUri.Parse($"mem://{FileSystem.DefaultRoot}/{normalized}");
        _trackedUris.AddOrUpdate(uri.AbsoluteUri, uri, static (_, current) => current);
        return uri;
    }

    private void RefreshReadStore()
    {
        var newStore = new DuckDbGraphStore(
            _databasePath,
            Metrics,
            logger: _loggerFactory.CreateLogger<DuckDbGraphStore>(),
            formatSchemaScripts: _formatScripts);
        newStore.EnsureSchema();
        var oldStore = Interlocked.Exchange(ref _store, newStore);
        oldStore.Dispose();
    }

    public async Task<Node?> WaitForDocumentAsync(RepoUri uri, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(2));
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var doc = Store.GetDocumentByUri(uri);
            if (doc is not null)
                return doc;
            if (DateTime.UtcNow >= deadline)
                return null;
            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }
    }

    private static string Normalize(string relativePath)
        => (relativePath ?? string.Empty).Replace('\\', '/').TrimStart('/');

    private static IReadOnlyList<CoordinatorPipelineStage> MapStages(PipelineStage stages)
    {
        var list = new List<CoordinatorPipelineStage>(4);
        if (stages.HasFlag(PipelineStage.Discovery)) list.Add(CoordinatorPipelineStage.Discovery);
        if (stages.HasFlag(PipelineStage.Parsing)) list.Add(CoordinatorPipelineStage.Parsing);
        if (stages.HasFlag(PipelineStage.Analysis)) list.Add(CoordinatorPipelineStage.Analysis);
        if (stages.HasFlag(PipelineStage.Writer)) list.Add(CoordinatorPipelineStage.Writer);
        return list;
    }

    private static string ResolveDatabasePath(IndexedRepoOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.DatabasePath))
        {
            var fullPath = Path.GetFullPath(options.DatabasePath);
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            return fullPath;
        }

        var tempRoot = Path.Combine(Path.GetTempPath(), "repoql-indexed-repo");
        Directory.CreateDirectory(tempRoot);
        return Path.Combine(tempRoot, $"repoql_{Guid.NewGuid():N}.duckdb");
    }

    private sealed class WorkspaceClassifier : IAsyncPipeline<IDiscoveredArtifact, SemanticMediaType?>
    {
        private readonly IFileClassifier _classifier;
        private readonly ILogger<WorkspaceClassifier> _logger;

        public WorkspaceClassifier(IFileClassifier classifier, ILogger<WorkspaceClassifier>? logger)
        {
            _classifier = classifier ?? throw new ArgumentNullException(nameof(classifier));
            _logger = logger ?? NullLogger<WorkspaceClassifier>.Instance;
        }

        public Task<(SemanticMediaType? Result, PipelineResult PipelineStatus)> ProcessAsync(
            IDiscoveredArtifact item,
            CallNextPipeline<IDiscoveredArtifact, SemanticMediaType?> next,
            CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var mediaType = _classifier.GetMediaType(item);
            if (_logger.IsEnabled(LogLevel.Trace))
            {
                _logger.LogTrace("Classified {Uri} as {MediaType}", item.Uri, mediaType);
            }

            return Task.FromResult<(SemanticMediaType?, PipelineResult)>((mediaType, PipelineResult.Success));
        }
    }

    private sealed class FormatRegistryParser : IAsyncPipeline<IClassifiedArtifact, Records?>
    {
        private readonly IFormatRegistry _registry;
        private readonly ILogger<FormatRegistryParser> _logger;

        public FormatRegistryParser(IFormatRegistry registry, ILogger<FormatRegistryParser>? logger)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _logger = logger ?? NullLogger<FormatRegistryParser>.Instance;
        }

        public async Task<(Records? Result, PipelineResult PipelineStatus)> ProcessAsync(
            IClassifiedArtifact item,
            CallNextPipeline<IClassifiedArtifact, Records?> next,
            CancellationToken token)
        {
            token.ThrowIfCancellationRequested();

            var media = item.MediaType;
            if (media is null || !_registry.TryResolveByMedia(media, out var descriptor))
            {
                return await next(item).ConfigureAwait(false);
            }

            try
            {
                var artifact = new DiscoveredArtifact
                {
                    File = item,
                    RepoUri = item.Uri,
                    MediaType = media
                };

                if (!await descriptor.Loader.CanLoadAsync(artifact, token).ConfigureAwait(false))
                {
                    return await next(item).ConfigureAwait(false);
                }

                var document = await descriptor.Loader.LoadAsync(artifact, token).ConfigureAwait(false);
                item["document_model"] = document;

                var materializer = descriptor.Materializer ?? descriptor.Loader as IFormatMaterializer;
                if (materializer is null || !materializer.Supports(document.MediaType))
                {
                    _logger.LogWarning("No materializer available for {MediaType}; skipping {Uri}", document.MediaType, item.Uri);
                    return await next(item).ConfigureAwait(false);
                }

                var records = materializer.Materialize(document);
                return (records, PipelineResult.Success);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to parse {Uri}", item.Uri);
                return (null, PipelineResult.Error);
            }
        }
    }

    private sealed class FormatRegistryAnalyzer : IAsyncPipeline<IParsedArtifact, Annotation[]>
    {
        private readonly IFormatRegistry _registry;
        private readonly IAnalysisWorkspace _workspace;
        private readonly IAnalyzerSettingsProvider? _settingsProvider;
        private readonly string _repositoryRoot;
        private readonly ILogger<FormatRegistryAnalyzer> _logger;

        public FormatRegistryAnalyzer(
            IFormatRegistry registry,
            IAnalysisWorkspace workspace,
            IAnalyzerSettingsProvider? settingsProvider,
            string repositoryRoot,
            ILogger<FormatRegistryAnalyzer>? logger)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
            _settingsProvider = settingsProvider;
            _repositoryRoot = repositoryRoot ?? Directory.GetCurrentDirectory();
            _logger = logger ?? NullLogger<FormatRegistryAnalyzer>.Instance;
        }

        public async Task<(Annotation[]? Result, PipelineResult PipelineStatus)> ProcessAsync(
            IParsedArtifact item,
            CallNextPipeline<IParsedArtifact, Annotation[]> next,
            CancellationToken token)
        {
            token.ThrowIfCancellationRequested();

            var media = item.MediaType;
            if (media is null || !_registry.TryResolveByMedia(media, out var descriptor))
            {
                return await next(item).ConfigureAwait(false);
            }

            if (!item.TryGetValue("document_model", out var documentModel) || documentModel is not DocumentModel document)
            {
                return await next(item).ConfigureAwait(false);
            }

            if (item.Records is null)
            {
                return await next(item).ConfigureAwait(false);
            }

            var documentNode = item.Records.Nodes.FirstOrDefault(n =>
                string.Equals(n.Kind, "document", StringComparison.OrdinalIgnoreCase));
            if (documentNode is null)
            {
                _logger.LogWarning("Document node missing for {Uri}; skipping analyzer", item.Uri);
                return await next(item).ConfigureAwait(false);
            }

            if (!descriptor.Analyzer.Supports(media))
            {
                return await next(item).ConfigureAwait(false);
            }

            try
            {
                var settings = _settingsProvider?.Resolve(item.Uri.AbsoluteUri, media, documentNode)
                    ?? new AnalyzerSettings();
                var context = new AnalyzerContext(settings, _repositoryRoot, _registry, _workspace);
                var annotations = new List<Annotation>();

                await foreach (var result in descriptor.Analyzer.AnalyzeAsync(document, context, token).ConfigureAwait(false))
                {
                    annotations.Add(new Annotation
                    {
                        SemanticKey = result.SemanticKey,
                        Kind = result.Kind,
                        Severity = result.Severity.ToString().ToLowerInvariant(),
                        Source = result.Source,
                        RuleId = result.RuleId,
                        Message = result.Message,
                        Data = result.Data ?? new JsonObject(),
                        ScopeDocumentId = documentNode.Id,
                        TargetNodeId = result.Target?.NodeId,
                        TargetEdgeId = result.Target?.EdgeId,
                        TargetSpanId = result.Target?.SpanId,
                        TargetUri = result.Target?.TargetUri
                    });
                }

                return (annotations.ToArray(), PipelineResult.Success);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Analyzer failed for {Uri}", item.Uri);
                return (null, PipelineResult.Error);
            }
        }
    }
}
