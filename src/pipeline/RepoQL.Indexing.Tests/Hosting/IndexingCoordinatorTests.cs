using AwesomeAssertions;
using FakeItEasy;
using Microsoft.Extensions.Logging.Abstractions;
using RepoQL.Contracts;
using RepoQL.Data.DuckDB;
using RepoQL.FileSystem.Abstractions;
using RepoQL.FileSystem.InMemory;
using RepoQL.Indexing.FileSystems;
using RepoQL.Indexing.Hosting;
using RepoQL.Testing.Indexing;

namespace RepoQL.Indexing.Tests.Hosting;

public sealed class IndexingCoordinatorTests
{
    [Test]
    [DisplayName("WaitForPipelineAsync throws when stage queue stays non-empty with idle workers")]
    public async Task Given_IdleWorkersAndStuckDiscoveryDepth_When_WaitingForPipeline_Then_ThrowsTimeout()
    {
        var fileSystem = new MemoryFileSystem("primary");
        var composite = new CompositeFileSystem(CompositeFileSystemMount.CreatePrimary(fileSystem));
        using var dataStore = new DuckDbDataStore(path: ":memory:");
        var filter = A.Fake<IUriFilter>();
        A.CallTo(() => filter.IncludeFile(A<RepoUri>._)).Returns(true);

        var context = IndexingEngineTestFactory.Create(builder => builder.WithFilter(filter));
        var coordinator = new IndexingCoordinator(
            composite,
            context.Engine,
            dataStore,
            NullLogger<IndexingCoordinator>.Instance,
            mountManager: null,
            gitIndexer: null,
            operationManager: null,
            uriRegistry: null,
            repoConfig: null,
            maxQueueDrainWait: TimeSpan.FromMilliseconds(10));

        coordinator.SetActiveMountIndexingForTests(1);

        Func<Task> act = () => coordinator.WaitForPipelineAsync(
            [CoordinatorPipelineStage.Discovery],
            waitAll: true,
            CancellationToken.None);

        await act.Should().ThrowAsync<TimeoutException>();

        await context.Engine.DisposeAsync();
    }

    [Test]
    [DisplayName("WaitForPipelineAsync waitAny cancels the unfinished stage waits")]
    public async Task Given_ParsingWouldTimeout_When_WaitingForAnyStage_Then_LosingWaitIsCancelledAndObserved()
    {
        var fileSystem = new MemoryFileSystem("primary");
        var composite = new CompositeFileSystem(CompositeFileSystemMount.CreatePrimary(fileSystem));
        using var dataStore = new DuckDbDataStore(path: ":memory:");
        var filter = A.Fake<IUriFilter>();
        A.CallTo(() => filter.IncludeFile(A<RepoUri>._)).Returns(true);

        var context = IndexingEngineTestFactory.Create(builder => builder.WithFilter(filter));
        var parsingCancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new IndexingCoordinator(
            composite,
            context.Engine,
            dataStore,
            NullLogger<IndexingCoordinator>.Instance,
            mountManager: null,
            gitIndexer: null,
            operationManager: null,
            uriRegistry: null,
            repoConfig: null,
            maxQueueDrainWait: TimeSpan.FromMilliseconds(25),
            stageWaitOverride: (stage, cancellationToken) => stage switch
            {
                CoordinatorPipelineStage.Discovery => Task.CompletedTask,
                CoordinatorPipelineStage.Parsing => WaitUntilCancelledAsync(cancellationToken, parsingCancelled),
                _ => Task.CompletedTask
            });

        try
        {
            var waitTask = coordinator.WaitForPipelineAsync(
                [CoordinatorPipelineStage.Discovery, CoordinatorPipelineStage.Parsing],
                waitAll: false,
                CancellationToken.None);

            var completed = await Task.WhenAny(waitTask, Task.Delay(TimeSpan.FromSeconds(2)));
            completed.Should().BeSameAs(waitTask);
            await waitTask;

            var cancelled = await Task.WhenAny(parsingCancelled.Task, Task.Delay(TimeSpan.FromSeconds(2)));
            cancelled.Should().BeSameAs(parsingCancelled.Task);
            parsingCancelled.Task.Result.Should().BeTrue();
        }
        finally
        {
            await context.Engine.DisposeAsync();
        }
    }

    private static async Task WaitUntilCancelledAsync(
        CancellationToken cancellationToken,
        TaskCompletionSource<bool> cancelled)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            cancelled.TrySetResult(true);
            return;
        }

        cancelled.TrySetResult(false);
    }
}
