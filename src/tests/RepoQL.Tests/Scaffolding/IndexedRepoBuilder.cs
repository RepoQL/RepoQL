using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using RepoQL.Contracts;
using RepoQL.Contracts.Data;
using RepoQL.Core;
using RepoQL.Core.Analysis;
using RepoQL.Metrics;
using RepoQL.Data.DuckDB;
using RepoQL.FileSystem;
using RepoQL.FileSystem.Abstractions;
using RepoQL.FileSystem.InMemory;

namespace RepoQL.Tests.Scaffolding;

/// <summary>
/// Builds an in-memory repository backed by DuckDB storage and RepositoryIndexer for tests.
/// </summary>
internal sealed class IndexedRepoBuilder : IAsyncDisposable
{
    private readonly IndexedRepoOptions _options;
    private readonly MultiFileSystem _hub;
    private readonly IAnalysisResultWriter? _analysisWriter;
    private readonly ConcurrentDictionary<string, RepoUri> _trackedUris = new(StringComparer.OrdinalIgnoreCase);

    private IndexedRepoBuilder(
        IndexedRepoOptions options,
        MemoryFileSystem fileSystem,
        MultiFileSystem hub,
        DuckDbGraphStore store,
        IndexingMetrics metrics,
        Meter meter,
        FormatRegistry formatRegistry,
        AnalysisWorkspace workspace,
        RepositoryIndexer indexer,
        IHasher hasher,
        IAnalysisResultWriter? analysisWriter)
    {
        _options = options;
        FileSystem = fileSystem;
        _hub = hub;
        Store = store;
        Metrics = metrics;
        Meter = meter;
        FormatRegistry = formatRegistry;
        Workspace = workspace;
        Indexer = indexer;
        Hasher = hasher;
        _analysisWriter = analysisWriter;
    }

    public MemoryFileSystem FileSystem { get; }
    public DuckDbGraphStore Store { get; }
    public FormatRegistry FormatRegistry { get; }
    public AnalysisWorkspace Workspace { get; }
    public RepositoryIndexer Indexer { get; }
    public IndexingMetrics Metrics { get; }
    public Meter Meter { get; }
    public IHasher Hasher { get; }
    public IMultiFileSystem FileHub => _hub;

    public static async Task<IndexedRepoBuilder> CreateAsync(
        Action<IndexedRepoOptions>? configure = null,
        CancellationToken cancellationToken = default)
    {
        var options = new IndexedRepoOptions();
        configure?.Invoke(options);

        if (options.Formats.Count == 0)
            throw new InvalidOperationException("IndexedRepoOptions must register at least one format descriptor.");

        var fileSystem = new MemoryFileSystem(options.Root);
        var registry = new FileSystemRegistry([fileSystem]);
        var hub = new MultiFileSystem(registry, [fileSystem]);
        var metrics = new IndexingMetrics();
        var store = options.CreateStore(metrics);
        var meter = new Meter(options.MeterName);
        var hasher = options.ResolveHasher();
        var classifier = options.ResolveClassifier();
        var formatRegistry = new FormatRegistry(options.Formats);
        var workspace = new AnalysisWorkspace(hub, classifier, hasher, formatRegistry);
        var analysisWriter = options.CreateAnalysisWriter?.Invoke(store);

        var indexer = new RepositoryIndexer(
            hub,
            store,
            classifier,
            formatRegistry,
            workspace,
            options.Filter,
            hasher,
            options.DatabaseWriter,
            analysisWriter: analysisWriter,
            settingsProvider: options.SettingsProvider,
            repositoryRoot: options.RepositoryRoot,
            logger: options.Logger);

        try
        {
            await indexer.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await indexer.DisposeAsync().ConfigureAwait(false);
            if (analysisWriter is IAsyncDisposable asyncWriter)
                await asyncWriter.DisposeAsync().ConfigureAwait(false);
            else if (analysisWriter is IDisposable disposableWriter)
                disposableWriter.Dispose();
            metrics.Dispose();
            meter.Dispose();
            store.Dispose();
            throw;
        }

        return new IndexedRepoBuilder(
            options,
            fileSystem,
            hub,
            store,
            metrics,
            meter,
            formatRegistry,
            workspace,
            indexer,
            hasher,
            analysisWriter);
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

        await Indexer.QueueForIndexingAsync(_trackedUris.Values, skipUnchanged).ConfigureAwait(false);
        await Indexer.WaitForIdle(cancellationToken).ConfigureAwait(false);
    }

    public async Task IndexUriAsync(RepoUri uri, bool skipUnchanged = false, CancellationToken cancellationToken = default)
    {
        await Indexer.QueueForIndexingAsync([uri], skipUnchanged).ConfigureAwait(false);
        await Indexer.WaitForIdle(cancellationToken).ConfigureAwait(false);
    }

    public Task WaitForIdleAsync(CancellationToken cancellationToken = default)
        => Indexer.WaitForIdle(cancellationToken);

    public Task WaitForStagesIdleAsync(PipelineStage stages, CancellationToken cancellationToken = default)
        => Indexer.WaitForStagesIdleAsync(stages, cancellationToken);

    public RepoUri GetUri(string relativePath)
        => RepoUri.Parse($"mem://{FileSystem.DefaultRoot}/{Normalize(relativePath)}");

    public IReadOnlyCollection<RepoUri> KnownUris => _trackedUris.Values.ToArray();

    public async ValueTask DisposeAsync()
    {
        await Indexer.DisposeAsync().ConfigureAwait(false);
        if (_options.DatabaseWriter is not null)
            await _options.DatabaseWriter.DisposeAsync().ConfigureAwait(false);
        Metrics.Dispose();
        Meter.Dispose();
        Store.Dispose();
        switch (_analysisWriter)
        {
            case IAsyncDisposable asyncDisposable:
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                break;
            case IDisposable disposable:
                disposable.Dispose();
                break;
        }
    }

    private RepoUri Track(string relativePath)
    {
        var normalized = Normalize(relativePath);
        var uri = RepoUri.Parse($"mem://{FileSystem.DefaultRoot}/{normalized}");
        _trackedUris.AddOrUpdate(uri.AbsoluteUri, uri, static (_, current) => current);
        return uri;
    }

    private static string Normalize(string relativePath)
        => (relativePath ?? string.Empty).Replace('\\', '/').TrimStart('/');
}

internal sealed class IndexedRepoOptions
{
    private const string DefaultMeterName = "RepoQL.Tests.IndexedRepo";

    public string Root { get; set; } = "repo";
    public string MeterName { get; set; } = DefaultMeterName;
    public IFileClassifier? Classifier { get; set; }
    public IHasher? Hasher { get; set; }
    public IUriFilter Filter { get; set; } = new NoOpUriFilter();
    public IDatabaseWriter? DatabaseWriter { get; set; }
    public IAnalyzerSettingsProvider? SettingsProvider { get; set; }
    public ILogger<RepositoryIndexer>? Logger { get; set; }
    public string? RepositoryRoot { get; set; }
    public Func<IndexingMetrics, DuckDbGraphStore>? StoreFactory { get; set; }
    public Func<DuckDbGraphStore, IAnalysisResultWriter?>? CreateAnalysisWriter { get; set; } = store => new AnnotationResultWriter(store);
    public IList<FormatDescriptor> Formats { get; } = new List<FormatDescriptor>();

    public void AddFormat(FormatDescriptor descriptor)
        => Formats.Add(descriptor);

    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope")]
    internal DuckDbGraphStore CreateStore(IndexingMetrics metrics)
        => StoreFactory?.Invoke(metrics) ?? new DuckDbGraphStore(":memory:", metrics);

    internal IFileClassifier ResolveClassifier()
    {
        if (Classifier is not null)
            return Classifier;

        var byLabel = new Dictionary<string, SemanticMediaType>(StringComparer.OrdinalIgnoreCase);
        foreach (var descriptor in Formats)
        {
            foreach (var label in descriptor.Labels)
            {
                if (string.IsNullOrWhiteSpace(label))
                    continue;
                byLabel.TryAdd(label.TrimStart('.'), descriptor.MediaType);
            }
        }

        return new LabelClassifier(byLabel);
    }

    internal IHasher ResolveHasher()
        => Hasher ?? new XxHasher();

    private sealed class LabelClassifier : IFileClassifier
    {
        private readonly IReadOnlyDictionary<string, SemanticMediaType> _byLabel;

        public LabelClassifier(IReadOnlyDictionary<string, SemanticMediaType> byLabel)
        {
            _byLabel = byLabel;
        }

        public SemanticMediaType GetMediaType(Microsoft.Extensions.FileProviders.IFileInfo fileInfo)
        {
            var name = fileInfo.Name ?? string.Empty;
            var ext = Path.GetExtension(name).TrimStart('.');
            if (!string.IsNullOrWhiteSpace(ext) && _byLabel.TryGetValue(ext, out var media))
                return media;
            if (_byLabel.TryGetValue(name, out var mediaByName))
                return mediaByName;
            return SemanticMediaType.Create("text", "plain").WithKind("plain.document");
        }
    }
}
