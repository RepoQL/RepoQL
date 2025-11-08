using System;
using AwesomeAssertions;
using FakeItEasy;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using RepoQL.Contracts;
using RepoQL.Contracts.Models;
using RepoQL.FileSystem.Abstractions;
using RepoQL.Contracts.Data;
using RepoQL.Indexing.Indexing;
using RepoQL.Indexing.Indexing.Commit;
using RepoQL.Indexing.Indexing.Pipelines;
using RepoQL.Indexing.Indexing.Pipelines.Analysis;
using RepoQL.Indexing.Indexing.Pipelines.Classification;
using RepoQL.Indexing.Indexing.Pipelines.Parsing;
using RepoQL.Indexing.Indexing.PostProcessing;
using RepoQL.Indexing.Indexing.State;
using RepoQL.Indexing.Tests.TestHelpers;

namespace RepoQL.Indexing.Tests.Indexing;

public class IndexingEngineTests
{
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
            committer: NullIndexingCommitter.Instance,
            options: null,
            logger: TestLogging.CreateLogger<IndexingEngine>());

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

        var committer = A.Fake<IIndexingCommitter>();

        var engine = new IndexingEngine(
            databaseWriter: null,
            filter: filter,
            classifier: classifier,
            parser: parser,
            singleFileAnalyzer: singleFileAnalyzer,
            multiFileAnalyzer: null,
            indexRebuilder: null,
            documentCatalog: catalog,
            committer: committer,
            options: null,
            logger: TestLogging.CreateLogger<IndexingEngine>());

        var item = CreateTestItem();

        // Act
        await engine.IndexItemAsync(item, CancellationToken.None);

        // Assert
        A.CallTo(() => catalog.BeginProcessing(item.Uri, A<string>._)).MustHaveHappenedOnceExactly();
        A.CallTo(() => catalog.CompleteProcessing(item.Uri)).MustHaveHappenedOnceExactly();
        A.CallTo(() => classifier.ProcessItemAsync(item, A<CancellationToken>._)).MustHaveHappenedOnceExactly();
        A.CallTo(() => parser.ProcessItemAsync(item, A<CancellationToken>._)).MustHaveHappenedOnceExactly();
        A.CallTo(() => singleFileAnalyzer.ProcessItemAsync(item, A<CancellationToken>._)).MustHaveHappenedOnceExactly();
        A.CallTo(() => committer.CommitAsync(item, A<CancellationToken>._)).MustHaveHappenedOnceExactly();

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

        var committer = A.Fake<IIndexingCommitter>();

        var engine = new IndexingEngine(
            databaseWriter: null,
            filter: filter,
            classifier: classifier,
            parser: parser,
            singleFileAnalyzer: singleFileAnalyzer,
            multiFileAnalyzer: null,
            indexRebuilder: null,
            documentCatalog: catalog,
            committer: committer,
            options: null,
            logger: TestLogging.CreateLogger<IndexingEngine>());

        var item = CreateTestItem();

        // Act
        await engine.IndexItemAsync(item, CancellationToken.None);

        // Assert
        A.CallTo(() => catalog.BeginProcessing(item.Uri, A<string>._)).MustHaveHappenedOnceExactly();
        A.CallTo(() => catalog.CompleteProcessing(item.Uri)).MustHaveHappenedOnceExactly();
        A.CallTo(() => singleFileAnalyzer.ProcessItemAsync(A<IndexItem>._, A<CancellationToken>._)).MustNotHaveHappened();
        A.CallTo(() => committer.CommitAsync(A<IndexItem>._, A<CancellationToken>._)).MustNotHaveHappened();
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
            singleFileAnalyzer: singleFileAnalyzer,
            multiFileAnalyzer: null,
            indexRebuilder: null,
            documentCatalog: new DocumentCatalog(NullDocumentCatalogDataSource.Instance),
            committer: NullIndexingCommitter.Instance);

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
            singleFileAnalyzer: singleFileAnalyzer,
            multiFileAnalyzer: null,
            indexRebuilder: null,
            documentCatalog: new DocumentCatalog(NullDocumentCatalogDataSource.Instance),
            committer: NullIndexingCommitter.Instance);

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
            singleFileAnalyzer: singleFileAnalyzer,
            multiFileAnalyzer: null,
            indexRebuilder: null,
            documentCatalog: new DocumentCatalog(NullDocumentCatalogDataSource.Instance),
            committer: NullIndexingCommitter.Instance);

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
            singleFileAnalyzer: singleFileAnalyzer,
            multiFileAnalyzer: null,
            indexRebuilder: null,
            documentCatalog: new DocumentCatalog(NullDocumentCatalogDataSource.Instance),
            committer: NullIndexingCommitter.Instance);

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
            singleFileAnalyzer: singleFileAnalyzer,
            multiFileAnalyzer: null,
            indexRebuilder: null,
            documentCatalog: new DocumentCatalog(NullDocumentCatalogDataSource.Instance),
            committer: NullIndexingCommitter.Instance);

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
            singleFileAnalyzer: singleFileAnalyzer,
            multiFileAnalyzer: null,
            indexRebuilder: null,
            documentCatalog: new DocumentCatalog(NullDocumentCatalogDataSource.Instance),
            committer: NullIndexingCommitter.Instance);

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

    [Test]
    [Timeout(15_000)]
    [DisplayName("StateChanged fires and WaitForAsync waits until all stages are idle")]
    public async Task Given_ItemProcessing_When_WaitingForAllIdle_Then_CompletesAfterStagesFinish(CancellationToken token)
    {
        // Arrange
        var gate = NewTaskCompletionSource<bool>();
        var classifier = A.Fake<ClassificationPipeline>();
        A.CallTo(() => classifier.ProcessItemAsync(A<IndexItem>._, A<CancellationToken>._))
            .ReturnsLazily(async _ =>
            {
                await gate.Task.ConfigureAwait(false);
                return PipelineResult.Success;
            });

        var parser = A.Fake<ParsingPipeline>();
        A.CallTo(() => parser.ProcessItemAsync(A<IndexItem>._, A<CancellationToken>._))
            .Returns(Task.FromResult(PipelineResult.Success));

        var singleFileAnalyzer = A.Fake<SingleFileAnalysisPipeline>();
        A.CallTo(() => singleFileAnalyzer.ProcessItemAsync(A<IndexItem>._, A<CancellationToken>._))
            .Returns(Task.FromResult(PipelineResult.Success));

        var catalog = new DocumentCatalog(NullDocumentCatalogDataSource.Instance);
        var filter = A.Fake<IUriFilter>();
        A.CallTo(() => filter.IncludeFile(A<RepoUri>._)).Returns(false);
        var committer = A.Fake<IIndexingCommitter>();
        A.CallTo(() => committer.CommitAsync(A<IndexItem>._, A<CancellationToken>._))
            .Returns(Task.CompletedTask);

        var engine = new IndexingEngine(
            databaseWriter: null,
            filter: filter,
            classifier: classifier,
            parser: parser,
            singleFileAnalyzer: singleFileAnalyzer,
            multiFileAnalyzer: null,
            indexRebuilder: null,
            documentCatalog: catalog,
            committer: committer,
            options: null,
            logger: TestLogging.CreateLogger<IndexingEngine>());

        var transitions = new List<(IndexingState OldState, IndexingState NewState)>();
        var busySignal = NewTaskCompletionSource<bool>();
        engine.StateChanged += (_, args) =>
        {
            transitions.Add((args.OldState, args.NewState));
            if (args.NewState.HasFlag(IndexingState.ClassificationBusy) &&
                !args.OldState.HasFlag(IndexingState.ClassificationBusy))
            {
                busySignal.TrySetResult(true);
            }
        };

        var item = CreateTestItem();

        var processingTask = engine.IndexItemAsync(item, token);
        await busySignal.Task;
        var waitTask = engine.WaitForAsync(IndexingState.AllIdle, token).AsTask();
        waitTask.IsCompleted.Should().BeFalse("engine should report busy while classification is blocked");

        gate.SetResult(true);

        await processingTask.WaitAsync(token);
        await waitTask;

        // Assert
        transitions.Should().NotBeEmpty();
        transitions.Should().Contain(t => t.NewState.HasFlag(IndexingState.ClassificationBusy));
        transitions.Last().NewState.Should().Be(IndexingState.AllIdle);
        A.CallTo(() => committer.CommitAsync(item, A<CancellationToken>._)).MustHaveHappenedOnceExactly();
    }

    [Test]
    [DisplayName("Raises HotPathIdle when pending work drains")]
    public async Task Given_WorkCompletes_When_Idle_Then_HotPathIdleFires()
    {
        var engine = CreateEngineForIdleTests();
        var artifact = CreateRawArtifact("file:///repo/hot1.md");

        var idleTcs = NewTaskCompletionSource<long>();
        EventHandler<HotPathIdleEventArgs>? handler = null;
        handler = (_, args) =>
        {
            idleTcs.TrySetResult(args.Epoch);
            if (handler != null)
                engine.HotPathIdle -= handler;
        };
        engine.HotPathIdle += handler;

        await engine.EnqueueItemAsync(artifact, IndexItemOptions.Default, CancellationToken.None);
        (await idleTcs.Task.WaitAsync(TimeSpan.FromSeconds(2))).Should().Be(0);
    }

    [Test]
    [DisplayName("HotPathIdle waits for pending items before signalling")]
    public async Task Given_WorkPending_When_WaitingForIdle_Then_EventDelays()
    {
        var gate = NewTaskCompletionSource<bool>();
        var engine = CreateEngineForIdleTests(gate);
        var artifact1 = CreateRawArtifact("file:///repo/pending-a.md");
        var artifact2 = CreateRawArtifact("file:///repo/pending-b.md");

        var idleTcs = NewTaskCompletionSource<long>();
        engine.HotPathIdle += (_, args) => idleTcs.TrySetResult(args.Epoch);

        await engine.EnqueueItemAsync(artifact1, IndexItemOptions.Default, CancellationToken.None);
        await engine.EnqueueItemAsync(artifact2, IndexItemOptions.Default, CancellationToken.None);

        await Task.Delay(100);
        idleTcs.Task.IsCompleted.Should().BeFalse("event should not fire while work is still running");

        gate.SetResult(true);
        (await idleTcs.Task.WaitAsync(TimeSpan.FromSeconds(2))).Should().Be(0);
    }

    [Test]
    [DisplayName("HotPathIdle reports the epoch that drained")]
    public async Task Given_NewEpoch_When_WorkCompletes_Then_ReportsEpoch()
    {
        var engine = CreateEngineForIdleTests();

        var firstIdle = NewTaskCompletionSource<long>();
        EventHandler<HotPathIdleEventArgs>? firstHandler = null;
        firstHandler = (_, args) =>
        {
            firstIdle.TrySetResult(args.Epoch);
            if (firstHandler != null)
                engine.HotPathIdle -= firstHandler;
        };
        engine.HotPathIdle += firstHandler;

        await engine.EnqueueItemAsync(CreateRawArtifact("file:///repo/epoch0.md"), IndexItemOptions.Default, CancellationToken.None);
        (await firstIdle.Task.WaitAsync(TimeSpan.FromSeconds(2))).Should().Be(0);

        var nextEpoch = engine.BeginNewEpoch();
        var secondIdle = NewTaskCompletionSource<long>();
        engine.HotPathIdle += (_, args) => secondIdle.TrySetResult(args.Epoch);

        await engine.EnqueueItemAsync(CreateRawArtifact("file:///repo/epoch1.md"), IndexItemOptions.Default, CancellationToken.None);
        (await secondIdle.Task.WaitAsync(TimeSpan.FromSeconds(2))).Should().Be(nextEpoch);
    }

    [Test]
    [Timeout(15_000)]
    [DisplayName("Dispatches completed items to analysis once hot path is idle")]
    public async Task Given_ItemCompletes_When_HotPathIdle_Then_AnalysisRuns(CancellationToken token)
    {
        var harness = CreateAnalysisHarness();
        await harness.EnqueueAsync("file:///repo/post-index/analysis.md", token);

        await harness.WaitForAnalysisAsync(token);
        harness.Engine.AnalysisQueue.Depth.Should().Be(0);
    }

    [Test]
    [Timeout(15_000)]
    [DisplayName("Analysis waits until hot path drains before dispatching work")]
    public async Task Given_HotPathBusy_When_IdleDelayed_Then_AnalysisDeferred(CancellationToken token)
    {
        var harness = CreateAnalysisHarness(gateParsing: true);
        await harness.EnqueueAsync("file:///repo/post-index/deferred.md", token);

        await Task.Delay(100, token);
        harness.AnalysisStarted.Should().BeFalse("analysis should not run while the hot path is still busy");

        harness.ReleaseParsing();

        await harness.WaitForAnalysisAsync(token);
    }

    [Test]
    [Timeout(15_000)]
    [DisplayName("Pruning completes before analysis dispatches work")]
    public async Task Given_PostProcessing_When_PruningCompletes_Then_AnalysisRunsAfter(CancellationToken token)
    {
        var pruneGate = NewTaskCompletionSource<bool>();
        var pruner = A.Fake<IArtifactPruner>();
        A.CallTo(() => pruner.PruneAsync(A<IReadOnlyCollection<IndexItem>>._, A<CancellationToken>._))
            .ReturnsLazily(call =>
            {
                pruneGate.TrySetResult(true);
                return Task.FromResult(PruningResult.None);
            });

        var analysisSignal = NewTaskCompletionSource<bool>();
        var engine = CreateEngineForAnalysisTests(
            parsingGate: null,
            multiFileSignal: analysisSignal,
            pruner: pruner,
            vectorCoordinator: NullVectorIndexCoordinator.Instance);

        await engine.EnqueueItemAsync(CreateRawArtifact("file:///repo/prune-first.md"), IndexItemOptions.Default, token);

        await analysisSignal.Task.WaitAsync(token);
        pruneGate.Task.IsCompleted.Should().BeTrue("Pruning should complete before analysis starts.");
    }

    [Test]
    [Timeout(15_000)]
    [DisplayName("Vector index updates run before enqueuing analysis work")]
    public async Task Given_VectorCoordinator_When_PostProcessing_Then_VectorRunsBeforeAnalysis(CancellationToken token)
    {
        var deletedUri = CreateUri("file:///repo/deleted.md");
        var pruneResult = new PruningResult(new[] { deletedUri });
        var pruner = A.Fake<IArtifactPruner>();
        A.CallTo(() => pruner.PruneAsync(A<IReadOnlyCollection<IndexItem>>._, A<CancellationToken>._))
            .Returns(Task.FromResult(pruneResult));

        var deleteApplied = NewTaskCompletionSource<bool>();
        var vectorApplied = NewTaskCompletionSource<bool>();
        var vector = A.Fake<IVectorIndexCoordinator>();
        A.CallTo(() => vector.ApplyDeletesAsync(pruneResult.DeletedArtifacts, A<CancellationToken>._))
            .ReturnsLazily(_ =>
            {
                deleteApplied.TrySetResult(true);
                return Task.CompletedTask;
            });
        A.CallTo(() => vector.ApplyAsync(A<IndexItem>._, A<CancellationToken>._))
            .ReturnsLazily(async _ =>
            {
                await deleteApplied.Task.WaitAsync(TimeSpan.FromSeconds(2));
                vectorApplied.TrySetResult(true);
            });

        var analysisSignal = NewTaskCompletionSource<bool>();
        var engine = CreateEngineForAnalysisTests(
            parsingGate: null,
            multiFileSignal: analysisSignal,
            pruner: pruner,
            vectorCoordinator: vector);

        await engine.EnqueueItemAsync(CreateRawArtifact("file:///repo/vector-upsert.md"), IndexItemOptions.Default, token);

        await analysisSignal.Task.WaitAsync(token);
        vectorApplied.Task.IsCompleted.Should().BeTrue("Vector updates should complete before analysis starts.");
        A.CallTo(() => vector.ApplyDeletesAsync(pruneResult.DeletedArtifacts, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => vector.ApplyAsync(A<IndexItem>._, A<CancellationToken>._))
            .MustHaveHappened();
    }

    [Test]
    [Timeout(15_000)]
    [DisplayName("Pruning deletes are issued through the database writer")]
    public async Task Given_StaleDocuments_When_PrunerFindsThem_Then_DeleteOperationSentToWriter(CancellationToken token)
    {
        var deleteUri = CreateUri("file:///repo/stale.md");
        var pruner = A.Fake<IArtifactPruner>();
        A.CallTo(() => pruner.PruneAsync(A<IReadOnlyCollection<IndexItem>>._, A<CancellationToken>._))
            .Returns(Task.FromResult(new PruningResult(new[] { deleteUri })));

        var writer = A.Fake<IDatabaseWriter>();
        A.CallTo(() => writer.EnqueueAndWaitAsync(
                A<WriteOperation>.That.Matches(op => op.Type == WriteOperationType.DeleteDocument && op.Uri == deleteUri),
                A<CancellationToken>._))
            .Returns(new ValueTask<CommitResult>(new CommitResult { Success = true }));

        var vector = NullVectorIndexCoordinator.Instance;
        var analysisSignal = NewTaskCompletionSource<bool>();
        var engine = CreateEngineForAnalysisTests(
            parsingGate: null,
            multiFileSignal: analysisSignal,
            pruner: pruner,
            vectorCoordinator: vector,
            writer: writer);

        await engine.EnqueueItemAsync(CreateRawArtifact("file:///repo/live.md"), IndexItemOptions.Default, token);

        await analysisSignal.Task.WaitAsync(token);
        A.CallTo(() => writer.EnqueueAndWaitAsync(
                A<WriteOperation>.That.Matches(op => op.Type == WriteOperationType.DeleteDocument && op.Uri == deleteUri),
                A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
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

    private static RawArtifact CreateRawArtifact(string uri)
    {
        var fileInfo = A.Fake<IFileInfo>();
        A.CallTo(() => fileInfo.Name).Returns(Path.GetFileName(uri));
        A.CallTo(() => fileInfo.Exists).Returns(true);
        A.CallTo(() => fileInfo.Length).Returns(64L);
        A.CallTo(() => fileInfo.LastModified).Returns(DateTimeOffset.UtcNow);
        A.CallTo(() => fileInfo.IsDirectory).Returns(false);
        A.CallTo(() => fileInfo.PhysicalPath).Returns(uri);
        A.CallTo(() => fileInfo.CreateReadStream()).Returns(new MemoryStream(new byte[32]));

        var fileSystem = A.Fake<IVirtualFileSystem>();
        if (!RepoUri.TryParse(uri, out var repoUri))
            throw new InvalidOperationException($"Unable to parse URI '{uri}'.");
        A.CallTo(() => fileSystem.GetUri(fileInfo)).Returns(repoUri);

        return new RawArtifact(fileInfo, fileSystem);
    }

    private static IndexingEngine CreateEngineForIdleTests(
        TaskCompletionSource<bool>? parsingGate = null,
        IArtifactPruner? pruner = null,
        IVectorIndexCoordinator? vectorCoordinator = null)
    {
        var classifier = A.Fake<ClassificationPipeline>();
        A.CallTo(() => classifier.ProcessItemAsync(A<IndexItem>._, A<CancellationToken>._))
            .Returns(Task.FromResult(PipelineResult.Success));

        var parser = A.Fake<ParsingPipeline>();
        if (parsingGate is null)
        {
            A.CallTo(() => parser.ProcessItemAsync(A<IndexItem>._, A<CancellationToken>._))
                .Returns(Task.FromResult(PipelineResult.Success));
        }
        else
        {
            A.CallTo(() => parser.ProcessItemAsync(A<IndexItem>._, A<CancellationToken>._))
                .ReturnsLazily(async _ =>
                {
                    await parsingGate.Task.ConfigureAwait(false);
                    return PipelineResult.Success;
                });
        }

        var singleFileAnalyzer = A.Fake<SingleFileAnalysisPipeline>();
        A.CallTo(() => singleFileAnalyzer.ProcessItemAsync(A<IndexItem>._, A<CancellationToken>._))
            .Returns(Task.FromResult(PipelineResult.Success));

        var catalog = new DocumentCatalog(NullDocumentCatalogDataSource.Instance);
        var filter = A.Fake<IUriFilter>();
        A.CallTo(() => filter.IncludeFile(A<RepoUri>._)).Returns(false);
        var committer = A.Fake<IIndexingCommitter>();
        A.CallTo(() => committer.CommitAsync(A<IndexItem>._, A<CancellationToken>._))
            .Returns(Task.CompletedTask);

        return new IndexingEngine(
            databaseWriter: null,
            filter: filter,
            classifier: classifier,
            parser: parser,
            singleFileAnalyzer: singleFileAnalyzer,
            multiFileAnalyzer: null,
            indexRebuilder: null,
            documentCatalog: catalog,
            committer: committer,
            artifactPruner: pruner,
            vectorCoordinator: vectorCoordinator,
            options: null,
            logger: TestLogging.CreateLogger<IndexingEngine>());
    }

    private static IndexingEngine CreateEngineForAnalysisTests(
        TaskCompletionSource<bool>? parsingGate,
        TaskCompletionSource<bool> multiFileSignal,
        IArtifactPruner? pruner = null,
        IVectorIndexCoordinator? vectorCoordinator = null,
        IDatabaseWriter? writer = null)
    {
        var classifier = A.Fake<ClassificationPipeline>();
        A.CallTo(() => classifier.ProcessItemAsync(A<IndexItem>._, A<CancellationToken>._))
            .Returns(Task.FromResult(PipelineResult.Success));

        var parser = A.Fake<ParsingPipeline>();
        if (parsingGate is null)
        {
            A.CallTo(() => parser.ProcessItemAsync(A<IndexItem>._, A<CancellationToken>._))
                .Returns(Task.FromResult(PipelineResult.Success));
        }
        else
        {
            A.CallTo(() => parser.ProcessItemAsync(A<IndexItem>._, A<CancellationToken>._))
                .ReturnsLazily(async _ =>
                {
                    await parsingGate.Task.ConfigureAwait(false);
                    return PipelineResult.Success;
                });
        }

        var singleFileAnalyzer = A.Fake<SingleFileAnalysisPipeline>();
        A.CallTo(() => singleFileAnalyzer.ProcessItemAsync(A<IndexItem>._, A<CancellationToken>._))
            .Returns(Task.FromResult(PipelineResult.Success));

        var multiFileAnalyzer = A.Fake<MultiFileAnalysisPipeline>(options =>
            options.WithArgumentsForConstructor(() =>
                new MultiFileAnalysisPipeline(Array.Empty<IAsyncPipeline<IAnnotatedArtifact, Annotation[]>>())));
        A.CallTo(() => multiFileAnalyzer.ProcessItemAsync(A<IAnnotatedArtifact>._, A<CancellationToken>._))
            .ReturnsLazily(_ =>
            {
                multiFileSignal.TrySetResult(true);
                return Task.FromResult(PipelineResult.Success);
            });

        var indexRebuilder = A.Fake<IndexRebuildPipeline>(options =>
            options.WithArgumentsForConstructor(() =>
                new IndexRebuildPipeline(Array.Empty<IAsyncPipeline<IAnnotatedArtifact, string>>())));
        A.CallTo(() => indexRebuilder.ProcessItemAsync(A<IAnnotatedArtifact>._, A<CancellationToken>._))
            .Returns(Task.FromResult(PipelineResult.Success));

        var catalog = new DocumentCatalog(NullDocumentCatalogDataSource.Instance);
        var filter = A.Fake<IUriFilter>();
        A.CallTo(() => filter.IncludeFile(A<RepoUri>._)).Returns(false);
        var committer = A.Fake<IIndexingCommitter>();
        A.CallTo(() => committer.CommitAsync(A<IndexItem>._, A<CancellationToken>._))
            .Returns(Task.CompletedTask);

        var options = new IndexingEngineOptions
        {
            IndexingQueueSize = 32,
            IndexingWorkers = 1,
            AnalysisQueueSize = 32,
            AnalysisWorkers = 1
        };

        return new IndexingEngine(
            databaseWriter: writer,
            filter: filter,
            classifier: classifier,
            parser: parser,
            singleFileAnalyzer: singleFileAnalyzer,
            multiFileAnalyzer: multiFileAnalyzer,
            indexRebuilder: indexRebuilder,
            documentCatalog: catalog,
            committer: committer,
            artifactPruner: pruner,
            vectorCoordinator: vectorCoordinator,
            options: options,
            logger: TestLogging.CreateLogger<IndexingEngine>());
    }

    private static TaskCompletionSource<T> NewTaskCompletionSource<T>() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static Task WaitForAnalysisToCompleteAsync(
        IndexingEngine engine,
        TaskCompletionSource<bool> analysisSignal,
        CancellationToken token)
    {
        return WaitAsync();

        async Task WaitAsync()
        {
            await analysisSignal.Task.WaitAsync(token);
            await engine.AnalysisQueue.WhenIdleAsync().WaitAsync(token);
            await engine.WaitForAsync(IndexingState.AllIdle, token);
        }
    }

    private static AnalysisHarness CreateAnalysisHarness(bool gateParsing = false) =>
        new(gateParsing);

    private sealed class AnalysisHarness
    {
        public AnalysisHarness(bool gateParsing)
        {
            ParsingGate = gateParsing ? NewTaskCompletionSource<bool>() : null;
            AnalysisSignal = NewTaskCompletionSource<bool>();
            Engine = CreateEngineForAnalysisTests(ParsingGate, AnalysisSignal);
        }

        private TaskCompletionSource<bool>? ParsingGate { get; }
        public TaskCompletionSource<bool> AnalysisSignal { get; }
        public IndexingEngine Engine { get; }

        public Task EnqueueAsync(string uri, CancellationToken token) =>
            Engine.EnqueueItemAsync(CreateRawArtifact(uri), IndexItemOptions.Default, token);

        public Task WaitForAnalysisAsync(CancellationToken token) =>
            WaitForAnalysisToCompleteAsync(Engine, AnalysisSignal, token);

        public bool AnalysisStarted => AnalysisSignal.Task.IsCompleted;

        public void ReleaseParsing() => ParsingGate?.TrySetResult(true);
    }
}
