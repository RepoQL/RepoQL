using AwesomeAssertions;
using FakeItEasy;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using RepoQL.Contracts;
using RepoQL.FileSystem.Abstractions;
using RepoQL.Indexing.Indexing;
using RepoQL.Indexing.Indexing.Pipelines;
using RepoQL.Indexing.Indexing.Pipelines.Analysis;
using RepoQL.Indexing.Indexing.Pipelines.Classification;
using RepoQL.Indexing.Indexing.Pipelines.Parsing;
using RepoQL.Indexing.Indexing.State;
using RepoQL.Indexing.Tests.TestHelpers;

namespace RepoQL.Indexing.Tests.Indexing;

public class IndexingEngineTests
{
    private static ILogger<IndexingEngine> CreateLogger()
    {
        var tunitLogger = TestContext.Current!.GetDefaultLogger();
        var factory = Microsoft.Extensions.Logging.LoggerFactory.Create(builder =>
            builder.AddProvider(new TUnitLoggerProvider(tunitLogger)));
        return factory.CreateLogger<IndexingEngine>();
    }

    [Test]
    [DisplayName("Skips unchanged artifacts when catalog confirms digest is current")]
    public async Task Given_CatalogReportsUpToDate_When_IndexItemAsync_Then_SkipsProcessing()
    {
        // Arrange
        var classifier = A.Fake<ClassificationPipeline>();
        A.CallTo(() => classifier.ProcessItemAsync(A<IndexItem>._, A<CancellationToken>._))
            .Throws(new InvalidOperationException("Classifier should not run when catalog skips the item."));

        var parser = A.Fake<ParsingPipeline>();
        A.CallTo(() => parser.ProcessItemAsync(A<IndexItem>._, A<CancellationToken>._))
            .Throws(new InvalidOperationException("Parser should not run when catalog skips the item."));

        var singleFileAnalyzer = A.Fake<SingleFileAnalysisPipeline>();
        A.CallTo(() => singleFileAnalyzer.ProcessItemAsync(A<IndexItem>._, A<CancellationToken>._))
            .Throws(new InvalidOperationException("Analyzer should not run when catalog skips the item."));

        var catalog = A.Fake<IDocumentCatalog>();
        A.CallTo(() => catalog.EnsureInitializedAsync(A<CancellationToken>._))
            .Returns(Task.CompletedTask);

        var existing = new DocumentCatalogEntry(
            CreateUri("file:///repo/already-indexed.md"),
            "A1B2C3",
            SemanticMediaType.Parse("text/markdown;kind=markdown.doc"),
            "C:\\repo\\already-indexed.md",
            DateTimeOffset.UtcNow.AddMinutes(-5));

        string? evaluatedDigest = null;
        A.CallTo(() => catalog.Evaluate(A<RepoUri>._, A<string>._))
            .ReturnsLazily(call =>
            {
                evaluatedDigest = call.GetArgument<string>(1);
                return new DocumentCatalogEvaluation(DocumentCatalogDecision.SkipUpToDate, existing);
            });

        var filter = A.Fake<IUriFilter>();
        A.CallTo(() => filter.IncludeFile(A<RepoUri>._)).Returns(false);

        var engine = new IndexingEngine(
            databaseWriter: null,
            filter: filter,
            classifier: classifier,
            parser: parser,
            singleFileAnalyzer: singleFileAnalyzer,
            multiFileAnalyzer: null,
            indexRebuilder: null,
            documentCatalog: catalog,
            options: null,
            logger: CreateLogger());

        var item = CreateTestItem();

        // Act
        await engine.IndexItemAsync(item, CancellationToken.None);

        // Assert
        A.CallTo(() => catalog.EnsureInitializedAsync(A<CancellationToken>._)).MustHaveHappenedOnceExactly();
        A.CallTo(() => catalog.Evaluate(item.Uri, A<string>._)).MustHaveHappenedOnceExactly();
        A.CallTo(() => catalog.BeginProcessing(A<RepoUri>._, A<string>._)).MustNotHaveHappened();
        A.CallTo(() => catalog.CompleteProcessing(A<RepoUri>._)).MustNotHaveHappened();

        evaluatedDigest.Should().NotBeNull();
        item.DigestHex.Should().Be(evaluatedDigest);
        item.ExistingEntry.Should().Be(existing);
    }

    [Test]
    [DisplayName("Registers and clears pending catalog state when processing a changed artifact")]
    public async Task Given_CatalogRequiresReindex_When_IndexItemAsync_Then_ProcessesAndTracksPendingState()
    {
        // Arrange
        var classifier = A.Fake<ClassificationPipeline>();
        A.CallTo(() => classifier.ProcessItemAsync(A<IndexItem>._, A<CancellationToken>._))
            .Returns(Task.FromResult(PipelineResult.Success));

        var parser = A.Fake<ParsingPipeline>();
        A.CallTo(() => parser.ProcessItemAsync(A<IndexItem>._, A<CancellationToken>._))
            .Returns(Task.FromResult(PipelineResult.Success));

        var singleFileAnalyzer = A.Fake<SingleFileAnalysisPipeline>();
        A.CallTo(() => singleFileAnalyzer.ProcessItemAsync(A<IndexItem>._, A<CancellationToken>._))
            .Returns(Task.FromResult(PipelineResult.Success));

        var catalog = A.Fake<IDocumentCatalog>();
        A.CallTo(() => catalog.EnsureInitializedAsync(A<CancellationToken>._))
            .Returns(Task.CompletedTask);

        var existing = new DocumentCatalogEntry(
            CreateUri("file:///repo/changed.md"),
            "OLD",
            SemanticMediaType.Parse("text/markdown;kind=markdown.doc"),
            "C:\\repo\\changed.md",
            DateTimeOffset.UtcNow.AddHours(-1));

        string? evaluatedDigest = null;
        A.CallTo(() => catalog.Evaluate(A<RepoUri>._, A<string>._))
            .ReturnsLazily(call =>
            {
                evaluatedDigest = call.GetArgument<string>(1);
                return new DocumentCatalogEvaluation(DocumentCatalogDecision.Reindex, existing);
            });

        string? pendingDigest = null;
        A.CallTo(() => catalog.BeginProcessing(A<RepoUri>._, A<string>._))
            .Invokes(call => pendingDigest = call.GetArgument<string>(1));

        var filter = A.Fake<IUriFilter>();
        A.CallTo(() => filter.IncludeFile(A<RepoUri>._)).Returns(false);

        var engine = new IndexingEngine(
            databaseWriter: null,
            filter: filter,
            classifier: classifier,
            parser: parser,
            singleFileAnalyzer: singleFileAnalyzer,
            multiFileAnalyzer: null,
            indexRebuilder: null,
            documentCatalog: catalog,
            options: null,
            logger: CreateLogger());

        var item = CreateTestItem();

        // Act
        await engine.IndexItemAsync(item, CancellationToken.None);

        // Assert
        A.CallTo(() => catalog.BeginProcessing(item.Uri, A<string>._)).MustHaveHappenedOnceExactly();
        A.CallTo(() => catalog.CompleteProcessing(item.Uri)).MustHaveHappenedOnceExactly();
        A.CallTo(() => classifier.ProcessItemAsync(item, A<CancellationToken>._)).MustHaveHappenedOnceExactly();
        A.CallTo(() => parser.ProcessItemAsync(item, A<CancellationToken>._)).MustHaveHappenedOnceExactly();
        A.CallTo(() => singleFileAnalyzer.ProcessItemAsync(item, A<CancellationToken>._)).MustHaveHappenedOnceExactly();

        evaluatedDigest.Should().NotBeNull();
        pendingDigest.Should().Be(evaluatedDigest);
        item.DigestHex.Should().Be(evaluatedDigest);
        item.ExistingEntry.Should().Be(existing);
    }

    [Test]
    [DisplayName("Clears pending catalog state even when pipeline terminates early")]
    public async Task Given_PipelineReturnsError_When_IndexItemAsync_Then_CatalogStateIsCleared()
    {
        // Arrange
        var classifier = A.Fake<ClassificationPipeline>();
        A.CallTo(() => classifier.ProcessItemAsync(A<IndexItem>._, A<CancellationToken>._))
            .Returns(Task.FromResult(PipelineResult.Success));

        var parser = A.Fake<ParsingPipeline>();
        A.CallTo(() => parser.ProcessItemAsync(A<IndexItem>._, A<CancellationToken>._))
            .Returns(Task.FromResult(PipelineResult.Error));

        var singleFileAnalyzer = A.Fake<SingleFileAnalysisPipeline>();
        A.CallTo(() => singleFileAnalyzer.ProcessItemAsync(A<IndexItem>._, A<CancellationToken>._))
            .Throws(new InvalidOperationException("Analyzer should not run when parser fails."));

        var catalog = A.Fake<IDocumentCatalog>();
        A.CallTo(() => catalog.EnsureInitializedAsync(A<CancellationToken>._))
            .Returns(Task.CompletedTask);

        A.CallTo(() => catalog.Evaluate(A<RepoUri>._, A<string>._))
            .Returns(new DocumentCatalogEvaluation(DocumentCatalogDecision.Reindex, null));

        var filter = A.Fake<IUriFilter>();
        A.CallTo(() => filter.IncludeFile(A<RepoUri>._)).Returns(false);

        var engine = new IndexingEngine(
            databaseWriter: null,
            filter: filter,
            classifier: classifier,
            parser: parser,
            singleFileAnalyzer: singleFileAnalyzer,
            multiFileAnalyzer: null,
            indexRebuilder: null,
            documentCatalog: catalog,
            options: null,
            logger: CreateLogger());

        var item = CreateTestItem();

        // Act
        await engine.IndexItemAsync(item, CancellationToken.None);

        // Assert
        A.CallTo(() => catalog.BeginProcessing(item.Uri, A<string>._)).MustHaveHappenedOnceExactly();
        A.CallTo(() => catalog.CompleteProcessing(item.Uri)).MustHaveHappenedOnceExactly();
        A.CallTo(() => singleFileAnalyzer.ProcessItemAsync(A<IndexItem>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Test]
    [DisplayName("Successfully processes item through all pipeline stages")]
    public async Task Given_AllPipelinesSucceed_When_ApplyIndexerPipeline_Then_ReturnsSuccess()
    {
        // Arrange
        var classifier = A.Fake<ClassificationPipeline>();
        var parser = A.Fake<ParsingPipeline>();
        var singleFileAnalyzer = A.Fake<SingleFileAnalysisPipeline>();
        
        var engine = new IndexingEngine(
            databaseWriter: null,
            filter: null,
            classifier: classifier,
            parser: parser,
            singleFileAnalyzer: singleFileAnalyzer);

        var item = CreateTestItem();

        A.CallTo(() => classifier.ProcessItemAsync(item, A<CancellationToken>._))
            .Returns(Task.FromResult(PipelineResult.Success));
        A.CallTo(() => parser.ProcessItemAsync(item, A<CancellationToken>._))
            .Returns(Task.FromResult(PipelineResult.Success));
        A.CallTo(() => singleFileAnalyzer.ProcessItemAsync(item, A<CancellationToken>._))
            .Returns(Task.FromResult(PipelineResult.Success));

        // Act
        var result = await engine.ApplyIndexerPipeline(item, CancellationToken.None);

        // Assert
        result.Should().Be(PipelineResult.Success);
        A.CallTo(() => classifier.ProcessItemAsync(item, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => parser.ProcessItemAsync(item, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => singleFileAnalyzer.ProcessItemAsync(item, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Test]
    [DisplayName("Short-circuits when classifier filters item")]
    public async Task Given_ClassifierFilters_When_ApplyIndexerPipeline_Then_ReturnsFilteredWithoutCallingSubsequentStages()
    {
        // Arrange
        var classifier = A.Fake<ClassificationPipeline>();
        var parser = A.Fake<ParsingPipeline>();
        var singleFileAnalyzer = A.Fake<SingleFileAnalysisPipeline>();

        var engine = new IndexingEngine(
            databaseWriter: null,
            filter: null,
            classifier: classifier,
            parser: parser,
            singleFileAnalyzer: singleFileAnalyzer);

        var item = CreateTestItem();

        A.CallTo(() => classifier.ProcessItemAsync(item, A<CancellationToken>._))
            .Returns(Task.FromResult(PipelineResult.Filtered));

        // Act
        var result = await engine.ApplyIndexerPipeline(item, CancellationToken.None);

        // Assert
        result.Should().Be(PipelineResult.Filtered, "pipeline should short-circuit on non-success result");
        A.CallTo(() => classifier.ProcessItemAsync(item, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => parser.ProcessItemAsync(A<IndexItem>._, A<CancellationToken>._))
            .MustNotHaveHappened();
        A.CallTo(() => singleFileAnalyzer.ProcessItemAsync(A<IndexItem>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    [Test]
    [DisplayName("Short-circuits when classifier returns error")]
    public async Task Given_ClassifierErrors_When_ApplyIndexerPipeline_Then_ReturnsErrorWithoutCallingSubsequentStages()
    {
        // Arrange
        var classifier = A.Fake<ClassificationPipeline>();
        var parser = A.Fake<ParsingPipeline>();
        var singleFileAnalyzer = A.Fake<SingleFileAnalysisPipeline>();

        var engine = new IndexingEngine(
            databaseWriter: null,
            filter: null,
            classifier: classifier,
            parser: parser,
            singleFileAnalyzer: singleFileAnalyzer);

        var item = CreateTestItem();

        A.CallTo(() => classifier.ProcessItemAsync(item, A<CancellationToken>._))
            .Returns(Task.FromResult(PipelineResult.Error));

        // Act
        var result = await engine.ApplyIndexerPipeline(item, CancellationToken.None);

        // Assert
        result.Should().Be(PipelineResult.Error, "pipeline should propagate error from classifier");
        A.CallTo(() => parser.ProcessItemAsync(A<IndexItem>._, A<CancellationToken>._))
            .MustNotHaveHappened();
        A.CallTo(() => singleFileAnalyzer.ProcessItemAsync(A<IndexItem>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    [Test]
    [DisplayName("Short-circuits when parser fails after successful classification")]
    public async Task Given_ParserFails_When_ApplyIndexerPipeline_Then_ReturnsErrorWithoutCallingAnalyzer()
    {
        // Arrange
        var classifier = A.Fake<ClassificationPipeline>();
        var parser = A.Fake<ParsingPipeline>();
        var singleFileAnalyzer = A.Fake<SingleFileAnalysisPipeline>();

        var engine = new IndexingEngine(
            databaseWriter: null,
            filter: null,
            classifier: classifier,
            parser: parser,
            singleFileAnalyzer: singleFileAnalyzer);

        var item = CreateTestItem();

        A.CallTo(() => classifier.ProcessItemAsync(item, A<CancellationToken>._))
            .Returns(Task.FromResult(PipelineResult.Success));
        A.CallTo(() => parser.ProcessItemAsync(item, A<CancellationToken>._))
            .Returns(Task.FromResult(PipelineResult.Error));

        // Act
        var result = await engine.ApplyIndexerPipeline(item, CancellationToken.None);

        // Assert
        result.Should().Be(PipelineResult.Error, "pipeline should propagate error from parser");
        A.CallTo(() => classifier.ProcessItemAsync(item, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => parser.ProcessItemAsync(item, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => singleFileAnalyzer.ProcessItemAsync(A<IndexItem>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    [Test]
    [DisplayName("Returns analyzer result when classifier and parser succeed")]
    public async Task Given_AnalyzerFails_When_ApplyIndexerPipeline_Then_ReturnsAnalyzerResult()
    {
        // Arrange
        var classifier = A.Fake<ClassificationPipeline>();
        var parser = A.Fake<ParsingPipeline>();
        var singleFileAnalyzer = A.Fake<SingleFileAnalysisPipeline>();

        var engine = new IndexingEngine(
            databaseWriter: null,
            filter: null,
            classifier: classifier,
            parser: parser,
            singleFileAnalyzer: singleFileAnalyzer);

        var item = CreateTestItem();

        A.CallTo(() => classifier.ProcessItemAsync(item, A<CancellationToken>._))
            .Returns(Task.FromResult(PipelineResult.Success));
        A.CallTo(() => parser.ProcessItemAsync(item, A<CancellationToken>._))
            .Returns(Task.FromResult(PipelineResult.Success));
        A.CallTo(() => singleFileAnalyzer.ProcessItemAsync(item, A<CancellationToken>._))
            .Returns(Task.FromResult(PipelineResult.Error));

        // Act
        var result = await engine.ApplyIndexerPipeline(item, CancellationToken.None);

        // Assert
        result.Should().Be(PipelineResult.Error, "final stage result should be returned");
        A.CallTo(() => classifier.ProcessItemAsync(item, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => parser.ProcessItemAsync(item, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => singleFileAnalyzer.ProcessItemAsync(item, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Test]
    [Arguments(PipelineResult.Cancelled)]
    [Arguments(PipelineResult.Filtered)]
    [DisplayName("Respects cancellation and filtered states from any pipeline stage")]
    public async Task Given_PipelineReturnsNonSuccessResult_When_ApplyIndexerPipeline_Then_ShortCircuits(
        PipelineResult pipelineResult)
    {
        // Arrange
        var classifier = A.Fake<ClassificationPipeline>();
        var parser = A.Fake<ParsingPipeline>();
        var singleFileAnalyzer = A.Fake<SingleFileAnalysisPipeline>();

        var engine = new IndexingEngine(
            databaseWriter: null,
            filter: null,
            classifier: classifier,
            parser: parser,
            singleFileAnalyzer: singleFileAnalyzer);

        var item = CreateTestItem();

        A.CallTo(() => classifier.ProcessItemAsync(item, A<CancellationToken>._))
            .Returns(Task.FromResult(PipelineResult.Success));
        A.CallTo(() => parser.ProcessItemAsync(item, A<CancellationToken>._))
            .Returns(Task.FromResult(pipelineResult));

        // Act
        var result = await engine.ApplyIndexerPipeline(item, CancellationToken.None);

        // Assert
        result.Should().Be(pipelineResult);
        A.CallTo(() => singleFileAnalyzer.ProcessItemAsync(A<IndexItem>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    private static IndexItem CreateTestItem()
    {
        // Create mock IFileInfo
        var fileInfo = A.Fake<IFileInfo>();
        A.CallTo(() => fileInfo.Name).Returns("test.txt");
        A.CallTo(() => fileInfo.Exists).Returns(true);
        A.CallTo(() => fileInfo.Length).Returns(100L);
        A.CallTo(() => fileInfo.LastModified).Returns(DateTimeOffset.UtcNow);
        A.CallTo(() => fileInfo.IsDirectory).Returns(false);
        A.CallTo(() => fileInfo.PhysicalPath).Returns("C:\\test\\test.txt");
        A.CallTo(() => fileInfo.CreateReadStream()).Returns(new MemoryStream());

        // Create mock IVirtualFileSystem
        var fileSystem = A.Fake<IVirtualFileSystem>();
        if (!RepoUri.TryParse("file:///test.txt", out var testUri))
            throw new InvalidOperationException("Failed to parse test URI");
        A.CallTo(() => fileSystem.GetUri(fileInfo)).Returns(testUri);

        // Create real RawArtifact
        var rawArtifact = new RawArtifact(fileInfo, fileSystem);

        return new IndexItem(rawArtifact, IndexItemOptions.Default);
    }

    private static RepoUri CreateUri(string value)
    {
        return RepoUri.TryParse(value, out var parsed)
            ? parsed!
            : throw new InvalidOperationException($"Unable to parse URI '{value}'.");
    }
}
