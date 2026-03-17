using AwesomeAssertions;
using FakeItEasy;
using Microsoft.Extensions.Logging.Abstractions;
using RepoQL.Contracts;
using RepoQL.Contracts.Data;
using RepoQL.Contracts.Embeddings;
using RepoQL.Contracts.Models;
using RepoQL.Data.DuckDB;
using RepoQL.Indexing.Indexing;
using RepoQL.Indexing.Indexing.Commit;
using RepoQL.Indexing.Indexing.Pipelines;
using RepoQL.Indexing.Indexing.Pipelines.Analysis;
using RepoQL.Indexing.Indexing.Pipelines.Classification;
using RepoQL.Indexing.Indexing.Pipelines.Parsing;
using RepoQL.Indexing.Indexing.PostProcessing;
using RepoQL.Indexing.Indexing.State;
using Microsoft.Extensions.FileProviders;
using RepoQL.FileSystem.Abstractions;
using RepoQL.Testing;
using RepoQL.Testing.Indexing;
using System.Collections.Concurrent;
using ModelSpan = RepoQL.Contracts.Models.Span;

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
        DocumentCatalogEntry? existingEntryAtCommit = null;
        A.CallTo(() => committer.CommitAsync(A<IndexItem>._, A<CancellationToken>._))
            .Invokes(call => existingEntryAtCommit = call.GetArgument<IndexItem>(0)?.ExistingEntry);
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
        existingEntryAtCommit.Should().Be(existing);
        item.ExistingEntry.Should().BeNull();
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
    [DisplayName("Dedup comparer rejects identical URI even when options differ")]
    public async Task Given_SameUriDifferentOptions_When_EnqueuedTwice_Then_SecondIsDeduped()
    {
        var pause = new TaskCompletionSource<PipelineResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var classifier = A.Fake<ClassificationPipeline>();
        A.CallTo(() => classifier.ProcessItemAsync(A<IndexItem>._, A<CancellationToken>._))
            .Returns(pause.Task);

        var context = IndexingEngineTestFactory.Create(builder =>
        {
            builder.WithClassifier(classifier);
        });

        var baseBuilder = IndexingTestItemFactory.Builder().WithUri("file:///repo/doc.md");

        var staleItem = baseBuilder.WithOptions(IndexItemOptions.Default).Build();
        var forceItem = IndexingTestItemFactory.Builder()
            .WithUri("file:///repo/doc.md")
            .WithOptions(IndexItemOptions.Always)
            .Build();

        var first = await context.Engine.EnqueueIndexItemAsync(staleItem, CancellationToken.None);
        first.Should().BeTrue();

        // Same URI is deduplicated regardless of options; MarkRequeue captures
        // the merged options so the item is re-processed after the first completes.
        var second = await context.Engine.EnqueueIndexItemAsync(forceItem, CancellationToken.None);
        second.Should().BeFalse();

        pause.TrySetResult(PipelineResult.Success);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var waited = await context.Engine.WaitForAsync(IndexingState.AllIdle, cts.Token);
        waited.Should().BeTrue("engine should return to AllIdle once the queued item finishes");
    }

    [Test]
    [DisplayName("Dedup comparer rejects URI variants that differ only by casing")]
    public async Task Given_CaseVariantUris_When_EnqueuedTwice_Then_SecondIsDeduped()
    {
        var context = IndexingEngineTestFactory.Create();
        var firstItem = IndexingTestItemFactory.Builder()
            .WithUri("file:///Repo/Docs/ReadMe.md")
            .Build();
        var secondItem = IndexingTestItemFactory.Builder()
            .WithUri("file:///repo/docs/readme.md")
            .Build();

        var first = await context.Engine.EnqueueIndexItemAsync(firstItem, CancellationToken.None);
        first.Should().BeTrue();

        var second = await context.Engine.EnqueueIndexItemAsync(secondItem, CancellationToken.None);
        second.Should().BeFalse();
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
    [Timeout(15_000)]
    [DisplayName("FM-001: Cancelled pipeline results caused by timeout still defer to idle retry")]
    public async Task Given_StageReturnsCancelledOnTimeout_When_ItemTimesOut_Then_DeferredRetryStillRuns(CancellationToken token)
    {
        var context = IndexingEngineTestFactory.Create(builder =>
        {
            builder.WithOptions(new IndexingEngineOptions
            {
                IndexingQueueSize = 32,
                IndexingWorkers = 1,
                AnalysisQueueSize = 32,
                AnalysisWorkers = 1,
                HotPathItemTimeout = TimeSpan.FromMilliseconds(200)
            });
        });

        A.CallTo(() => context.Classifier.ProcessItemAsync(A<IndexItem>._, A<CancellationToken>._))
            .ReturnsLazily(async call =>
            {
                var ct = call.GetArgument<CancellationToken>(1);
                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(1), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return PipelineResult.Cancelled;
                }

                return PipelineResult.Success;
            });

        await using var engine = context.Engine;
        await engine.EnqueueItemAsync(CreateRawArtifact("file:///repo/cancelled-timeout.md"), IndexItemOptions.Default, token);

        using var timeoutWait = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeoutWait.CancelAfter(TimeSpan.FromSeconds(5));
        while (engine.HotPathTimeoutCount == 0 ||
               (engine.GetPendingDeferredRetryItems().Count == 0 && engine.GetDeferredRetryInFlightItems().Count == 0))
        {
            await Task.Delay(25, timeoutWait.Token);
        }

        engine.HotPathTimeoutCount.Should().Be(1);
        engine.DeferredToIdleCount.Should().Be(1);
    }

    [Test]
    [Timeout(15_000)]
    [DisplayName("FM-001: Timeout hands pending catalog state off cleanly to deferred retry")]
    public async Task Given_CooperativeHotPathTimeout_When_ItemTimesOut_Then_CatalogPendingDigestTransfersToDeferredRetry(CancellationToken token)
    {
        var catalog = new DocumentCatalog(NullDocumentCatalogDataSource.Instance);
        await catalog.EnsureInitializedAsync(token);

        var classifierEntered = NewTaskCompletionSource<bool>();

        var context = IndexingEngineTestFactory.Create(builder =>
        {
            builder.WithCatalog(catalog);
            builder.WithOptions(new IndexingEngineOptions
            {
                IndexingQueueSize = 32,
                IndexingWorkers = 1,
                AnalysisQueueSize = 32,
                AnalysisWorkers = 1,
                HotPathItemTimeout = TimeSpan.FromMilliseconds(300)
            });
        });

        A.CallTo(() => context.Classifier.ProcessItemAsync(A<IndexItem>._, A<CancellationToken>._))
            .ReturnsLazily(async call =>
            {
                classifierEntered.TrySetResult(true);
                var ct = call.GetArgument<CancellationToken>(1);
                await Task.Delay(TimeSpan.FromMinutes(1), ct).ConfigureAwait(false);
                return PipelineResult.Success;
            });

        await using var engine = context.Engine;
        await engine.EnqueueItemAsync(CreateRawArtifact("file:///repo/timeout-catalog.md"), IndexItemOptions.Default, token);

        await classifierEntered.Task.WaitAsync(token);
        catalog.PendingDigestCount.Should().Be(1, "pending digest should be registered before timeout cleanup runs");

        using var timeoutWait = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeoutWait.CancelAfter(TimeSpan.FromSeconds(5));
        while (engine.HotPathTimeoutCount == 0)
        {
            await Task.Delay(25, timeoutWait.Token);
        }

        while (engine.GetPendingDeferredRetryItems().Count == 0 && engine.GetDeferredRetryInFlightItems().Count == 0)
        {
            await Task.Delay(25, timeoutWait.Token);
        }

        engine.HotPathTimeoutCount.Should().Be(1, "item should be marked timed out");
        engine.DeferredToIdleCount.Should().Be(1, "the timed-out item should be handed off to idle retry");
        catalog.PendingDigestCount.Should().BeLessThanOrEqualTo(1, "timeout cleanup should clear the original pending digest and only re-register it once the deferred retry actually runs");
    }

    [Test]
    [Timeout(15_000)]
    [DisplayName("FM-001: Timed-out hot-path item is deferred to idle retry without blocking later work")]
    public async Task Given_HotPathTimeout_When_ItemTimesOut_Then_ItemIsDeferredToIdleRetry(CancellationToken token)
    {
        var fastItemProcessed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var context = IndexingEngineTestFactory.Create(builder =>
        {
            builder.WithOptions(new IndexingEngineOptions
            {
                IndexingQueueSize = 32,
                IndexingWorkers = 1,
                AnalysisQueueSize = 32,
                AnalysisWorkers = 1,
                HotPathItemTimeout = TimeSpan.FromMilliseconds(300)
            });
        });

        A.CallTo(() => context.Classifier.ProcessItemAsync(A<IndexItem>._, A<CancellationToken>._))
            .ReturnsLazily(async call =>
            {
                var item = call.GetArgument<IndexItem>(0);
                var ct = call.GetArgument<CancellationToken>(1);
                if (item.Uri.ToString().Contains("slow", StringComparison.Ordinal))
                {
                    await Task.Delay(TimeSpan.FromMinutes(1), ct).ConfigureAwait(false);
                }
                else
                {
                    fastItemProcessed.TrySetResult(true);
                }
                return PipelineResult.Success;
            });

        await using var engine = context.Engine;
        await engine.EnqueueItemAsync(CreateRawArtifact("file:///repo/slow-timeout.md"), IndexItemOptions.Default, token);
        await engine.EnqueueItemAsync(CreateRawArtifact("file:///repo/fast-after-timeout.md"), IndexItemOptions.Default, token);

        using var timeoutWait = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeoutWait.CancelAfter(TimeSpan.FromSeconds(5));
        while (
            engine.HotPathTimeoutCount == 0 ||
            (engine.GetPendingDeferredRetryItems().Count == 0 && engine.GetDeferredRetryInFlightItems().Count == 0))
        {
            await Task.Delay(25, timeoutWait.Token);
        }

        await fastItemProcessed.Task.WaitAsync(timeoutWait.Token);
        engine.DeferredToIdleCount.Should().Be(1);
        engine.GetPendingDeferredRetryItems()
            .Concat(engine.GetDeferredRetryInFlightItems().Select(info => info.Item))
            .Should()
            .ContainSingle(item => item.Uri.ToString().Contains("slow-timeout", StringComparison.Ordinal));
    }

    [Test]
    [Timeout(15_000)]
    [DisplayName("FM-001: Non-cooperative hot-path work still times out and hands off to deferred retry")]
    public async Task Given_HotPathWorkIgnoresCancellation_When_TimeoutExpires_Then_DeferredRetryStillStarts(CancellationToken token)
    {
        var neverCompletes = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDeferred = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var deferredStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var deferredCompleted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var context = IndexingEngineTestFactory.Create(builder =>
        {
            builder.WithOptions(new IndexingEngineOptions
            {
                IndexingQueueSize = 32,
                IndexingWorkers = 1,
                AnalysisQueueSize = 32,
                AnalysisWorkers = 1,
                HotPathItemTimeout = TimeSpan.FromMilliseconds(100)
            });
        });

        A.CallTo(() => context.Classifier.ProcessItemAsync(A<IndexItem>._, A<CancellationToken>._))
            .ReturnsLazily(async call =>
            {
                var item = call.GetArgument<IndexItem>(0);
                var ct = call.GetArgument<CancellationToken>(1);
                if (!item.IsDeferredRetry)
                {
                    await neverCompletes.Task.ConfigureAwait(false);
                    return PipelineResult.Success;
                }

                deferredStarted.TrySetResult(true);
                try
                {
                    await releaseDeferred.Task.WaitAsync(ct).ConfigureAwait(false);
                }
                finally
                {
                    deferredCompleted.TrySetResult(true);
                }

                return PipelineResult.Success;
            });

        await using var engine = context.Engine;
        await engine.EnqueueItemAsync(CreateRawArtifact("file:///repo/non-cooperative-timeout.md"), IndexItemOptions.Default, token);

        using var settle = CancellationTokenSource.CreateLinkedTokenSource(token);
        settle.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            while (engine.HotPathTimeoutCount == 0 || engine.DeferredToIdleCount == 0)
            {
                await Task.Delay(25, settle.Token);
            }

            await deferredStarted.Task.WaitAsync(settle.Token);
            engine.HotPathTimeoutCount.Should().Be(1);
            engine.DeferredToIdleCount.Should().Be(1);
        }
        finally
        {
            releaseDeferred.TrySetResult(true);
            neverCompletes.TrySetResult(true);
            if (deferredStarted.Task.IsCompleted)
                await deferredCompleted.Task.WaitAsync(token).ConfigureAwait(false);
        }
    }

    [Test]
    [Timeout(15_000)]
    [DisplayName("FM-001: Deferred retry ownership blocks duplicate hot-path enqueue for the same URI")]
    public async Task Given_DeferredRetryPending_When_SameUriEnqueuedAgain_Then_FreshHotPathEnqueueIsRejected(CancellationToken token)
    {
        var releaseDeferred = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var deferredCompleted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var uri = "file:///repo/dedup-timeout.md";

        var context = IndexingEngineTestFactory.Create(builder =>
        {
            builder.WithOptions(new IndexingEngineOptions
            {
                IndexingQueueSize = 32,
                IndexingWorkers = 1,
                AnalysisQueueSize = 32,
                AnalysisWorkers = 1,
                HotPathItemTimeout = TimeSpan.FromMilliseconds(100)
            });
        });

        A.CallTo(() => context.Classifier.ProcessItemAsync(A<IndexItem>._, A<CancellationToken>._))
            .ReturnsLazily(async call =>
            {
                var item = call.GetArgument<IndexItem>(0);
                var ct = call.GetArgument<CancellationToken>(1);

                if (!item.IsDeferredRetry)
                {
                    await Task.Delay(TimeSpan.FromMinutes(1), ct).ConfigureAwait(false);
                }
                else
                {
                    try
                    {
                        await releaseDeferred.Task.WaitAsync(ct).ConfigureAwait(false);
                    }
                    finally
                    {
                        deferredCompleted.TrySetResult(true);
                    }
                }

                return PipelineResult.Success;
            });

        await using var engine = context.Engine;
        await engine.EnqueueItemAsync(CreateRawArtifact(uri), IndexItemOptions.Default, token);

        using var timeoutWait = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeoutWait.CancelAfter(TimeSpan.FromSeconds(5));
        while (engine.HotPathTimeoutCount == 0 ||
               (engine.GetPendingDeferredRetryItems().Count == 0 && engine.GetDeferredRetryInFlightItems().Count == 0))
        {
            await Task.Delay(25, timeoutWait.Token);
        }

        var duplicate = await engine.EnqueueIndexItemAsync(
            new IndexItem(CreateRawArtifact(uri), IndexItemOptions.Default),
            timeoutWait.Token);

        duplicate.Should().BeFalse();

        releaseDeferred.TrySetResult(true);

        using var cleanupWait = CancellationTokenSource.CreateLinkedTokenSource(token);
        cleanupWait.CancelAfter(TimeSpan.FromSeconds(10));
        await deferredCompleted.Task.WaitAsync(cleanupWait.Token);
    }

    [Test]
    [Timeout(15_000)]
    [DisplayName("FM-001: Second timeout during idle retry marks the file failed")]
    public async Task Given_DeferredRetryTimeout_When_ItemTimesOutAgain_Then_FileIsMarkedFailed(CancellationToken token)
    {
        var registry = new UriRegistry();

        var context = IndexingEngineTestFactory.Create(builder =>
        {
            builder.WithUriRegistry(registry);
            builder.WithOptions(new IndexingEngineOptions
            {
                IndexingQueueSize = 32,
                IndexingWorkers = 1,
                AnalysisQueueSize = 32,
                AnalysisWorkers = 1,
                HotPathItemTimeout = TimeSpan.FromMilliseconds(100),
                AnalysisItemTimeout = TimeSpan.FromMilliseconds(150)
            });
        });

        A.CallTo(() => context.Classifier.ProcessItemAsync(A<IndexItem>._, A<CancellationToken>._))
            .ReturnsLazily(async call =>
            {
                var ct = call.GetArgument<CancellationToken>(1);
                await Task.Delay(TimeSpan.FromMinutes(1), ct).ConfigureAwait(false);
                return PipelineResult.Success;
            });

        await using var engine = context.Engine;
        var uri = CreateRawArtifact("file:///repo/retry-timeout.md").Uri;
        registry.TryRegisterDiscovered(uri);
        await engine.EnqueueItemAsync(CreateRawArtifact("file:///repo/retry-timeout.md"), IndexItemOptions.Default, token);

        using var settle = CancellationTokenSource.CreateLinkedTokenSource(token);
        settle.CancelAfter(TimeSpan.FromSeconds(15));
        while (engine.DeferredRetryTimeoutCount == 0 ||
               !registry.TryGetValue(uri, out var entry) ||
               entry.Status != UriStatus.Failed)
        {
            await Task.Delay(25, settle.Token);
        }

        engine.HotPathTimeoutCount.Should().Be(1);
        engine.DeferredToIdleCount.Should().Be(1);
        engine.DeferredRetryTimeoutCount.Should().Be(1);
        registry[uri].Error.Should().Contain("idle retry timed out");
    }

    [Test]
    [DisplayName("Idle loop supervisor restarts twice before a successful run")]
    public async Task Given_IdleSupervisor_When_RunFailsTwiceThenSucceeds_Then_RestartsWithFixedBackoff()
    {
        var observedFailures = new List<string>();
        var observedDelays = new List<TimeSpan>();
        var attempts = 0;
        using var cts = new CancellationTokenSource();

        await IdleLoopSupervisor.RunAsync(
            loopName: "idle test loop",
            runLoopAsync: async cancellationToken =>
            {
                attempts++;
                if (attempts <= 2)
                    throw new InvalidOperationException($"boom-{attempts}");

                cts.Cancel();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            },
            onFailure: ex => observedFailures.Add(ex.Message),
            logger: NullLogger.Instance,
            cancellationToken: cts.Token,
            delayAsync: (delay, _) =>
            {
                observedDelays.Add(delay);
                return Task.CompletedTask;
            });

        attempts.Should().Be(3);
        observedFailures.Should().BeEquivalentTo(["boom-1", "boom-2"]);
        observedDelays.Should().BeEquivalentTo(
            [TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(1)],
            options => options.WithStrictOrdering());
    }

    [Test]
    [DisplayName("Idle loop supervisor stops after the third restart attempt")]
    public async Task Given_IdleSupervisor_When_RunAlwaysFails_Then_RestartsThreeTimesAndStops()
    {
        var observedFailures = new List<string>();
        var observedDelays = new List<TimeSpan>();
        var attempts = 0;

        await IdleLoopSupervisor.RunAsync(
            loopName: "idle test loop",
            runLoopAsync: _ =>
            {
                attempts++;
                throw new InvalidOperationException($"boom-{attempts}");
            },
            onFailure: ex => observedFailures.Add(ex.Message),
            logger: NullLogger.Instance,
            cancellationToken: CancellationToken.None,
            delayAsync: (delay, _) =>
            {
                observedDelays.Add(delay);
                return Task.CompletedTask;
            });

        attempts.Should().Be(4);
        observedFailures.Should().BeEquivalentTo(["boom-1", "boom-2", "boom-3", "boom-4"]);
        observedDelays.Should().BeEquivalentTo(
            [TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5)],
            options => options.WithStrictOrdering());
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
    [DisplayName("Stage boundary check skips remaining stages when URI is marked failed")]
    public async Task Given_RegistryMarkedFailedDuringParsing_When_IndexItemAsync_Then_ItemIsNotCommitted()
    {
        var registry = new UriRegistry();
        var uri = CreateUri("file:///repo/boundary-failed.md");
        var committer = A.Fake<IIndexingCommitter>();

        var context = IndexingEngineTestFactory.Create(builder =>
        {
            builder.WithUriRegistry(registry);
            builder.WithCommitter(committer);
        });

        A.CallTo(() => context.Parser.ProcessItemAsync(A<IndexItem>._, A<CancellationToken>._))
            .ReturnsLazily(call =>
            {
                var item = call.GetArgument<IndexItem>(0);
                registry.SetFailed(item.Uri, "Cancelled by user");
                return Task.FromResult(PipelineResult.Success);
            });

        var item = IndexingTestItemFactory.CreateIndexItem(uri: uri.AbsoluteUri);
        await context.Engine.IndexItemAsync(item, CancellationToken.None);

        A.CallTo(() => committer.CommitAsync(A<IndexItem>._, A<CancellationToken>._)).MustNotHaveHappened();
        registry[uri].Status.Should().Be(UriStatus.Failed);
    }

    [Test]
    [DisplayName("Stage boundary check skips remaining stages when URI is marked skipped")]
    public async Task Given_RegistryMarkedSkippedDuringParsing_When_IndexItemAsync_Then_ItemIsNotCommitted()
    {
        var registry = new UriRegistry();
        var uri = CreateUri("file:///repo/boundary-skipped.md");
        var committer = A.Fake<IIndexingCommitter>();

        var context = IndexingEngineTestFactory.Create(builder =>
        {
            builder.WithUriRegistry(registry);
            builder.WithCommitter(committer);
        });

        A.CallTo(() => context.Parser.ProcessItemAsync(A<IndexItem>._, A<CancellationToken>._))
            .ReturnsLazily(call =>
            {
                var item = call.GetArgument<IndexItem>(0);
                registry.SetSkipped(item.Uri, "Skipped by user");
                return Task.FromResult(PipelineResult.Success);
            });

        var item = IndexingTestItemFactory.CreateIndexItem(uri: uri.AbsoluteUri);
        await context.Engine.IndexItemAsync(item, CancellationToken.None);

        A.CallTo(() => committer.CommitAsync(A<IndexItem>._, A<CancellationToken>._)).MustNotHaveHappened();
        registry[uri].Status.Should().Be(UriStatus.Skipped);
    }

    [Test]
    [DisplayName("Normal indexing still commits when URI remains indexing")]
    public async Task Given_RegistryStatusIndexing_When_IndexItemAsync_Then_ItemCommitsNormally()
    {
        var registry = new UriRegistry();
        var uri = CreateUri("file:///repo/boundary-normal.md");
        var committer = A.Fake<IIndexingCommitter>();
        A.CallTo(() => committer.CommitAsync(A<IndexItem>._, A<CancellationToken>._))
            .Returns(Task.FromResult(RepoQL.Indexing.Indexing.Commit.CommitOutcome.Committed));

        var context = IndexingEngineTestFactory.Create(builder =>
        {
            builder.WithUriRegistry(registry);
            builder.WithCommitter(committer);
        });

        var item = IndexingTestItemFactory.CreateIndexItem(uri: uri.AbsoluteUri);
        await context.Engine.IndexItemAsync(item, CancellationToken.None);

        A.CallTo(() => committer.CommitAsync(A<IndexItem>._, A<CancellationToken>._)).MustHaveHappenedOnceExactly();
        registry[uri].Status.Should().Be(UriStatus.Indexed);
    }

    [Test]
    [Timeout(15_000)]
    [DisplayName("Queues structure embedding asynchronously after commit when enabled")]
    public async Task Given_HotPathStructureEmbeddingEnabled_When_IndexItemAsync_Then_CommitDoesNotWaitForEagerEmbedding(CancellationToken token)
    {
        var committer = A.Fake<IIndexingCommitter>();
        var commitObserved = NewTaskCompletionSource<bool>();
        A.CallTo(() => committer.CommitAsync(A<IndexItem>._, A<CancellationToken>._))
            .Invokes(() => commitObserved.TrySetResult(true))
            .Returns(Task.FromResult(RepoQL.Indexing.Indexing.Commit.CommitOutcome.Committed));

        var embedEntered = NewTaskCompletionSource<bool>();
        var releaseEmbedding = NewTaskCompletionSource<bool>();
        var vectorCoordinator = A.Fake<IVectorIndexCoordinator>();
        A.CallTo(() => vectorCoordinator.GenerateStructureEmbeddingsAsync(A<IReadOnlyList<IndexItem>>._, A<CancellationToken>._))
            .ReturnsLazily(async _ =>
            {
                embedEntered.TrySetResult(true);
                await releaseEmbedding.Task.ConfigureAwait(false);
            });

        var embeddingProvider = new DeterministicEmbeddingProvider();
        var context = IndexingEngineTestFactory.Create(builder =>
        {
            builder.WithCommitter(committer);
            builder.WithVectorCoordinator(vectorCoordinator);
            builder.WithEmbeddingProvider(embeddingProvider);
            builder.WithEmbeddingMode(EmbeddingMode.StructureOnly);
        });

        var item = IndexingTestItemFactory.CreateIndexItem();
        await using var engine = context.Engine;

        var indexTask = engine.IndexItemAsync(item, token);

        await commitObserved.Task.WaitAsync(token);
        await indexTask.WaitAsync(token);
        await embedEntered.Task.WaitAsync(token);

        releaseEmbedding.TrySetResult(true);
    }

    [Test]
    [Timeout(15_000)]
    [DisplayName("Idle analysis waits for eager structure embedding completion")]
    public async Task Given_EagerStructureEmbeddingInFlight_When_IdleProcessingRuns_Then_AnalysisWaitsForBarrier(CancellationToken token)
    {
        var embedEntered = NewTaskCompletionSource<bool>();
        var releaseEmbedding = NewTaskCompletionSource<bool>();
        var analysisSignal = NewTaskCompletionSource<bool>();

        var vectorCoordinator = A.Fake<IVectorIndexCoordinator>();
        A.CallTo(() => vectorCoordinator.GenerateStructureEmbeddingsAsync(A<IReadOnlyList<IndexItem>>._, A<CancellationToken>._))
            .ReturnsLazily(async _ =>
            {
                embedEntered.TrySetResult(true);
                await releaseEmbedding.Task.ConfigureAwait(false);
            });
        A.CallTo(() => vectorCoordinator.ApplyAsync(A<IReadOnlyList<IndexItem>>._, A<CancellationToken>._))
            .Returns(Task.CompletedTask);
        A.CallTo(() => vectorCoordinator.RefreshVssIndexAsync(A<CancellationToken>._))
            .Returns(Task.CompletedTask);

        var context = IndexingEngineTestFactory.Create(builder =>
        {
            builder.WithOptions(new IndexingEngineOptions
            {
                IndexingQueueSize = 32,
                IndexingWorkers = 1,
                AnalysisQueueSize = 32,
                AnalysisWorkers = 1
            });
            builder.WithVectorCoordinator(vectorCoordinator);
            builder.WithEmbeddingProvider(new DeterministicEmbeddingProvider());
            builder.WithEmbeddingMode(EmbeddingMode.StructureOnly);
        });

        A.CallTo(() => context.MultiFileAnalyzer.ProcessItemAsync(A<IAnnotatedArtifact>._, A<CancellationToken>._))
            .ReturnsLazily(_ =>
            {
                analysisSignal.TrySetResult(true);
                return Task.FromResult(PipelineResult.Success);
            });
        A.CallTo(() => context.IndexRebuilder.ProcessItemAsync(A<IAnnotatedArtifact>._, A<CancellationToken>._))
            .Returns(Task.FromResult(PipelineResult.Success));

        await using var engine = context.Engine;
        await engine.EnqueueItemAsync(CreateRawArtifact("file:///repo/eager-barrier.md"), IndexItemOptions.Default, token);

        await embedEntered.Task.WaitAsync(token);
        await Task.Delay(100, token);
        analysisSignal.Task.IsCompleted.Should().BeFalse("idle processing should wait for eager structure embedding completion before analysis dispatch");

        releaseEmbedding.TrySetResult(true);
        await analysisSignal.Task.WaitAsync(token);
    }

    [Test]
    [Timeout(15_000)]
    [DisplayName("Idle safety net retries structure embedding after eager failure")]
    public async Task Given_EagerStructureEmbeddingFails_When_IdleProcessingRuns_Then_UnembeddedItemsAreRetried(CancellationToken token)
    {
        var registry = new UriRegistry();
        var retryCompleted = NewTaskCompletionSource<bool>();
        var analysisSignal = NewTaskCompletionSource<bool>();
        var structureAttempts = 0;
        var targetUri = CreateUri("file:///repo/eager-retry.md");

        var vectorCoordinator = A.Fake<IVectorIndexCoordinator>();
        A.CallTo(() => vectorCoordinator.GenerateStructureEmbeddingsAsync(A<IReadOnlyList<IndexItem>>._, A<CancellationToken>._))
            .ReturnsLazily(call =>
            {
                var items = call.GetArgument<IReadOnlyList<IndexItem>>(0);
                var attempt = Interlocked.Increment(ref structureAttempts);
                if (attempt == 1)
                {
                    throw new InvalidOperationException("Simulated eager structure embedding failure");
                }

                foreach (var embedded in items)
                {
                    registry.SetEmbedded(embedded.Uri, 1);
                }

                retryCompleted.TrySetResult(true);
                return Task.CompletedTask;
            });
        A.CallTo(() => vectorCoordinator.ApplyAsync(A<IReadOnlyList<IndexItem>>._, A<CancellationToken>._))
            .Returns(Task.CompletedTask);
        A.CallTo(() => vectorCoordinator.RefreshVssIndexAsync(A<CancellationToken>._))
            .Returns(Task.CompletedTask);

        var context = IndexingEngineTestFactory.Create(builder =>
        {
            builder.WithOptions(new IndexingEngineOptions
            {
                IndexingQueueSize = 32,
                IndexingWorkers = 1,
                AnalysisQueueSize = 32,
                AnalysisWorkers = 1
            });
            builder.WithVectorCoordinator(vectorCoordinator);
            builder.WithEmbeddingProvider(new DeterministicEmbeddingProvider());
            builder.WithEmbeddingMode(EmbeddingMode.StructureOnly);
            builder.WithUriRegistry(registry);
        });

        A.CallTo(() => context.MultiFileAnalyzer.ProcessItemAsync(A<IAnnotatedArtifact>._, A<CancellationToken>._))
            .ReturnsLazily(_ =>
            {
                analysisSignal.TrySetResult(true);
                return Task.FromResult(PipelineResult.Success);
            });
        A.CallTo(() => context.IndexRebuilder.ProcessItemAsync(A<IAnnotatedArtifact>._, A<CancellationToken>._))
            .Returns(Task.FromResult(PipelineResult.Success));

        await using var engine = context.Engine;
        await engine.EnqueueItemAsync(CreateRawArtifact(targetUri.AbsoluteUri), IndexItemOptions.Default, token);

        await retryCompleted.Task.WaitAsync(token);
        await analysisSignal.Task.WaitAsync(token);

        structureAttempts.Should().Be(2, "idle safety net should retry structure embedding once eager attempt fails");
        registry.TryGetValue(targetUri, out var entry).Should().BeTrue();
        entry.EmbeddingStatus.Should().Be(EmbeddingStatus.Embedded);
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
    [DisplayName("FM-005: Processes orphaned epoch when later epoch completes first")]
    public async Task Given_EpochNCompletesWhileEpochN1Processing_When_HotPathIdle_Then_BothEpochsProcessed(CancellationToken token)
    {
        // This test verifies the FM-005 fix for the race condition where epoch N completes
        // while epoch N+1 is still processing. Without the fix, epoch N's pending items
        // would never be processed because HotPathIdle wouldn't fire for it.
        //
        // Timeline being tested:
        // t0: Item A enqueued to epoch 0
        // t1: Begin epoch 1, Item B enqueued to epoch 1
        // t2: Item A completes (epoch 0 idle) but State != AllIdle (epoch 1 busy)
        //     → HotPathIdle SKIPPED for epoch 0 (the race condition)
        // t3: Item B completes, epoch 1 idle, State = AllIdle
        //     → HotPathIdle fires for epoch 1
        //     → FM-005 fix: ALL pending epochs are enqueued, not just epoch 1
        // t4: Both epoch 0 and 1 items processed

        // Gate A completes before B to create the race condition
        var gateA = NewTaskCompletionSource<bool>();
        var gateB = NewTaskCompletionSource<bool>();
        var itemAUri = "file:///repo/fm005/item-a.md";
        var itemBUri = "file:///repo/fm005/item-b.md";

        var context = IndexingEngineTestFactory.Create(builder =>
        {
            builder.WithOptions(new IndexingEngineOptions
            {
                IndexingQueueSize = 32,
                IndexingWorkers = 2, // Allow parallel processing
                AnalysisQueueSize = 32,
                AnalysisWorkers = 1
            });
        });

        // Configure parser to wait on item-specific gates
        A.CallTo(() => context.Classifier.ProcessItemAsync(A<IndexItem>._, A<CancellationToken>._))
            .Returns(Task.FromResult(PipelineResult.Success));
        A.CallTo(() => context.Parser.ProcessItemAsync(A<IndexItem>._, A<CancellationToken>._))
            .ReturnsLazily(async call =>
            {
                var item = call.GetArgument<IndexItem>(0)!;
                if (item.Uri.ToString().Contains("item-a"))
                    await gateA.Task.ConfigureAwait(false);
                else if (item.Uri.ToString().Contains("item-b"))
                    await gateB.Task.ConfigureAwait(false);
                return PipelineResult.Success;
            });
        A.CallTo(() => context.SingleFileAnalyzer.ProcessItemAsync(A<IndexItem>._, A<CancellationToken>._))
            .Returns(Task.FromResult(PipelineResult.Success));

        await using var engine = context.Engine;

        // Enqueue item A in epoch 0
        await engine.EnqueueItemAsync(CreateRawArtifact(itemAUri), IndexItemOptions.Default, token);

        // Wait for item A to be picked up by a worker
        await Task.Delay(50, token);

        // Begin new epoch and enqueue item B in epoch 1 BEFORE item A completes
        var epoch1 = engine.BeginNewEpoch();
        epoch1.Should().Be(1);
        await engine.EnqueueItemAsync(CreateRawArtifact(itemBUri), IndexItemOptions.Default, token);

        // Wait for item B to be picked up
        await Task.Delay(50, token);

        // Now release item A first - this creates the race condition
        // Epoch 0 becomes idle, but State != AllIdle because epoch 1 is still busy
        gateA.SetResult(true);

        // Give time for epoch 0 to complete and schedule its analysis
        await Task.Delay(100, token);

        // At this point, epoch 0's items are in _pendingStructureEmbeddings but HotPathIdle
        // hasn't fired because epoch 1 is still running

        // Now release item B - HotPathIdle should fire and process BOTH epochs
        gateB.SetResult(true);

        // Wait for everything to settle
        await engine.WaitForAsync(IndexingState.AllIdle, token);

        // FM-005 fix verification: GetPendingIdleProcessingCount should eventually reach 0.
        // Without the fix, epoch 0's items would remain orphaned permanently.
        using var settle = CancellationTokenSource.CreateLinkedTokenSource(token);
        settle.CancelAfter(TimeSpan.FromSeconds(5));
        while (engine.GetPendingIdleProcessingCount() != 0)
        {
            await Task.Delay(25, settle.Token);
        }

        engine.GetPendingIdleProcessingCount().Should().Be(0,
            "all epochs should be processed including orphaned epoch 0 (FM-005 fix)");
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
    [DisplayName("Idle processing requeues epoch when prune fails transiently")]
    public async Task Given_PruneFailsOnce_When_IdleProcessingRuns_Then_EpochIsRetriedAndAnalysisRuns(CancellationToken token)
    {
        var pruneAttempts = 0;
        var pruner = A.Fake<IArtifactPruner>();
        A.CallTo(() => pruner.PruneAsync(A<IReadOnlyCollection<IndexItem>>._, A<CancellationToken>._))
            .ReturnsLazily(_ =>
            {
                var attempt = Interlocked.Increment(ref pruneAttempts);
                if (attempt == 1)
                {
                    throw new InvalidOperationException("transient prune failure");
                }

                return Task.FromResult(PruningResult.None);
            });

        var analysisSignal = NewTaskCompletionSource<bool>();
        await using var engine = CreateEngineForAnalysisTests(
            parsingGate: null,
            multiFileSignal: analysisSignal,
            pruner: pruner,
            vectorCoordinator: NullVectorIndexCoordinator.Instance);

        await engine.EnqueueItemAsync(CreateRawArtifact("file:///repo/prune-retry.md"), IndexItemOptions.Default, token);

        await WaitForAnalysisToCompleteAsync(engine, analysisSignal, token);

        pruneAttempts.Should().BeGreaterThanOrEqualTo(2, "idle processing should retry the epoch after a transient prune failure");

        // Poll briefly — idle processing may still be draining under CI load
        for (var i = 0; i < 20 && engine.GetPendingIdleProcessingCount() > 0; i++)
            await Task.Delay(50, token);

        engine.GetPendingIdleProcessingCount().Should().Be(0, "failed epoch backlog should be replayed and drained");
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
        A.CallTo(() => vector.ApplyAsync(A<IReadOnlyList<IndexItem>>._, A<CancellationToken>._))
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
        A.CallTo(() => vector.ApplyAsync(A<IReadOnlyList<IndexItem>>._, A<CancellationToken>._))
            .ReturnsLazily(call =>
            {
                var items = call.GetArgument<IReadOnlyList<IndexItem>>(0)!;
                var epoch = items.Max(i => i.Epoch);
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

    private sealed class DeterministicEmbeddingProvider : IEmbeddingProvider
    {
        public string Model => "test-model";
        public int Dimension => 4;
        public bool Enabled => true;
        public int PassageCalls { get; private set; }

        public Task<float[]?> EmbedQueryAsync(string text, CancellationToken cancellationToken = default)
            => Task.FromResult<float[]?>(null);

        public Task<float[]?> EmbedPassageAsync(string text, CancellationToken cancellationToken = default)
        {
            PassageCalls++;
            return Task.FromResult<float[]?>(new[] { 0.1f, 0.2f, 0.3f, 0.4f });
        }

        public Task<float[]?[]> EmbedQueryBatchAsync(IReadOnlyList<string>? texts, CancellationToken cancellationToken = default)
            => Task.FromResult(Array.Empty<float[]?>());

        public Task<float[]?[]> EmbedPassageBatchAsync(IReadOnlyList<string>? texts, CancellationToken cancellationToken = default)
            => Task.FromResult(Array.Empty<float[]?>());

        public Task<float[]?[]> EmbedPassageBatchAsync(IReadOnlyList<string>? texts, BatchEmbeddingProgress progress, CancellationToken cancellationToken = default)
            => Task.FromResult(Array.Empty<float[]?>());
    }

    // === UriRegistry Integration Tests ===

    [Test]
    [Timeout(10_000)]
    [DisplayName("UriRegistry tracks file status through indexing lifecycle")]
    public async Task Given_UriRegistry_When_IndexingSucceeds_Then_RegistryShowsIndexedStatus(CancellationToken token)
    {
        // Arrange
        var registry = new UriRegistry();
        const string fileUriStr = "file:///repo/test-file.md";
        var fileUri = CreateUri(fileUriStr);

        var committer = A.Fake<IIndexingCommitter>();
        A.CallTo(() => committer.CommitAsync(A<IndexItem>._, A<CancellationToken>._))
            .Returns(Task.FromResult(RepoQL.Indexing.Indexing.Commit.CommitOutcome.Committed));

        var context = IndexingEngineTestFactory.Create(builder =>
        {
            builder.WithUriRegistry(registry);
            builder.WithCommitter(committer);
        });

        var item = IndexingTestItemFactory.CreateIndexItem(uri: fileUriStr);

        // Act
        await context.Engine.IndexItemAsync(item, token);

        // Assert - verify the registry was updated
        registry.Should().ContainKey(fileUri);
        registry[fileUri].Status.Should().Be(UriStatus.Indexed);
        registry[fileUri].IndexedAt.Should().NotBeNull();
    }

    [Test]
    [Timeout(10_000)]
    [DisplayName("UriRegistry tracks failed files with error message")]
    public async Task Given_UriRegistry_When_IndexingFails_Then_RegistryShowsFailedStatus(CancellationToken token)
    {
        // Arrange
        var registry = new UriRegistry();
        const string fileUriStr = "file:///repo/failing-file.md";
        var fileUri = CreateUri(fileUriStr);

        var classifier = A.Fake<ClassificationPipeline>();
        A.CallTo(() => classifier.ProcessItemAsync(A<IndexItem>._, A<CancellationToken>._))
            .Throws(new InvalidOperationException("Test classification failure"));

        var context = IndexingEngineTestFactory.Create(builder =>
        {
            builder.WithUriRegistry(registry);
            builder.WithClassifier(classifier);
        });

        var item = IndexingTestItemFactory.CreateIndexItem(uri: fileUriStr);

        // Act
        await context.Engine.IndexItemAsync(item, token);

        // Assert - verify the registry was updated with failure
        registry.Should().ContainKey(fileUri);
        registry[fileUri].Status.Should().Be(UriStatus.Failed);
        registry[fileUri].Error.Should().Contain("classification");
    }

    [Test]
    [Timeout(10_000)]
    [DisplayName("UriRegistry preserves stage failure detail when pipeline returns error")]
    public async Task Given_PipelineReturnsErrorWithFailureDetail_When_IndexingFails_Then_RegistryShowsSpecificMessage(CancellationToken token)
    {
        var registry = new UriRegistry();
        const string fileUriStr = "file:///repo/failing-parser.md";
        var fileUri = CreateUri(fileUriStr);

        var parser = new ParsingPipeline(
            [new ThrowingParser("front matter was malformed")],
            NullLogger<ParsingPipeline>.Instance);

        var context = IndexingEngineTestFactory.Create(builder =>
        {
            builder.WithUriRegistry(registry);
            builder.WithParser(parser);
        });

        var item = IndexingTestItemFactory.CreateIndexItem(uri: fileUriStr);

        await context.Engine.IndexItemAsync(item, token);

        registry.Should().ContainKey(fileUri);
        registry[fileUri].Status.Should().Be(UriStatus.Failed);
        registry[fileUri].Error.Should().Be("Parsing: front matter was malformed");
    }

    private sealed class ThrowingParser(string message) : IAsyncPipeline<IClassifiedArtifact, Records?>
    {
        public Task<(Records? Result, PipelineResult PipelineStatus)> ProcessAsync(
            IClassifiedArtifact item,
            CallNextPipeline<IClassifiedArtifact, Records?> next,
            CancellationToken cancellationToken)
        {
            throw new InvalidOperationException(message);
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

    // === Symbol Extraction with Spans Tests ===

    [Test]
    [DisplayName("ExtractSymbolsFromRecords returns empty dictionary when records is null")]
    public void ExtractSymbolsFromRecords_NullRecords_ReturnsEmptyDictionary()
    {
        var result = IndexingEngine.ExtractSymbolsFromRecords(null);

        result.Should().NotBeNull();
        result.Should().BeEmpty();
    }

    [Test]
    [DisplayName("ExtractSymbolsFromRecords extracts spans from nodes")]
    public void ExtractSymbolsFromRecords_NodesWithSpans_ExtractsLineRanges()
    {
        // Arrange
        var docNode = new Node { Kind = "document", Uri = CreateUri("file:///test.cs") };
        var classSpan = new ModelSpan { DocumentId = Guid.NewGuid(), StartLine = 5, EndLine = 20 };
        var methodSpan = new ModelSpan { DocumentId = Guid.NewGuid(), StartLine = 10, EndLine = 15 };

        var classNode = new Node
        {
            Kind = "class",
            Uri = CreateUri("file:///test.cs#symbol=MyClass"),
            SpanId = classSpan.Id
        };
        var methodNode = new Node
        {
            Kind = "method",
            Uri = CreateUri("file:///test.cs#symbol=MyClass.Method"),
            SpanId = methodSpan.Id
        };

        var records = new Records
        {
            Artifacts = [],
            Nodes = [docNode, classNode, methodNode],
            Spans = [classSpan, methodSpan],
            Edges = [],
            Annotations = [],
            AnnotationSources = []
        };

        // Act
        var result = IndexingEngine.ExtractSymbolsFromRecords(records);

        // Assert
        result.Should().HaveCount(2);
        result.Should().ContainKey(classNode.Uri!);
        result.Should().ContainKey(methodNode.Uri!);

        var classEntry = result[classNode.Uri!];
        classEntry.Kind.Should().Be("class");
        classEntry.StartLine.Should().Be(5);
        classEntry.EndLine.Should().Be(20);

        var methodEntry = result[methodNode.Uri!];
        methodEntry.Kind.Should().Be("method");
        methodEntry.StartLine.Should().Be(10);
        methodEntry.EndLine.Should().Be(15);
    }

    [Test]
    [DisplayName("ExtractSymbolsFromRecords handles nodes without spans")]
    public void ExtractSymbolsFromRecords_NodeWithoutSpan_ReturnsZeroSpan()
    {
        // Arrange
        var docNode = new Node { Kind = "document", Uri = CreateUri("file:///test.cs") };
        var classNode = new Node
        {
            Kind = "class",
            Uri = CreateUri("file:///test.cs#symbol=MyClass"),
            SpanId = null // No span
        };

        var records = new Records
        {
            Artifacts = [],
            Nodes = [docNode, classNode],
            Spans = [],
            Edges = [],
            Annotations = [],
            AnnotationSources = []
        };

        // Act
        var result = IndexingEngine.ExtractSymbolsFromRecords(records);

        // Assert
        result.Should().HaveCount(1);
        var entry = result[classNode.Uri!];
        entry.Kind.Should().Be("class");
        entry.StartLine.Should().Be(0);
        entry.EndLine.Should().Be(0);
    }

    [Test]
    [DisplayName("ExtractSymbolsFromRecords skips document nodes")]
    public void ExtractSymbolsFromRecords_DocumentNode_IsSkipped()
    {
        // Arrange
        var docNode = new Node { Kind = "document", Uri = CreateUri("file:///test.cs") };

        var records = new Records
        {
            Artifacts = [],
            Nodes = [docNode],
            Spans = [],
            Edges = [],
            Annotations = [],
            AnnotationSources = []
        };

        // Act
        var result = IndexingEngine.ExtractSymbolsFromRecords(records);

        // Assert
        result.Should().BeEmpty();
    }

    [Test]
    [DisplayName("ExtractSymbolsFromRecords handles spans with null line numbers")]
    public void ExtractSymbolsFromRecords_SpanWithNullLines_ReturnsZero()
    {
        // Arrange
        var span = new ModelSpan { DocumentId = Guid.NewGuid(), StartLine = null, EndLine = null };
        var node = new Node
        {
            Kind = "field",
            Uri = CreateUri("file:///test.cs#symbol=MyClass.Field"),
            SpanId = span.Id
        };

        var records = new Records
        {
            Artifacts = [],
            Nodes = [node],
            Spans = [span],
            Edges = [],
            Annotations = [],
            AnnotationSources = []
        };

        // Act
        var result = IndexingEngine.ExtractSymbolsFromRecords(records);

        // Assert
        var entry = result[node.Uri!];
        entry.StartLine.Should().Be(0);
        entry.EndLine.Should().Be(0);
    }

    // === Line Count Extraction Tests ===

    [Test]
    [DisplayName("ExtractLineCount returns 0 when records is null")]
    public void ExtractLineCount_NullRecords_ReturnsZero()
    {
        var result = IndexingEngine.ExtractLineCount(null);
        result.Should().Be(0);
    }

    [Test]
    [DisplayName("ExtractLineCount returns 0 when no artifacts")]
    public void ExtractLineCount_NoArtifacts_ReturnsZero()
    {
        var records = new Records
        {
            Artifacts = [],
            Nodes = [],
            Spans = [],
            Edges = [],
            Annotations = [],
            AnnotationSources = []
        };

        var result = IndexingEngine.ExtractLineCount(records);
        result.Should().Be(0);
    }

    [Test]
    [DisplayName("ExtractLineCount returns 0 when artifact has no text")]
    public void ExtractLineCount_NullText_ReturnsZero()
    {
        var records = new Records
        {
            Artifacts = [new RepoQL.Contracts.Models.Artifact { Digest = "abc", Text = null }],
            Nodes = [],
            Spans = [],
            Edges = [],
            Annotations = [],
            AnnotationSources = []
        };

        var result = IndexingEngine.ExtractLineCount(records);
        result.Should().Be(0);
    }

    [Test]
    [DisplayName("ExtractLineCount counts lines correctly for single line")]
    public void ExtractLineCount_SingleLine_ReturnsOne()
    {
        var records = new Records
        {
            Artifacts = [new RepoQL.Contracts.Models.Artifact { Digest = "abc", Text = "hello world" }],
            Nodes = [],
            Spans = [],
            Edges = [],
            Annotations = [],
            AnnotationSources = []
        };

        var result = IndexingEngine.ExtractLineCount(records);
        result.Should().Be(1);
    }

    [Test]
    [DisplayName("ExtractLineCount counts lines correctly for multiple lines")]
    public void ExtractLineCount_MultipleLines_ReturnsCorrectCount()
    {
        var records = new Records
        {
            Artifacts = [new RepoQL.Contracts.Models.Artifact { Digest = "abc", Text = "line1\nline2\nline3" }],
            Nodes = [],
            Spans = [],
            Edges = [],
            Annotations = [],
            AnnotationSources = []
        };

        var result = IndexingEngine.ExtractLineCount(records);
        result.Should().Be(3);
    }

    [Test]
    [DisplayName("ExtractLineCount handles trailing newline")]
    public void ExtractLineCount_TrailingNewline_CountsCorrectly()
    {
        var records = new Records
        {
            Artifacts = [new RepoQL.Contracts.Models.Artifact { Digest = "abc", Text = "line1\nline2\n" }],
            Nodes = [],
            Spans = [],
            Edges = [],
            Annotations = [],
            AnnotationSources = []
        };

        // Trailing newline means 3 lines (line1, line2, empty line after)
        var result = IndexingEngine.ExtractLineCount(records);
        result.Should().Be(3);
    }

    [Test]
    [DisplayName("Handles file deleted between discovery and indexing without logging an error")]
    public async Task Given_FileDeletedBeforeDigest_When_IndexItemAsync_Then_MarkedAsPrunedNotError()
    {
        // Arrange: create a RawArtifact whose stream throws DirectoryNotFoundException (simulating deleted file)
        var fileInfo = A.Fake<IFileInfo>();
        A.CallTo(() => fileInfo.Name).Returns("deleted.md");
        A.CallTo(() => fileInfo.Exists).Returns(true);
        A.CallTo(() => fileInfo.Length).Returns(100);
        A.CallTo(() => fileInfo.LastModified).Returns(DateTimeOffset.UtcNow);
        A.CallTo(() => fileInfo.IsDirectory).Returns(false);
        A.CallTo(() => fileInfo.PhysicalPath).Returns("/repo/research/deleted.md");
        A.CallTo(() => fileInfo.CreateReadStream())
            .Throws(new DirectoryNotFoundException("Could not find a part of the path '/repo/research/deleted.md'."));

        var fileSystem = A.Fake<IVirtualFileSystem>();
        var uri = CreateUri("file:///research/deleted.md");
        A.CallTo(() => fileSystem.GetUri(fileInfo)).Returns(uri);

        var rawArtifact = new RawArtifact(fileInfo, fileSystem);
        var item = new IndexItem(rawArtifact, IndexItemOptions.Default);

        var uriRegistry = new UriRegistry();
        var context = IndexingEngineTestFactory.Create(builder => builder.WithUriRegistry(uriRegistry));

        // Pipeline should NOT be invoked for a deleted file
        A.CallTo(() => context.Classifier.ProcessItemAsync(A<IndexItem>._, A<CancellationToken>._))
            .Throws(new InvalidOperationException("Classifier should not run for deleted files."));

        // Act
        await context.Engine.IndexItemAsync(item, CancellationToken.None);

        // Assert: file was removed from registry, not marked as failed
        uriRegistry.TryGetValue(uri, out _).Should().BeFalse("deleted file should be removed from registry, not marked as failed");
    }
}

