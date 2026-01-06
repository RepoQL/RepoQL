using AwesomeAssertions;
using FakeItEasy;
using RepoQL.Contracts;
using RepoQL.Contracts.Data;
using RepoQL.Data.DuckDB;
using RepoQL.Indexing.Indexing;
using RepoQL.Indexing.Indexing.Commit;
using RepoQL.Indexing.Indexing.Pipelines;
using RepoQL.Indexing.Indexing.Pipelines.Analysis;
using RepoQL.Indexing.Indexing.Pipelines.Classification;
using RepoQL.Indexing.Indexing.PostProcessing;
using RepoQL.Indexing.Indexing.State;
using RepoQL.Testing;
using RepoQL.Testing.Indexing;

namespace RepoQL.Indexing.Tests.Indexing;

public class IndexingEngineTests
{
    [Test]
    [DisplayName("Skips unchanged artifacts when catalog confirms digest is current")]
    public async Task Given_CatalogReportsUpToDate_When_IndexItemAsync_Then_SkipsProcessing()
    {
        // Arrange
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

        var context = IndexingEngineTestFactory.Create(builder => builder.WithCatalog(catalog));

        A.CallTo(() => context.Classifier.ProcessItemAsync(A<IndexItem>._, A<CancellationToken>._))
            .Throws(new InvalidOperationException("Classifier should not run when catalog skips the item."));
        A.CallTo(() => context.Parser.ProcessItemAsync(A<IndexItem>._, A<CancellationToken>._))
            .Throws(new InvalidOperationException("Parser should not run when catalog skips the item."));
        A.CallTo(() => context.SingleFileAnalyzer.ProcessItemAsync(A<IndexItem>._, A<CancellationToken>._))
            .Throws(new InvalidOperationException("Analyzer should not run when catalog skips the item."));

        var item = IndexingTestItemFactory.CreateIndexItem();

        // Act
        await context.Engine.IndexItemAsync(item, CancellationToken.None);

        // Assert
        catalog.ShouldMatch(item.Uri, CatalogInvocationPlan.SkipProcessing);

        evaluatedDigest.Should().NotBeNull();
        item.DigestHex.Should().Be(evaluatedDigest);
        item.ExistingEntry.Should().Be(existing);
    }

    [Test]
    [DisplayName("Registers and clears pending catalog state when processing a changed artifact")]
    public async Task Given_CatalogRequiresReindex_When_IndexItemAsync_Then_ProcessesAndTracksPendingState()
    {
        // Arrange
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

        var committer = A.Fake<IIndexingCommitter>();
        var context = IndexingEngineTestFactory.Create(builder =>
        {
            builder.WithCatalog(catalog);
            builder.WithCommitter(committer);
        });

        var item = IndexingTestItemFactory.CreateIndexItem();

        // Act
        await context.Engine.IndexItemAsync(item, CancellationToken.None);

        // Assert
        catalog.ShouldMatch(item.Uri, CatalogInvocationPlan.Reindex);
        context.ShouldMatchPipeline(item, PipelineInvocationPlan.HotPathSuccess);

        evaluatedDigest.Should().NotBeNull();
        pendingDigest.Should().Be(evaluatedDigest);
        item.DigestHex.Should().Be(evaluatedDigest);
        item.ExistingEntry.Should().Be(existing);
    }

    [Test]
    [DisplayName("Dedup comparer rejects identical URI with same options")]
    public async Task Given_SameUriAndOptions_When_EnqueuedTwice_Then_SecondIsRejected()
    {
        var context = IndexingEngineTestFactory.Create();
        var item = IndexingTestItemFactory.Builder()
            .WithOptions(IndexItemOptions.Default)
            .Build();

        var first = await context.Engine.EnqueueIndexItemAsync(item, CancellationToken.None);
        first.Should().BeTrue();

        var duplicate = await context.Engine.EnqueueIndexItemAsync(item, CancellationToken.None);
        duplicate.Should().BeFalse();
    }

    [Test]
    [DisplayName("Dedup comparer allows identical URI when options differ")]
    public async Task Given_SameUriDifferentOptions_When_EnqueuedTwice_Then_BothAccepted()
    {
        var context = IndexingEngineTestFactory.Create();
        var baseBuilder = IndexingTestItemFactory.Builder().WithUri("file:///repo/doc.md");

        var staleItem = baseBuilder.WithOptions(IndexItemOptions.Default).Build();
        var forceItem = IndexingTestItemFactory.Builder()
            .WithUri("file:///repo/doc.md")
            .WithOptions(IndexItemOptions.Always)
            .Build();

        var first = await context.Engine.EnqueueIndexItemAsync(staleItem, CancellationToken.None);
        first.Should().BeTrue();

        var second = await context.Engine.EnqueueIndexItemAsync(forceItem, CancellationToken.None);
        second.Should().BeTrue();
    }

    [Test]
    [DisplayName("Does not leak epochs when duplicate enqueue is rejected")]
    public async Task Given_SameItemEnqueuedTwice_When_SecondIsDeduped_Then_EpochsRemainBalanced()
    {
        var pause = new TaskCompletionSource<PipelineResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var classifier = A.Fake<ClassificationPipeline>();
        A.CallTo(() => classifier.ProcessItemAsync(A<IndexItem>._, A<CancellationToken>._))
            .Returns(pause.Task);

        var context = IndexingEngineTestFactory.Create(builder =>
        {
            builder.WithClassifier(classifier);
        });

        var item = IndexingTestItemFactory.Builder()
            .WithOptions(IndexItemOptions.Always)
            .Build();

        var firstEnqueue = await context.Engine.EnqueueIndexItemAsync(item, CancellationToken.None);
        firstEnqueue.Should().BeTrue("initial enqueue should succeed");

        var duplicateEnqueue = await context.Engine.EnqueueIndexItemAsync(item, CancellationToken.None);
        duplicateEnqueue.Should().BeFalse("duplicate enqueue should be rejected by the queue");

        pause.TrySetResult(PipelineResult.Success);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var waited = await context.Engine.WaitForAsync(IndexingState.AllIdle, cts.Token);
        waited.Should().BeTrue("engine should return to AllIdle once the only item finishes");
    }

    [Test]
    [DisplayName("Clears pending catalog state even when pipeline terminates early")]
    public async Task Given_PipelineReturnsError_When_IndexItemAsync_Then_CatalogStateIsCleared()
    {
        // Arrange
        var catalog = A.Fake<IDocumentCatalog>();
        A.CallTo(() => catalog.EnsureInitializedAsync(A<CancellationToken>._))
            .Returns(Task.CompletedTask);

        A.CallTo(() => catalog.Evaluate(A<RepoUri>._, A<string>._))
            .Returns(new DocumentCatalogEvaluation(DocumentCatalogDecision.Reindex, null));

        var committer = A.Fake<IIndexingCommitter>();
        var context = IndexingEngineTestFactory.Create(builder =>
        {
            builder.WithCatalog(catalog);
            builder.WithCommitter(committer);
        });

        A.CallTo(() => context.Parser.ProcessItemAsync(A<IndexItem>._, A<CancellationToken>._))
            .Returns(Task.FromResult(PipelineResult.Error));
        A.CallTo(() => context.SingleFileAnalyzer.ProcessItemAsync(A<IndexItem>._, A<CancellationToken>._))
            .Throws(new InvalidOperationException("Analyzer should not run when parser fails."));

        var item = IndexingTestItemFactory.CreateIndexItem();

        // Act
        await context.Engine.IndexItemAsync(item, CancellationToken.None);

        // Assert
        catalog.ShouldMatch(item.Uri, CatalogInvocationPlan.Reindex);
        var failurePlan = PipelineInvocationPlan.HotPathSuccess with
        {
            SingleFileAnalyzer = InvocationExpectation.None,
            Committer = InvocationExpectation.None
        };
        context.ShouldMatchPipeline(item, failurePlan);
    }

    [Test]
    [DisplayName("Successfully processes item through all pipeline stages")]
    public async Task Given_AllPipelinesSucceed_When_ApplyIndexerPipeline_Then_ReturnsSuccess()
    {
        // Arrange
        var context = IndexingEngineTestFactory.Create();
        var item = IndexingTestItemFactory.CreateIndexItem();

        // Act
        var result = await context.Engine.ApplyIndexerPipeline(item, CancellationToken.None);

        // Assert
        result.Should().Be(PipelineResult.Success);
        context.ShouldMatchPipeline(item, PipelineInvocationPlan.Success);
    }

    [Test]
    [DisplayName("Short-circuits when classifier filters item")]
    public async Task Given_ClassifierFilters_When_ApplyIndexerPipeline_Then_ReturnsFilteredWithoutCallingSubsequentStages()
    {
        // Arrange
        var context = IndexingEngineTestFactory.Create();
        var item = IndexingTestItemFactory.CreateIndexItem();

        A.CallTo(() => context.Classifier.ProcessItemAsync(item, A<CancellationToken>._))
            .Returns(Task.FromResult(PipelineResult.Filtered));

        // Act
        var result = await context.Engine.ApplyIndexerPipeline(item, CancellationToken.None);

        // Assert
        result.Should().Be(PipelineResult.Filtered, "pipeline should short-circuit on non-success result");
        context.ShouldMatchPipeline(item, PipelineInvocationPlan.ShortCircuitAfterClassifier);
    }

    [Test]
    [DisplayName("Short-circuits when classifier returns error")]
    public async Task Given_ClassifierErrors_When_ApplyIndexerPipeline_Then_ReturnsErrorWithoutCallingSubsequentStages()
    {
        // Arrange
        var context = IndexingEngineTestFactory.Create();
        var item = IndexingTestItemFactory.CreateIndexItem();

        A.CallTo(() => context.Classifier.ProcessItemAsync(item, A<CancellationToken>._))
            .Returns(Task.FromResult(PipelineResult.Error));

        // Act
        var result = await context.Engine.ApplyIndexerPipeline(item, CancellationToken.None);

        // Assert
        result.Should().Be(PipelineResult.Error, "pipeline should propagate error from classifier");
        context.ShouldMatchPipeline(item, PipelineInvocationPlan.ShortCircuitAfterClassifier);
    }

    [Test]
    [DisplayName("Short-circuits when parser fails after successful classification")]
    public async Task Given_ParserFails_When_ApplyIndexerPipeline_Then_ReturnsErrorWithoutCallingAnalyzer()
    {
        // Arrange
        var context = IndexingEngineTestFactory.Create();
        var item = IndexingTestItemFactory.CreateIndexItem();

        A.CallTo(() => context.Classifier.ProcessItemAsync(item, A<CancellationToken>._))
            .Returns(Task.FromResult(PipelineResult.Success));
        A.CallTo(() => context.Parser.ProcessItemAsync(item, A<CancellationToken>._))
            .Returns(Task.FromResult(PipelineResult.Error));

        // Act
        var result = await context.Engine.ApplyIndexerPipeline(item, CancellationToken.None);

        // Assert
        result.Should().Be(PipelineResult.Error, "pipeline should propagate error from parser");
        context.ShouldMatchPipeline(item, PipelineInvocationPlan.ShortCircuitAfterParser);
    }

    [Test]
    [DisplayName("Returns analyzer result when classifier and parser succeed")]
    public async Task Given_AnalyzerFails_When_ApplyIndexerPipeline_Then_ReturnsAnalyzerResult()
    {
        // Arrange
        var context = IndexingEngineTestFactory.Create();
        var item = IndexingTestItemFactory.CreateIndexItem();

        A.CallTo(() => context.Classifier.ProcessItemAsync(item, A<CancellationToken>._))
            .Returns(Task.FromResult(PipelineResult.Success));
        A.CallTo(() => context.Parser.ProcessItemAsync(item, A<CancellationToken>._))
            .Returns(Task.FromResult(PipelineResult.Success));
        A.CallTo(() => context.SingleFileAnalyzer.ProcessItemAsync(item, A<CancellationToken>._))
            .Returns(Task.FromResult(PipelineResult.Error));

        // Act
        var result = await context.Engine.ApplyIndexerPipeline(item, CancellationToken.None);

        // Assert
        result.Should().Be(PipelineResult.Error, "final stage result should be returned");
        context.ShouldMatchPipeline(item, PipelineInvocationPlan.Success);
    }

    [Test]
    [Arguments(PipelineResult.Cancelled)]
    [Arguments(PipelineResult.Filtered)]
    [DisplayName("Respects cancellation and filtered states from any pipeline stage")]
    public async Task Given_PipelineReturnsNonSuccessResult_When_ApplyIndexerPipeline_Then_ShortCircuits(
        PipelineResult pipelineResult)
    {
        // Arrange
        var context = IndexingEngineTestFactory.Create();
        var item = IndexingTestItemFactory.CreateIndexItem();

        A.CallTo(() => context.Classifier.ProcessItemAsync(item, A<CancellationToken>._))
            .Returns(Task.FromResult(PipelineResult.Success));
        A.CallTo(() => context.Parser.ProcessItemAsync(item, A<CancellationToken>._))
            .Returns(Task.FromResult(pipelineResult));

        // Act
        var result = await context.Engine.ApplyIndexerPipeline(item, CancellationToken.None);

        // Assert
        result.Should().Be(pipelineResult);
        var plan = PipelineInvocationPlan.Success with
        {
            SingleFileAnalyzer = InvocationExpectation.None
        };
        context.ShouldMatchPipeline(item, plan);
    }

    [Test]
    [Timeout(15_000)]
    [DisplayName("StateChanged fires and WaitForAsync waits until all stages are idle")]
    public async Task Given_ItemProcessing_When_WaitingForAllIdle_Then_CompletesAfterStagesFinish(CancellationToken token)
    {
        // Arrange
        var gate = NewTaskCompletionSource<bool>();
        var context = IndexingEngineTestFactory.Create();

        A.CallTo(() => context.Classifier.ProcessItemAsync(A<IndexItem>._, A<CancellationToken>._))
            .ReturnsLazily(async _ =>
            {
                await gate.Task.ConfigureAwait(false);
                return PipelineResult.Success;
            });
        A.CallTo(() => context.Parser.ProcessItemAsync(A<IndexItem>._, A<CancellationToken>._))
            .Returns(Task.FromResult(PipelineResult.Success));
        A.CallTo(() => context.SingleFileAnalyzer.ProcessItemAsync(A<IndexItem>._, A<CancellationToken>._))
            .Returns(Task.FromResult(PipelineResult.Success));

        var transitions = new List<(IndexingState OldState, IndexingState NewState)>();
        var busySignal = NewTaskCompletionSource<bool>();
        context.Engine.StateChanged += (_, args) =>
        {
            transitions.Add((args.OldState, args.NewState));
            if (args.NewState.HasFlag(IndexingState.ClassificationBusy) &&
                !args.OldState.HasFlag(IndexingState.ClassificationBusy))
            {
                busySignal.TrySetResult(true);
            }
        };

        var item = IndexingTestItemFactory.CreateIndexItem();

        var processingTask = context.Engine.IndexItemAsync(item, token);
        await busySignal.Task;
        var waitTask = context.Engine.WaitForAsync(IndexingState.AllIdle, token).AsTask();
        waitTask.IsCompleted.Should().BeFalse("engine should report busy while classification is blocked");

        gate.SetResult(true);

        await processingTask.WaitAsync(token);
        await waitTask;

        // Assert
        transitions.Should().NotBeEmpty();
        transitions.Should().Contain(t => t.NewState.HasFlag(IndexingState.ClassificationBusy));
        transitions.Last().NewState.Should().Be(IndexingState.AllIdle);
        context.ShouldMatchPipeline(item, PipelineInvocationPlan.HotPathSuccess);
    }

    [Test]
    [Timeout(15_000)]
    [DisplayName("WaitForAsync signals once hot path starts processing")]
    public async Task Given_WorkQueued_When_WaitingForStarted_Then_CompletesAfterBusy(CancellationToken token)
    {
        var gate = NewTaskCompletionSource<bool>();
        var busySignal = NewTaskCompletionSource<bool>();

        var classifier = A.Fake<ClassificationPipeline>();
        A.CallTo(() => classifier.ProcessItemAsync(A<IndexItem>._, A<CancellationToken>._))
            .ReturnsLazily(async _ =>
            {
                busySignal.TrySetResult(true);
                await gate.Task.ConfigureAwait(false);
                return PipelineResult.Success;
            });

        var context = IndexingEngineTestFactory.Create(builder => builder.WithClassifier(classifier));
        var item = IndexingTestItemFactory.CreateIndexItem();

        var waitTask = context.Engine.WaitForAsync(IndexingState.Started, token).AsTask();
        var processingTask = context.Engine.IndexItemAsync(item, token);

        await busySignal.Task.WaitAsync(token);
        context.Engine.State.HasFlag(IndexingState.Started).Should().BeTrue("started flag should raise once any stage begins work");
        await waitTask.WaitAsync(token);
        context.Engine.State.HasFlag(IndexingState.ClassificationBusy).Should().BeTrue("started should coincide with an active stage");

        gate.TrySetResult(true);
        await processingTask;
        await context.Engine.WaitForAsync(IndexingState.AllIdle, token);

        context.Engine.State.HasFlag(IndexingState.Started).Should().BeFalse("started flag clears when the hot path drains");
    }

    [Test]
    [Timeout(15_000)]
    [DisplayName("Raises HotPathIdle when pending work drains")]
    public async Task Given_WorkCompletes_When_Idle_Then_HotPathIdleFires(CancellationToken token)
    {
        await using var engine = CreateEngineForIdleTests();
        var artifact = CreateRawArtifact("file:///repo/hot1.md");

        var idleTask = engine.AwaitHotPathIdleAsync();

        await engine.EnqueueItemAsync(artifact, IndexItemOptions.Default, CancellationToken.None);
        (await idleTask.WaitAsync(token)).Should().Be(0);
    }

    [Test]
    [Timeout(15_000)]
    [DisplayName("HotPathIdle waits for pending items before signalling")]
    public async Task Given_WorkPending_When_WaitingForIdle_Then_EventDelays(CancellationToken token)
    {
        var gate = NewTaskCompletionSource<bool>();
        await using var engine = CreateEngineForIdleTests(gate);
        var artifact1 = CreateRawArtifact("file:///repo/pending-a.md");
        var artifact2 = CreateRawArtifact("file:///repo/pending-b.md");

        var idleTask = engine.AwaitHotPathIdleAsync();

        await engine.EnqueueItemAsync(artifact1, IndexItemOptions.Default, CancellationToken.None);
        await engine.EnqueueItemAsync(artifact2, IndexItemOptions.Default, CancellationToken.None);

        await Task.Delay(100);
        idleTask.IsCompleted.Should().BeFalse("event should not fire while work is still running");

        gate.SetResult(true);
        (await idleTask.WaitAsync(token)).Should().Be(0);
    }

    [Test]
    [Timeout(30_000)]
    [DisplayName("HotPathIdle reports the epoch that drained")]
    public async Task Given_NewEpoch_When_WorkCompletes_Then_ReportsEpoch(CancellationToken token)
    {
        await using var engine = CreateEngineForIdleTests();

        var firstIdle = engine.AwaitHotPathIdleAsync();

        await engine.EnqueueItemAsync(CreateRawArtifact("file:///repo/epoch0.md"), IndexItemOptions.Default, CancellationToken.None);
        (await firstIdle.WaitAsync(token)).Should().Be(0);

        var nextEpoch = engine.BeginNewEpoch();
        var secondIdle = engine.AwaitHotPathIdleAsync();

        await engine.EnqueueItemAsync(CreateRawArtifact("file:///repo/epoch1.md"), IndexItemOptions.Default, CancellationToken.None);
        (await secondIdle.WaitAsync(token)).Should().Be(nextEpoch);
    }

    [Test]
    [Timeout(30_000)]
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
        await using var engine = CreateEngineForAnalysisTests(
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
                await deleteApplied.Task.WaitAsync(token);
                vectorApplied.TrySetResult(true);
            });

        var analysisSignal = NewTaskCompletionSource<bool>();
        await using var engine = CreateEngineForAnalysisTests(
            parsingGate: null,
            multiFileSignal: analysisSignal,
            pruner: pruner,
            vectorCoordinator: vector);

        await engine.EnqueueItemAsync(CreateRawArtifact("file:///repo/vector-upsert.md"), IndexItemOptions.Default, token);

        await analysisSignal.Task.WaitAsync(token);
        vectorApplied.Task.IsCompleted.Should().BeTrue("Vector updates should complete before analysis starts.");
        vector.ShouldHaveAppliedVectorDeletes(pruneResult.DeletedArtifacts);
        vector.ShouldHaveAppliedVectors(InvocationExpectation.AtLeastOnce);
    }

    [Test]
    [Timeout(15_000)]
    [DisplayName("New work after idle runs in a new epoch so vectors refresh again")]
    public async Task Given_SubsequentWork_When_PreviousEpochCompleted_Then_VectorRefreshesAgain(CancellationToken token)
    {
        var firstVector = NewTaskCompletionSource<bool>();
        var secondVector = NewTaskCompletionSource<bool>();
        var observedEpochs = new List<long>();

        var vector = A.Fake<IVectorIndexCoordinator>();
        A.CallTo(() => vector.ApplyAsync(A<IndexItem>._, A<CancellationToken>._))
            .ReturnsLazily(call =>
            {
                var epoch = call.GetArgument<IndexItem>(0)!.Epoch;
                lock (observedEpochs)
                {
                    observedEpochs.Add(epoch);
                    if (observedEpochs.Count == 1)
                    {
                        firstVector.TrySetResult(true);
                    }
                    else if (observedEpochs.Count == 2)
                    {
                        secondVector.TrySetResult(true);
                    }
                }

                return Task.CompletedTask;
            });

        var analysisSignal = NewTaskCompletionSource<bool>();
        await using var engine = CreateEngineForAnalysisTests(
            parsingGate: null,
            multiFileSignal: analysisSignal,
            vectorCoordinator: vector);

        await engine.EnqueueItemAsync(CreateRawArtifact("file:///repo/vector-epoch-a.md"), IndexItemOptions.Default, token);
        await firstVector.Task.WaitAsync(token);
        await engine.AnalysisQueue.WhenIdleAsync().WaitAsync(token);

        await engine.EnqueueItemAsync(CreateRawArtifact("file:///repo/vector-epoch-b.md"), IndexItemOptions.Default, token);
        await secondVector.Task.WaitAsync(token);
        await engine.AnalysisQueue.WhenIdleAsync().WaitAsync(token);

        observedEpochs.Should().HaveCount(2);
        observedEpochs[1].Should().BeGreaterThan(observedEpochs[0]);
    }

    [Test]
    [Timeout(15_000)]
    [DisplayName("Pruning deletes are issued through the database")]
    public async Task Given_StaleDocuments_When_PrunerFindsThem_Then_DeleteOperationSentToDatabase(CancellationToken token)
    {
        var deleteUri = CreateUri("file:///repo/stale.md");

        // Create a real database with a document that will be "stale"
        using var db = new DuckDbDataStore();

        // Insert the stale document first
        var staleArtifact = new Contracts.Models.Artifact
        {
            Id = Guid.NewGuid(),
            Digest = "stale-digest",
            Size = 10,
            MediaType = SemanticMediaType.Parse("text/markdown")
        };
        var staleNode = new Contracts.Models.Node
        {
            Id = Guid.NewGuid(),
            Kind = "document",
            Uri = deleteUri,
            ArtifactId = staleArtifact.Id,
            Props = new System.Text.Json.Nodes.JsonObject()
        };
        db.IndexArtifact(new ParsedArtifact { Artifact = staleArtifact, DocumentNode = staleNode });

        // Verify it exists
        db.GetDocumentByUri(deleteUri).Should().NotBeNull("stale document should exist before pruning");

        var pruner = A.Fake<IArtifactPruner>();
        A.CallTo(() => pruner.PruneAsync(A<IReadOnlyCollection<IndexItem>>._, A<CancellationToken>._))
            .Returns(Task.FromResult(new PruningResult(new[] { deleteUri })));

        var vector = NullVectorIndexCoordinator.Instance;
        var analysisSignal = NewTaskCompletionSource<bool>();
        await using var engine = CreateEngineForAnalysisTests(
            parsingGate: null,
            multiFileSignal: analysisSignal,
            pruner: pruner,
            vectorCoordinator: vector,
            dataStore: db);

        await engine.EnqueueItemAsync(CreateRawArtifact("file:///repo/live.md"), IndexItemOptions.Default, token);

        await analysisSignal.Task.WaitAsync(token);
        db.ShouldHaveDeletedDocuments(deleteUri);
    }

    private static RepoUri CreateUri(string value)
    {
        return RepoUri.TryParse(value, out var parsed)
            ? parsed!
            : throw new InvalidOperationException($"Unable to parse URI '{value}'.");
    }

    private static RawArtifact CreateRawArtifact(string uri)
        => IndexingTestItemFactory.CreateRawArtifact(uri);

    private static IndexingEngine CreateEngineForIdleTests(
        TaskCompletionSource<bool>? parsingGate = null,
        IArtifactPruner? pruner = null,
        IVectorIndexCoordinator? vectorCoordinator = null)
    {
        var context = IndexingEngineTestFactory.Create(builder =>
        {
            if (pruner is not null)
            {
                builder.WithArtifactPruner(pruner);
            }

            if (vectorCoordinator is not null)
            {
                builder.WithVectorCoordinator(vectorCoordinator);
            }
        });

        A.CallTo(() => context.Classifier.ProcessItemAsync(A<IndexItem>._, A<CancellationToken>._))
            .Returns(Task.FromResult(PipelineResult.Success));

        if (parsingGate is null)
        {
            A.CallTo(() => context.Parser.ProcessItemAsync(A<IndexItem>._, A<CancellationToken>._))
                .Returns(Task.FromResult(PipelineResult.Success));
        }
        else
        {
            A.CallTo(() => context.Parser.ProcessItemAsync(A<IndexItem>._, A<CancellationToken>._))
                .ReturnsLazily(async _ =>
                {
                    await parsingGate.Task.ConfigureAwait(false);
                    return PipelineResult.Success;
                });
        }

        A.CallTo(() => context.SingleFileAnalyzer.ProcessItemAsync(A<IndexItem>._, A<CancellationToken>._))
            .Returns(Task.FromResult(PipelineResult.Success));

        return context.Engine;
    }

    private static IndexingEngine CreateEngineForAnalysisTests(
        TaskCompletionSource<bool>? parsingGate,
        TaskCompletionSource<bool> multiFileSignal,
        IArtifactPruner? pruner = null,
        IVectorIndexCoordinator? vectorCoordinator = null,
        DuckDbDataStore? dataStore = null)
    {
        var context = IndexingEngineTestFactory.Create(builder =>
        {
            builder.WithOptions(new IndexingEngineOptions
            {
                IndexingQueueSize = 32,
                IndexingWorkers = 1,
                AnalysisQueueSize = 32,
                AnalysisWorkers = 1
            });

            if (pruner is not null)
            {
                builder.WithArtifactPruner(pruner);
            }

            if (vectorCoordinator is not null)
            {
                builder.WithVectorCoordinator(vectorCoordinator);
            }

            if (dataStore is not null)
            {
                builder.WithDataStore(dataStore);
            }
        });

        A.CallTo(() => context.Classifier.ProcessItemAsync(A<IndexItem>._, A<CancellationToken>._))
            .Returns(Task.FromResult(PipelineResult.Success));

        if (parsingGate is null)
        {
            A.CallTo(() => context.Parser.ProcessItemAsync(A<IndexItem>._, A<CancellationToken>._))
                .Returns(Task.FromResult(PipelineResult.Success));
        }
        else
        {
            A.CallTo(() => context.Parser.ProcessItemAsync(A<IndexItem>._, A<CancellationToken>._))
                .ReturnsLazily(async _ =>
                {
                    await parsingGate.Task.ConfigureAwait(false);
                    return PipelineResult.Success;
                });
        }

        A.CallTo(() => context.SingleFileAnalyzer.ProcessItemAsync(A<IndexItem>._, A<CancellationToken>._))
            .Returns(Task.FromResult(PipelineResult.Success));
        A.CallTo(() => context.MultiFileAnalyzer.ProcessItemAsync(A<IAnnotatedArtifact>._, A<CancellationToken>._))
            .ReturnsLazily(_ =>
            {
                multiFileSignal.TrySetResult(true);
                return Task.FromResult(PipelineResult.Success);
            });
        A.CallTo(() => context.IndexRebuilder.ProcessItemAsync(A<IAnnotatedArtifact>._, A<CancellationToken>._))
            .Returns(Task.FromResult(PipelineResult.Success));

        return context.Engine;
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
