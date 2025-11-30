using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using RepoQL.Contracts;
using RepoQL.Contracts.Data;
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
    public IDatabaseWriter? DatabaseWriter { get; set; }
    public IAnalyzerSettingsProvider? SettingsProvider { get; set; }
    public ILoggerFactory? LoggerFactory { get; set; }
    public string? RepositoryRoot { get; set; }
    public string? DatabasePath { get; set; }
    public bool DeleteDatabaseOnDispose { get; set; } = true;
    public bool EnableWatching { get; set; }
    public bool RunFullScanOnStartup { get; set; }
    public IndexingEngineOptions? EngineOptions { get; set; }
    public Func<DuckDbGraphStore, IAnalysisResultWriter?>? CreateAnalysisWriter { get; set; } = store => new AnnotationResultWriter(store);
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
