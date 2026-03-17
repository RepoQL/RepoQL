using System.Collections.Concurrent;
using System.Text.Json.Nodes;
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
using RepoQL.Indexing;
using RepoQL.Metrics;
using MetricsIndexingMetrics = RepoQL.Metrics.IndexingMetrics;

namespace RepoQL.Testing.Scaffolding;

/// <summary>
/// Builds an in-memory repository backed by DuckDB storage and the new RepoQL indexing stack for tests.
/// </summary>
public sealed class IndexedRepoBuilder : IAsyncDisposable
{
    private readonly CompositeFileSystem _compositeFileSystem;
    private readonly IndexingCoordinator _coordinator;
    private readonly RepoqlHost _host;
    private readonly IAnalysisResultWriter? _analysisWriter;
    private readonly bool _deleteDatabaseOnDispose;
    private readonly string _databasePath;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IReadOnlyList<FormatSqlScript> _formatScripts;
    private DuckDbDataStore _dataStore;
    private readonly ConcurrentDictionary<string, RepoUri> _trackedUris = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    private IndexedRepoBuilder(
        MemoryFileSystem fileSystem,
        CompositeFileSystem composite,
        DuckDbDataStore dataStore,
        MetricsIndexingMetrics metrics,
        FormatRegistry? formatRegistry,
        IndexingEngine engine,
        IndexingCoordinator coordinator,
        RepoqlHost host,
        IHasher hasher,
        IAnalysisResultWriter? analysisWriter,
        string databasePath,
        bool deleteDatabaseOnDispose,
        ILoggerFactory loggerFactory,
        IReadOnlyList<FormatSqlScript> formatScripts)
    {
        FileSystem = fileSystem;
        _compositeFileSystem = composite;
        _dataStore = dataStore;
        Metrics = metrics;
        FormatRegistry = formatRegistry;
        Engine = engine;
        _coordinator = coordinator;
        _host = host;
        Hasher = hasher;
        _analysisWriter = analysisWriter;
        _databasePath = databasePath;
        _deleteDatabaseOnDispose = deleteDatabaseOnDispose;
        _loggerFactory = loggerFactory;
        _formatScripts = formatScripts;
    }

    public MemoryFileSystem FileSystem { get; }

    public DuckDbDataStore DataStore => _dataStore;

    /// <summary>
    /// Alias for DataStore for backward compatibility with tests.
    /// </summary>
    public DuckDbDataStore Store => _dataStore;

    public FormatRegistry? FormatRegistry { get; }

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

        // Require at least one format source (modern parsers or legacy descriptors)
        if (options.Formats.Count == 0 && options.Parsers.Count == 0)
            throw new InvalidOperationException("IndexedRepoOptions must register at least one parser or format descriptor.");

        var hasher = options.ResolveHasher();
        var classifier = options.ResolveClassifier();
        var fileSystem = new MemoryFileSystem(options.Root);
        var composite = new CompositeFileSystem(
            CompositeFileSystemMount.CreatePrimary(fileSystem),
            options.AdditionalMounts);
        var metrics = new MetricsIndexingMetrics();
        var formatRegistry = options.Formats.Count > 0 ? new FormatRegistry(options.Formats) : null;
        var loggerFactory = options.LoggerFactory ?? NullLoggerFactory.Instance;
        var repositoryRoot = string.IsNullOrWhiteSpace(options.RepositoryRoot)
            ? Directory.GetCurrentDirectory()
            : options.RepositoryRoot;

        var databasePath = ResolveDatabasePath(options);
        var deleteOnDispose = options.DeleteDatabaseOnDispose || string.IsNullOrWhiteSpace(options.DatabasePath);

        DuckDbDataStore? database = null;
        IAnalysisResultWriter? analysisWriter = null;
        RepoqlHost? host = null;
        IndexingCoordinator? coordinator = null;
        IndexingEngine? engine = null;

        try
        {
            // Collect format scripts from both modern schema providers and legacy loaders
            var formatScripts = options.SchemaProviders
                .SelectMany(p => p.GetSchemaScripts())
                .Concat(
                    (formatRegistry?.Formats ?? Enumerable.Empty<FormatDescriptor>())
                        .Select(f => f.Loader)
                        .OfType<IFormatSchemaProvider>()
                        .SelectMany(p => p.GetSchemaScripts()))
                .Where(s => !string.IsNullOrWhiteSpace(s.Sql))
                .ToList();

            // Create unified database interface for indexing operations
            var serviceProvider = options.ResolveServiceProvider();
            database = new DuckDbDataStore(
                databasePath,
                embeddingProvider: null,
                formatSchemaScripts: formatScripts,
                logger: loggerFactory.CreateLogger<DuckDbDataStore>(),
                serviceProvider: serviceProvider);

            analysisWriter = options.CreateAnalysisWriter?.Invoke(database);

            var artifactPruner = new StorageBackedArtifactPruner(
                database,
                () => coordinator?.IsReindexing ?? false,
                loggerFactory.CreateLogger<StorageBackedArtifactPruner>());
            var catalog = new DocumentCatalog(NullDocumentCatalogDataSource.Instance);
            var committer = new IndexingCommitter(
                database,
                catalog,
                loggerFactory.CreateLogger<IndexingCommitter>());

            var classificationPipeline = new ClassificationPipeline(
                new[]
                {
                    new WorkspaceClassifier(classifier, loggerFactory.CreateLogger<WorkspaceClassifier>())
                },
                loggerFactory.CreateLogger<ClassificationPipeline>());

            // Build parsing pipeline: modern parsers first, then legacy FormatRegistryParser
            var parsingProcessors = new List<IAsyncPipeline<IClassifiedArtifact, Records?>>();
            parsingProcessors.AddRange(options.Parsers);
            if (formatRegistry is not null)
            {
                parsingProcessors.Add(new FormatRegistryParser(formatRegistry, loggerFactory.CreateLogger<FormatRegistryParser>()));
            }
            var parsingPipeline = new ParsingPipeline(
                parsingProcessors,
                loggerFactory.CreateLogger<ParsingPipeline>());

            // Build analysis pipeline: modern analyzers first, then legacy FormatRegistryAnalyzer
            var analysisProcessors = new List<IAsyncPipeline<IParsedArtifact, Annotation[]>>();
            analysisProcessors.AddRange(options.SingleFileAnalyzers);
            if (formatRegistry is not null)
            {
                analysisProcessors.Add(new FormatRegistryAnalyzer(
                    formatRegistry,
                    options.SettingsProvider,
                    repositoryRoot,
                    loggerFactory.CreateLogger<FormatRegistryAnalyzer>()));
            }
            var singleFilePipeline = new SingleFileAnalysisPipeline(
                analysisProcessors,
                loggerFactory.CreateLogger<SingleFileAnalysisPipeline>());

            var multiFilePipeline = new MultiFileAnalysisPipeline(
                Array.Empty<IAsyncPipeline<IAnnotatedArtifact, Annotation[]>>(),
                loggerFactory.CreateLogger<MultiFileAnalysisPipeline>());

            var indexRebuildPipeline = new IndexRebuildPipeline(
                Array.Empty<IAsyncPipeline<IAnnotatedArtifact, string>>(),
                loggerFactory.CreateLogger<IndexRebuildPipeline>());

            engine = new IndexingEngine(
                database,
                options.Filter,
                classificationPipeline,
                parsingPipeline,
                singleFilePipeline,
                multiFilePipeline,
                indexRebuildPipeline,
                catalog,
                committer,
                artifactPruner,
                NullEmbeddingCoordinator.Instance,
                options.EngineOptions,
                loggerFactory.CreateLogger<IndexingEngine>());

            coordinator = new IndexingCoordinator(
                composite,
                engine,
                database,
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
                database,
                metrics,
                formatRegistry,
                engine,
                coordinator,
                host,
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

            database?.Dispose();
            metrics.Dispose();

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
    }

    public async Task ReindexAsync(bool clear = false, CancellationToken cancellationToken = default)
    {
        await foreach (var _ in _coordinator
                           .ReindexAsync(new ReindexRequestOptions(clear), cancellationToken)
                           .ConfigureAwait(false))
        {
        }
    }

    public async Task IndexUriAsync(RepoUri uri, bool skipUnchanged = false, CancellationToken cancellationToken = default)
    {
        await EnqueueUrisAsync(new[] { uri }, skipUnchanged, cancellationToken).ConfigureAwait(false);
        await _coordinator.WaitForIdleAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task WaitForIdleAsync(CancellationToken cancellationToken = default)
    {
        return _coordinator.WaitForIdleAsync(cancellationToken);
    }

    public Task WaitForStagesIdleAsync(PipelineStage stages, CancellationToken cancellationToken = default)
    {
        if (stages == PipelineStage.None)
            return Task.CompletedTask;

        var mapped = MapStages(stages);
        if (mapped.Count == 0)
            return Task.CompletedTask;

        return _coordinator.WaitForPipelineAsync(mapped, waitAll: true, cancellationToken);
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

        _dataStore.Dispose();
        Metrics.Dispose();

        if (_deleteDatabaseOnDispose && File.Exists(_databasePath))
        {
            try { File.Delete(_databasePath); } catch { }
        }
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

    public async Task<Node?> WaitForDocumentAsync(RepoUri uri, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(2));
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var doc = _dataStore.GetDocumentByUri(uri);
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
        private readonly IAnalyzerSettingsProvider? _settingsProvider;
        private readonly string _repositoryRoot;
        private readonly ILogger<FormatRegistryAnalyzer> _logger;

        public FormatRegistryAnalyzer(
            IFormatRegistry registry,
            IAnalyzerSettingsProvider? settingsProvider,
            string repositoryRoot,
            ILogger<FormatRegistryAnalyzer>? logger)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
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
                var context = new AnalyzerContext(settings, _repositoryRoot);
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
