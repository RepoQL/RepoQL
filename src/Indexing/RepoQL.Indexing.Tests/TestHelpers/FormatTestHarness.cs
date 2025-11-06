using Microsoft.Extensions.Logging;
using RepoQL.Contracts;
using RepoQL.Contracts.Analysis;
using RepoQL.Contracts.Models;
using RepoQL.Indexing.Indexing;
using RepoQL.Indexing.Indexing.Pipelines;
using RepoQL.Indexing.Indexing.Pipelines.Analysis;
using RepoQL.Indexing.Indexing.Pipelines.Classification;
using RepoQL.Indexing.Indexing.Pipelines.Discovery;
using RepoQL.Indexing.Indexing.Pipelines.Parsing;

namespace RepoQL.Indexing.Tests.TestHelpers;

/// <summary>
/// Test harness for format processing that encapsulates pipeline construction and execution.
/// Provides a fluent API for testing format handlers end-to-end.
/// </summary>
public sealed class FormatTestHarness
{
    private readonly IAsyncPipeline<IDiscoveredArtifact, SemanticMediaType?>? _classifier;
    private readonly IAsyncPipeline<IClassifiedArtifact, Records?>? _parser;
    private readonly IAsyncPipeline<IParsedArtifact, Annotation[]>? _analyzer;
    private readonly Func<AnalyzerContext>? _contextFactory;

    private FormatTestHarness(
        IAsyncPipeline<IDiscoveredArtifact, SemanticMediaType?>? classifier,
        IAsyncPipeline<IClassifiedArtifact, Records?>? parser,
        IAsyncPipeline<IParsedArtifact, Annotation[]>? analyzer,
        Func<AnalyzerContext>? contextFactory)
    {
        _classifier = classifier;
        _parser = parser;
        _analyzer = analyzer;
        _contextFactory = contextFactory;
    }

    /// <summary>
    /// Processes an IndexItem through the configured pipeline.
    /// </summary>
    public async Task<FormatTestResult> ProcessAsync(IndexItem item, CancellationToken cancellationToken = default)
    {
        var engine = CreateEngine();
        var pipelineResult = await engine.ApplyIndexerPipeline(item, cancellationToken);

        return new FormatTestResult(
            item,
            pipelineResult,
            item.MediaType,
            item.Records,
            item.AnnotationsList.ToArray());
    }

    /// <summary>
    /// Processes a file through the configured pipeline.
    /// </summary>
    public async Task<FormatTestResult> ProcessFileAsync(string filename, string content, CancellationToken cancellationToken = default)
    {
        var item = TestItemBuilder.ForFile(filename).WithContent(content).Build();
        return await ProcessAsync(item, cancellationToken);
    }

    private IndexingEngine CreateEngine()
    {
        var classificationProcessors = _classifier != null
            ? new[] { _classifier }
            : Array.Empty<IAsyncPipeline<IDiscoveredArtifact, SemanticMediaType?>>();

        var parsingProcessors = _parser != null
            ? new[] { _parser }
            : Array.Empty<IAsyncPipeline<IClassifiedArtifact, Records?>>();

        var analysisProcessors = _analyzer != null
            ? new[] { _analyzer }
            : Array.Empty<IAsyncPipeline<IParsedArtifact, Annotation[]>>();

        var classificationPipeline = new ClassificationPipeline(classificationProcessors, CreateLogger<ClassificationPipeline>());
        var parsingPipeline = new ParsingPipeline(parsingProcessors, CreateLogger<ParsingPipeline>());
        var analysisPipeline = new SingleFileAnalysisPipeline(analysisProcessors, CreateLogger<SingleFileAnalysisPipeline>());

        return new IndexingEngine(
            databaseWriter: null,
            filter: null,
            classifier: classificationPipeline,
            parser: parsingPipeline,
            singleFileAnalyzer: analysisPipeline,
            logger: CreateLogger<IndexingEngine>());
    }

    private static ILogger<T> CreateLogger<T>()
    {
        var tunitLogger = TestContext.Current!.GetDefaultLogger();
        var factory = Microsoft.Extensions.Logging.LoggerFactory.Create(builder =>
            builder.AddProvider(new TUnitLoggerProvider(tunitLogger)));
        return factory.CreateLogger<T>();
    }

    /// <summary>
    /// Builder for creating a FormatTestHarness with specific processors.
    /// </summary>
    public sealed class Builder
    {
        private IAsyncPipeline<IDiscoveredArtifact, SemanticMediaType?>? _classifier;
        private IAsyncPipeline<IClassifiedArtifact, Records?>? _parser;
        private IAsyncPipeline<IParsedArtifact, Annotation[]>? _analyzer;
        private Func<AnalyzerContext>? _contextFactory;

        /// <summary>
        /// Configures the classifier processor.
        /// </summary>
        public Builder WithClassifier(IAsyncPipeline<IDiscoveredArtifact, SemanticMediaType?> classifier)
        {
            _classifier = classifier;
            return this;
        }

        /// <summary>
        /// Configures the parser processor.
        /// </summary>
        public Builder WithParser(IAsyncPipeline<IClassifiedArtifact, Records?> parser)
        {
            _parser = parser;
            return this;
        }

        /// <summary>
        /// Configures the analyzer processor.
        /// </summary>
        public Builder WithAnalyzer(IAsyncPipeline<IParsedArtifact, Annotation[]> analyzer)
        {
            _analyzer = analyzer;
            return this;
        }

        /// <summary>
        /// Configures the analyzer context factory.
        /// </summary>
        public Builder WithContextFactory(Func<AnalyzerContext> contextFactory)
        {
            _contextFactory = contextFactory;
            return this;
        }

        /// <summary>
        /// Builds the harness with the configured processors.
        /// </summary>
        public FormatTestHarness Build()
        {
            return new FormatTestHarness(_classifier, _parser, _analyzer, _contextFactory);
        }
    }

    /// <summary>
    /// Creates a new builder for constructing a test harness.
    /// </summary>
    public static Builder Create() => new Builder();
}
