using FakeItEasy;
using Microsoft.Extensions.Logging;
using RepoQL.Contracts;
using RepoQL.Contracts.Data;
using RepoQL.Contracts.Models;
using RepoQL.FileSystem.Abstractions;
using RepoQL.Indexing.Indexing;
using RepoQL.Indexing.Indexing.Commit;
using RepoQL.Indexing.Indexing.Pipelines;
using RepoQL.Indexing.Indexing.Pipelines.Analysis;
using RepoQL.Indexing.Indexing.Pipelines.Classification;
using RepoQL.Indexing.Indexing.Pipelines.Parsing;
using RepoQL.Indexing.Indexing.PostProcessing;
using RepoQL.Indexing.Indexing.State;
using RepoQL.Testing.Logging;

namespace RepoQL.Testing.Indexing;

public static class IndexingEngineTestFactory
{
    public static IndexingEngineTestContext Create(Action<IndexingEngineTestBuilder>? configure = null)
    {
        var builder = new IndexingEngineTestBuilder();
        configure?.Invoke(builder);
        return builder.Build();
    }

    public static IndexingEngineTestContext CreateIdleEngine(Action<IndexingEngineTestBuilder>? configure = null)
        => Create(configure);
}

public sealed class IndexingEngineTestBuilder
{
    private IDatabaseWriter? _databaseWriter;
    private IUriFilter? _filter;
    private ClassificationPipeline? _classifier;
    private ParsingPipeline? _parser;
    private SingleFileAnalysisPipeline? _singleFileAnalyzer;
    private MultiFileAnalysisPipeline? _multiFileAnalyzer;
    private IndexRebuildPipeline? _indexRebuilder;
    private IDocumentCatalog? _catalog;
    private IIndexingCommitter? _committer;
    private IArtifactPruner? _artifactPruner;
    private IVectorIndexCoordinator? _vectorCoordinator;
    private IndexingEngineOptions? _options;
    private ILogger<IndexingEngine>? _logger;

    public IndexingEngineTestBuilder WithDatabaseWriter(IDatabaseWriter writer)
    {
        _databaseWriter = writer;
        return this;
    }

    public IndexingEngineTestBuilder WithFilter(IUriFilter filter)
    {
        _filter = filter;
        return this;
    }

    public IndexingEngineTestBuilder WithClassifier(ClassificationPipeline classifier)
    {
        _classifier = classifier;
        return this;
    }

    public IndexingEngineTestBuilder WithParser(ParsingPipeline parser)
    {
        _parser = parser;
        return this;
    }

    public IndexingEngineTestBuilder WithSingleFileAnalyzer(SingleFileAnalysisPipeline analyzer)
    {
        _singleFileAnalyzer = analyzer;
        return this;
    }

    public IndexingEngineTestBuilder WithMultiFileAnalyzer(MultiFileAnalysisPipeline analyzer)
    {
        _multiFileAnalyzer = analyzer;
        return this;
    }

    public IndexingEngineTestBuilder WithIndexRebuilder(IndexRebuildPipeline indexRebuilder)
    {
        _indexRebuilder = indexRebuilder;
        return this;
    }

    public IndexingEngineTestBuilder WithCatalog(IDocumentCatalog catalog)
    {
        _catalog = catalog;
        return this;
    }

    public IndexingEngineTestBuilder WithCommitter(IIndexingCommitter committer)
    {
        _committer = committer;
        return this;
    }

    public IndexingEngineTestBuilder WithArtifactPruner(IArtifactPruner pruner)
    {
        _artifactPruner = pruner;
        return this;
    }

    public IndexingEngineTestBuilder WithVectorCoordinator(IVectorIndexCoordinator coordinator)
    {
        _vectorCoordinator = coordinator;
        return this;
    }

    public IndexingEngineTestBuilder WithOptions(IndexingEngineOptions options)
    {
        _options = options;
        return this;
    }

    public IndexingEngineTestBuilder WithLogger(ILogger<IndexingEngine> logger)
    {
        _logger = logger;
        return this;
    }

    internal IndexingEngineTestContext Build()
    {
        var classifier = _classifier ?? A.Fake<ClassificationPipeline>();
        if (_classifier is null)
        {
            A.CallTo(() => classifier.ProcessItemAsync(A<IndexItem>._, A<CancellationToken>._))
                .Returns(Task.FromResult(PipelineResult.Success));
        }

        var parser = _parser ?? A.Fake<ParsingPipeline>();
        if (_parser is null)
        {
            A.CallTo(() => parser.ProcessItemAsync(A<IndexItem>._, A<CancellationToken>._))
                .Returns(Task.FromResult(PipelineResult.Success));
        }

        var singleFileAnalyzer = _singleFileAnalyzer ?? A.Fake<SingleFileAnalysisPipeline>();
        if (_singleFileAnalyzer is null)
        {
            A.CallTo(() => singleFileAnalyzer.ProcessItemAsync(A<IndexItem>._, A<CancellationToken>._))
                .Returns(Task.FromResult(PipelineResult.Success));
        }

        var multiFileAnalyzer = _multiFileAnalyzer ?? A.Fake<MultiFileAnalysisPipeline>(options =>
            options.WithArgumentsForConstructor(() =>
                new MultiFileAnalysisPipeline(Array.Empty<IAsyncPipeline<IAnnotatedArtifact, Annotation[]>>())));
        if (_multiFileAnalyzer is null)
        {
            A.CallTo(() => multiFileAnalyzer.ProcessItemAsync(A<IAnnotatedArtifact>._, A<CancellationToken>._))
                .Returns(Task.FromResult(PipelineResult.Success));
        }

        var indexRebuilder = _indexRebuilder ?? A.Fake<IndexRebuildPipeline>(options =>
            options.WithArgumentsForConstructor(() =>
                new IndexRebuildPipeline(Array.Empty<IAsyncPipeline<IAnnotatedArtifact, string>>())));
        if (_indexRebuilder is null)
        {
            A.CallTo(() => indexRebuilder.ProcessItemAsync(A<IAnnotatedArtifact>._, A<CancellationToken>._))
                .Returns(Task.FromResult(PipelineResult.Success));
        }

        var filter = _filter ?? A.Fake<IUriFilter>();
        if (_filter is null)
        {
            A.CallTo(() => filter.IncludeFile(A<RepoUri>._)).Returns(false);
        }

        var catalog = _catalog ?? new DocumentCatalog(NullDocumentCatalogDataSource.Instance);
        var committer = _committer ?? A.Fake<IIndexingCommitter>();
        if (_committer is null)
        {
            A.CallTo(() => committer.CommitAsync(A<IndexItem>._, A<CancellationToken>._))
                .Returns(Task.CompletedTask);
        }

        var artifactPruner = _artifactPruner ?? NullArtifactPruner.Instance;
        var vectorCoordinator = _vectorCoordinator ?? NullVectorIndexCoordinator.Instance;
        var options = _options ?? new IndexingEngineOptions
        {
            IndexingQueueSize = 32,
            IndexingWorkers = 1,
            AnalysisQueueSize = 32,
            AnalysisWorkers = 1
        };
        var logger = _logger ?? TestLogging.CreateLogger<IndexingEngine>();

        var engine = new IndexingEngine(
            databaseWriter: _databaseWriter,
            filter: filter,
            classifier: classifier,
            parser: parser,
            singleFileAnalyzer: singleFileAnalyzer,
            multiFileAnalyzer: multiFileAnalyzer,
            indexRebuilder: indexRebuilder,
            documentCatalog: catalog,
            committer: committer,
            artifactPruner: artifactPruner,
            vectorCoordinator: vectorCoordinator,
            options: options,
            logger: logger);

        return new IndexingEngineTestContext(
            engine,
            classifier,
            parser,
            singleFileAnalyzer,
            multiFileAnalyzer,
            indexRebuilder,
            filter,
            catalog,
            committer,
            _databaseWriter,
            artifactPruner,
            vectorCoordinator,
            options,
            logger);
    }
}

public sealed class IndexingEngineTestContext
{
    internal IndexingEngineTestContext(
        IndexingEngine engine,
        ClassificationPipeline classifier,
        ParsingPipeline parser,
        SingleFileAnalysisPipeline singleFileAnalyzer,
        MultiFileAnalysisPipeline multiFileAnalyzer,
        IndexRebuildPipeline indexRebuilder,
        IUriFilter filter,
        IDocumentCatalog catalog,
        IIndexingCommitter committer,
        IDatabaseWriter? writer,
        IArtifactPruner artifactPruner,
        IVectorIndexCoordinator vectorCoordinator,
        IndexingEngineOptions options,
        ILogger<IndexingEngine> logger)
    {
        Engine = engine;
        Classifier = classifier;
        Parser = parser;
        SingleFileAnalyzer = singleFileAnalyzer;
        MultiFileAnalyzer = multiFileAnalyzer;
        IndexRebuilder = indexRebuilder;
        Filter = filter;
        Catalog = catalog;
        Committer = committer;
        Writer = writer;
        ArtifactPruner = artifactPruner;
        VectorCoordinator = vectorCoordinator;
        Options = options;
        Logger = logger;
    }

    public IndexingEngine Engine { get; }
    public ClassificationPipeline Classifier { get; }
    public ParsingPipeline Parser { get; }
    public SingleFileAnalysisPipeline SingleFileAnalyzer { get; }
    public MultiFileAnalysisPipeline MultiFileAnalyzer { get; }
    public IndexRebuildPipeline IndexRebuilder { get; }
    public IUriFilter Filter { get; }
    public IDocumentCatalog Catalog { get; }
    public IIndexingCommitter Committer { get; }
    public IDatabaseWriter? Writer { get; }
    public IArtifactPruner ArtifactPruner { get; }
    public IVectorIndexCoordinator VectorCoordinator { get; }
    public IndexingEngineOptions Options { get; }
    public ILogger<IndexingEngine> Logger { get; }
}
