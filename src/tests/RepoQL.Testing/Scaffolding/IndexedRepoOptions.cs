using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RepoQL.Contracts;
using RepoQL.Contracts.Data;
using RepoQL.Contracts.Embeddings;
using RepoQL.Contracts.Models;
using RepoQL.Core;
using RepoQL.Core.Analysis;
using RepoQL.Data.DuckDB;
using RepoQL.FileSystem;
using RepoQL.FileSystem.Abstractions;
using RepoQL.Indexing.FileSystems;
using RepoQL.Indexing.Indexing;
using RepoQL.Indexing.Indexing.Pipelines;
using RepoQL.Indexing.Indexing.Pipelines.Classification;
using RepoQL.Indexing.Indexing.Pipelines.Parsing;

namespace RepoQL.Testing.Scaffolding;

public sealed class IndexedRepoOptions
{
    private const string DefaultMeterName = "RepoQL.Tests.IndexedRepo";

    public string Root { get; set; } = "repo";
    public string MeterName { get; set; } = DefaultMeterName;
    public IFileClassifier? Classifier { get; set; }
    public IHasher? Hasher { get; set; }
    public IUriFilter Filter { get; set; } = new NoOpUriFilter();
    public IAnalyzerSettingsProvider? SettingsProvider { get; set; }
    public ILoggerFactory? LoggerFactory { get; set; }
    public string? RepositoryRoot { get; set; }
    public string? DatabasePath { get; set; }
    public bool DeleteDatabaseOnDispose { get; set; } = true;
    public bool EnableWatching { get; set; }
    public bool RunFullScanOnStartup { get; set; }
    public IndexingEngineOptions? EngineOptions { get; set; }
    public Func<DuckDbDataStore, IAnalysisResultWriter?>? CreateAnalysisWriter { get; set; } = db => new AnnotationResultWriter(db);

    /// <summary>
    /// Service provider for UDF dependencies. If null, a default test provider is created.
    /// </summary>
    public IServiceProvider? ServiceProvider { get; set; }

    public IList<FormatDescriptor> Formats { get; } = new List<FormatDescriptor>();
    public IList<CompositeFileSystemMount> AdditionalMounts { get; } = new List<CompositeFileSystemMount>();

    /// <summary>
    /// Modern pipeline-based parsers. These are used instead of FormatDescriptor.Loader/Materializer.
    /// </summary>
    public IList<IAsyncPipeline<IClassifiedArtifact, Records?>> Parsers { get; } = new List<IAsyncPipeline<IClassifiedArtifact, Records?>>();

    /// <summary>
    /// Modern pipeline-based single-file analyzers.
    /// </summary>
    public IList<IAsyncPipeline<IParsedArtifact, Annotation[]>> SingleFileAnalyzers { get; } = new List<IAsyncPipeline<IParsedArtifact, Annotation[]>>();

    /// <summary>
    /// Schema providers for SQL view registration.
    /// </summary>
    public IList<IFormatSchemaProvider> SchemaProviders { get; } = new List<IFormatSchemaProvider>();

    /// <summary>
    /// Media type to file extension mappings for classification.
    /// </summary>
    public IDictionary<string, SemanticMediaType> ExtensionMappings { get; } = new Dictionary<string, SemanticMediaType>(StringComparer.OrdinalIgnoreCase);

    public void AddFormat(FormatDescriptor descriptor)
        => Formats.Add(descriptor);

    public void AddParser(IAsyncPipeline<IClassifiedArtifact, Records?> parser)
        => Parsers.Add(parser);

    public void AddAnalyzer(IAsyncPipeline<IParsedArtifact, Annotation[]> analyzer)
        => SingleFileAnalyzers.Add(analyzer);

    public void AddSchemaProvider(IFormatSchemaProvider provider)
        => SchemaProviders.Add(provider);

    public void MapExtension(string extension, SemanticMediaType mediaType)
        => ExtensionMappings[extension.TrimStart('.')] = mediaType;

    internal IFileClassifier ResolveClassifier()
    {
        if (Classifier is not null)
            return Classifier;

        var byLabel = new Dictionary<string, SemanticMediaType>(StringComparer.OrdinalIgnoreCase);

        // Add modern extension mappings first
        foreach (var (ext, mediaType) in ExtensionMappings)
        {
            byLabel.TryAdd(ext.TrimStart('.'), mediaType);
        }

        // Add legacy FormatDescriptor labels
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

    internal IServiceProvider ResolveServiceProvider()
        => ServiceProvider ?? CreateDefaultTestServiceProvider();

    private static IServiceProvider CreateDefaultTestServiceProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new RepositoryConfiguration { Path = Environment.CurrentDirectory });
        services.AddSingleton<UriRegistry>();
        services.AddSingleton<IEmbeddingProvider>(new DisabledTestEmbeddingProvider());
        services.AddSingleton<ILlmProvider>(new DisabledTestLlmProvider());
        services.AddSingleton<IMcpToolCaller?>(_ => null);
        return services.BuildServiceProvider();
    }

    private sealed class DisabledTestEmbeddingProvider : IEmbeddingProvider
    {
        public bool Enabled => false;
        public string Model => "test-disabled";
        public int Dimension => 384;
        public Task<float[]?> EmbedQueryAsync(string text, CancellationToken ct = default) => Task.FromResult<float[]?>(null);
        public Task<float[]?> EmbedPassageAsync(string text, CancellationToken ct = default) => Task.FromResult<float[]?>(null);
        public Task<float[]?[]> EmbedQueryBatchAsync(IReadOnlyList<string>? texts, CancellationToken ct = default)
            => Task.FromResult(texts?.Select(_ => (float[]?)null).ToArray() ?? []);
        public Task<float[]?[]> EmbedPassageBatchAsync(IReadOnlyList<string>? texts, CancellationToken ct = default)
            => Task.FromResult(texts?.Select(_ => (float[]?)null).ToArray() ?? []);
        public Task<float[]?[]> EmbedPassageBatchAsync(IReadOnlyList<string>? texts, BatchEmbeddingProgress progress, CancellationToken ct = default)
            => Task.FromResult(texts?.Select(_ => (float[]?)null).ToArray() ?? []);
    }

    private sealed class DisabledTestLlmProvider : ILlmProvider
    {
        public bool Enabled => false;
        public string Model => "test-disabled";
        public Task<string> SummarizeAsync(string jsonData, string intent, int maxTokens = 500, string? repoTree = null, CancellationToken ct = default)
            => Task.FromResult("LLM disabled in tests");
        public Task<LlmSummaryResult> SummarizeWithReasoningAsync(string jsonData, string intent, int maxTokens = 500, string? repoTree = null, CancellationToken ct = default)
            => Task.FromResult(new LlmSummaryResult("LLM disabled in tests"));
        public Task<string> ExtractAsync(string jsonData, string intent, Func<string, int, string> readUri, CancellationToken ct = default)
            => Task.FromResult("LLM disabled in tests");
        public Task<string> ExtractKeywordsAsync(string question, CancellationToken ct = default)
            => Task.FromResult(string.Empty);
    }

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
