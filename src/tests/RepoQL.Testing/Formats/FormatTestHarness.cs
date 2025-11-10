using Microsoft.Extensions.Logging;
using System.Linq;
using RepoQL.Contracts;
using RepoQL.Contracts.Analysis;
using RepoQL.Contracts.Models;
using RepoQL.Indexing.Indexing.Pipelines;
using RepoQL.Indexing.Indexing.Pipelines.Analysis;
using RepoQL.Indexing.Indexing.Pipelines.Classification;
using RepoQL.Indexing.Indexing.Pipelines.Discovery;
using RepoQL.Indexing.Indexing.Pipelines.Parsing;
using RepoQL.Testing.Indexing;
using RepoQL.Testing.Logging;

namespace RepoQL.Testing.Formats;

/// <summary>
/// Test harness for format processing that encapsulates pipeline construction and execution.
/// Provides a fluent API for testing format handlers end-to-end without wiring the entire IndexingEngine.
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

    public async Task<FormatTestResult> ProcessAsync(IndexItem item, CancellationToken cancellationToken = default)
    {
        var classificationPipeline = CreateClassificationPipeline();
        var parsingPipeline = CreateParsingPipeline();
        var analysisPipeline = CreateAnalysisPipeline();

        var pipelineResult = await RunPipelinesAsync(classificationPipeline, parsingPipeline, analysisPipeline, item, cancellationToken);

        return new FormatTestResult(
            item,
            pipelineResult,
            item.MediaType,
            item.Records,
            ((IAnnotatedArtifact)item).Annotations.ToArray());
    }

    public Task<FormatTestResult> ProcessFileAsync(string filename, string content, CancellationToken cancellationToken = default)
    {
        var item = IndexingTestItemBuilder.ForFile(filename).WithContent(content).Build();
        return ProcessAsync(item, cancellationToken);
    }

    private ClassificationPipeline CreateClassificationPipeline()
    {
        var processors = _classifier is null
            ? Array.Empty<IAsyncPipeline<IDiscoveredArtifact, SemanticMediaType?>>()
            : new[] { _classifier };
        return new ClassificationPipeline(processors, TestLogging.CreateLogger<ClassificationPipeline>());
    }

    private ParsingPipeline CreateParsingPipeline()
    {
        var processors = _parser is null
            ? Array.Empty<IAsyncPipeline<IClassifiedArtifact, Records?>>()
            : new[] { _parser };
        return new ParsingPipeline(processors, TestLogging.CreateLogger<ParsingPipeline>());
    }

    private SingleFileAnalysisPipeline CreateAnalysisPipeline()
    {
        var processors = _analyzer is null
            ? Array.Empty<IAsyncPipeline<IParsedArtifact, Annotation[]>>()
            : new[] { _analyzer };
        return new SingleFileAnalysisPipeline(processors, TestLogging.CreateLogger<SingleFileAnalysisPipeline>());
    }

    private static async Task<PipelineResult> RunPipelinesAsync(
        ClassificationPipeline classifier,
        ParsingPipeline parser,
        SingleFileAnalysisPipeline analyzer,
        IndexItem item,
        CancellationToken cancellationToken)
    {
        var result = await classifier.ProcessItemAsync(item, cancellationToken).ConfigureAwait(false);
        if (result != PipelineResult.Success)
            return result;

        result = await parser.ProcessItemAsync(item, cancellationToken).ConfigureAwait(false);
        if (result != PipelineResult.Success)
            return result;

        return await analyzer.ProcessItemAsync(item, cancellationToken).ConfigureAwait(false);
    }

    public sealed class FormatHarnessBuilder
    {
        private IAsyncPipeline<IDiscoveredArtifact, SemanticMediaType?>? _classifier;
        private IAsyncPipeline<IClassifiedArtifact, Records?>? _parser;
        private IAsyncPipeline<IParsedArtifact, Annotation[]>? _analyzer;
        private Func<AnalyzerContext>? _contextFactory;

        public FormatHarnessBuilder WithClassifier(IAsyncPipeline<IDiscoveredArtifact, SemanticMediaType?> classifier)
        {
            _classifier = classifier;
            return this;
        }

        public FormatHarnessBuilder WithParser(IAsyncPipeline<IClassifiedArtifact, Records?> parser)
        {
            _parser = parser;
            return this;
        }

        public FormatHarnessBuilder WithAnalyzer(IAsyncPipeline<IParsedArtifact, Annotation[]> analyzer)
        {
            _analyzer = analyzer;
            return this;
        }

        public FormatHarnessBuilder WithContextFactory(Func<AnalyzerContext> contextFactory)
        {
            _contextFactory = contextFactory;
            return this;
        }

        public FormatTestHarness Build()
        {
            return new FormatTestHarness(_classifier, _parser, _analyzer, _contextFactory);
        }
    }

    public static FormatHarnessBuilder Create() => new();
}
