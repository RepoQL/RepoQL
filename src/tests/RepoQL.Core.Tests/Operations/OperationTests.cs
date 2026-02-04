using AwesomeAssertions;
using FakeItEasy;
using RepoQL.Contracts;
using RepoQL.Core.Operations;

namespace RepoQL.Core.Tests.Operations;

internal sealed class OperationTests
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    [Test]
    public async Task Operation_Lifecycle_CompletesSuccessfully()
    {
        var registry = new UriRegistry();
        var uri = RepoUri.Parse("file:///src/App.cs");
        registry.TryRegisterDiscovered(uri);
        var manager = new OperationManager(registry);

        var operation = manager.CreateOperation("index: lifecycle", new[] { uri });

        registry.SetIndexed(uri, new Dictionary<RepoUri, string>().AsReadOnly());
        registry.SetEmbedded(uri, 1);

        var progress = await AwaitCompletionAsync(operation, DefaultTimeout);

        operation.State.Should().Be(OperationState.Completed);
        progress.TotalFiles.Should().Be(1);
        progress.IndexedCount.Should().Be(1);
        progress.EmbeddedCount.Should().Be(1);
        progress.FailedCount.Should().Be(0);

        operation.Log.Select(entry => entry.Type).Should().ContainInOrder(
            OperationEntry.TypeCreated,
            OperationEntry.TypeFileIndexed,
            OperationEntry.TypeFileEmbedded,
            OperationEntry.TypeCompleted);
    }

    [Test]
    public async Task Operation_IndexingFailure_CompletesWithFailures()
    {
        var registry = new UriRegistry();
        var uri = RepoUri.Parse("file:///src/App.cs");
        registry.SetFailed(uri, "Parse error");
        var manager = new OperationManager(registry);

        var operation = manager.CreateOperation("index: failure", new[] { uri });

        var progress = await AwaitCompletionAsync(operation, DefaultTimeout);

        operation.State.Should().Be(OperationState.CompletedWithFailures);
        progress.FailedCount.Should().Be(1);
        operation.Log.Should().Contain(entry => entry.Type == OperationEntry.TypeFileFailed);
    }

    [Test]
    public async Task Operation_EmbeddingFailure_CompletesWithFailures()
    {
        var registry = new UriRegistry();
        var uri = RepoUri.Parse("file:///src/App.cs");
        registry.TryRegisterDiscovered(uri);
        var manager = new OperationManager(registry);

        var operation = manager.CreateOperation("embedding: failure", new[] { uri });

        registry.SetIndexed(uri, new Dictionary<RepoUri, string>().AsReadOnly());
        registry.SetEmbeddingFailed(uri, "Embedding error");

        var progress = await AwaitCompletionAsync(operation, DefaultTimeout);

        operation.State.Should().Be(OperationState.CompletedWithFailures);
        progress.FailedCount.Should().Be(1);
        operation.Log.Should().Contain(entry => entry.Type == OperationEntry.TypeEmbeddingFailed);
    }

    [Test]
    public async Task Operation_Cancelled_StopsAndCancelsCompletion()
    {
        var registry = new UriRegistry();
        var uri = RepoUri.Parse("file:///src/App.cs");
        registry.TryRegisterDiscovered(uri);
        var manager = new OperationManager(registry);

        var operation = manager.CreateOperation("index: cancel", new[] { uri });

        operation.Cancel();

        operation.State.Should().Be(OperationState.Cancelled);
        operation.Log.Should().Contain(entry => entry.Type == OperationEntry.TypeCancelled);

        var act = async () => await operation.Completion;
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Test]
    public async Task Operation_EmptyScope_CompletesImmediately()
    {
        var registry = new UriRegistry();
        var manager = new OperationManager(registry);

        var operation = manager.CreateOperation("index: empty", Array.Empty<RepoUri>());

        var progress = await operation.Completion;

        operation.State.Should().Be(OperationState.Completed);
        progress.Should().Be(OperationProgress.Empty);
        operation.Log.Select(entry => entry.Type).Should().ContainInOrder(
            OperationEntry.TypeCreated,
            OperationEntry.TypeCompleted);
    }

    [Test]
    public async Task Operation_ProgressCallback_Fires()
    {
        var registry = new UriRegistry();
        var uri = RepoUri.Parse("file:///src/App.cs");
        registry.TryRegisterDiscovered(uri);
        var progress = A.Fake<IProgress<OperationProgress>>();
        var manager = new OperationManager(registry);

        var operation = manager.CreateOperation("index: progress", new[] { uri }, progress);

        registry.SetIndexed(uri, new Dictionary<RepoUri, string>().AsReadOnly());
        registry.SetEmbedded(uri, 1);

        await AwaitCompletionAsync(operation, DefaultTimeout);

        A.CallTo(() => progress.Report(A<OperationProgress>._)).MustHaveHappened();
    }

    [Test]
    public async Task Operation_ProgressCallback_Throws_DoesNotFail()
    {
        var registry = new UriRegistry();
        var uri = RepoUri.Parse("file:///src/App.cs");
        registry.TryRegisterDiscovered(uri);
        var progress = A.Fake<IProgress<OperationProgress>>();
        A.CallTo(() => progress.Report(A<OperationProgress>._)).Throws(new InvalidOperationException("boom"));
        var manager = new OperationManager(registry);

        var operation = manager.CreateOperation("index: progress throws", new[] { uri }, progress);

        registry.SetIndexed(uri, new Dictionary<RepoUri, string>().AsReadOnly());
        registry.SetEmbedded(uri, 1);

        await AwaitCompletionAsync(operation, DefaultTimeout);

        operation.State.Should().Be(OperationState.Completed);
    }

    [Test]
    public async Task Operation_Deduplicates_Uris()
    {
        var registry = new UriRegistry();
        var uri = RepoUri.Parse("file:///src/App.cs");
        registry.TryRegisterDiscovered(uri);
        var manager = new OperationManager(registry);

        var operation = manager.CreateOperation("index: dedupe", new[] { uri, RepoUri.Parse("file:///src/App.cs") });

        registry.SetIndexed(uri, new Dictionary<RepoUri, string>().AsReadOnly());
        registry.SetEmbedded(uri, 1);

        var progress = await AwaitCompletionAsync(operation, DefaultTimeout);

        progress.TotalFiles.Should().Be(1);
        operation.Log.Count(entry => entry.Type == OperationEntry.TypeFileIndexed).Should().Be(1);
    }

    [Test]
    public async Task Operation_MissingUri_LogsFailure()
    {
        var registry = new UriRegistry();
        var manager = new OperationManager(registry);
        var missingUri = RepoUri.Parse("file:///src/Missing.cs");

        var operation = manager.CreateOperation("index: missing", new[] { missingUri });

        var progress = await AwaitCompletionAsync(operation, DefaultTimeout);

        operation.State.Should().Be(OperationState.CompletedWithFailures);
        progress.FailedCount.Should().Be(1);
        operation.Log.Should().Contain(entry =>
            entry.Type == OperationEntry.TypeFileFailed &&
            entry.Message == "URI not found in registry" &&
            entry.Uri == missingUri);
    }

    [Test]
    public async Task Operation_NotApplicable_LogsFileReady()
    {
        var registry = new UriRegistry();
        var uri = RepoUri.Parse("file:///src/binary.png");
        registry.TryRegisterDiscovered(uri);
        var manager = new OperationManager(registry);

        var operation = manager.CreateOperation("index: not applicable", new[] { uri });

        registry.SetIndexed(uri, new Dictionary<RepoUri, string>().AsReadOnly());
        registry.SetEmbeddingNotApplicable(uri);

        var progress = await AwaitCompletionAsync(operation, DefaultTimeout);

        operation.State.Should().Be(OperationState.Completed);
        progress.EmbeddedCount.Should().Be(1); // NotApplicable counts as embedded
        progress.FailedCount.Should().Be(0);
        operation.Log.Should().Contain(entry => entry.Type == OperationEntry.TypeFileReady);
    }

    [Test]
    public async Task Operation_MixedOutcomes_TracksAllCorrectly()
    {
        var registry = new UriRegistry();
        var successUri = RepoUri.Parse("file:///src/success.cs");
        var indexFailUri = RepoUri.Parse("file:///src/index-fail.cs");
        var embedFailUri = RepoUri.Parse("file:///src/embed-fail.cs");
        var notApplicableUri = RepoUri.Parse("file:///src/binary.png");

        registry.TryRegisterDiscovered(successUri);
        registry.TryRegisterDiscovered(embedFailUri);
        registry.TryRegisterDiscovered(notApplicableUri);
        registry.SetFailed(indexFailUri, "Parse error");

        var manager = new OperationManager(registry);
        var operation = manager.CreateOperation("index: mixed", new[] { successUri, indexFailUri, embedFailUri, notApplicableUri });

        // Success path
        registry.SetIndexed(successUri, new Dictionary<RepoUri, string>().AsReadOnly());
        registry.SetEmbedded(successUri, 1);

        // Embedding failure path
        registry.SetIndexed(embedFailUri, new Dictionary<RepoUri, string>().AsReadOnly());
        registry.SetEmbeddingFailed(embedFailUri, "Embedding error");

        // Not applicable path
        registry.SetIndexed(notApplicableUri, new Dictionary<RepoUri, string>().AsReadOnly());
        registry.SetEmbeddingNotApplicable(notApplicableUri);

        var progress = await AwaitCompletionAsync(operation, DefaultTimeout);

        operation.State.Should().Be(OperationState.CompletedWithFailures);
        progress.TotalFiles.Should().Be(4);
        progress.IndexedCount.Should().Be(3); // success, embedFail, notApplicable
        progress.EmbeddedCount.Should().Be(2); // success + notApplicable
        progress.FailedCount.Should().Be(2); // indexFail + embedFail
    }

    [Test]
    public async Task Operation_Cancel_NoOpWhenAlreadyCompleted()
    {
        var registry = new UriRegistry();
        var uri = RepoUri.Parse("file:///src/App.cs");
        registry.TryRegisterDiscovered(uri);
        var manager = new OperationManager(registry);

        var operation = manager.CreateOperation("index: cancel after complete", new[] { uri });

        registry.SetIndexed(uri, new Dictionary<RepoUri, string>().AsReadOnly());
        registry.SetEmbedded(uri, 1);

        await AwaitCompletionAsync(operation, DefaultTimeout);

        operation.Cancel(); // Should be no-op

        operation.State.Should().Be(OperationState.Completed);
        operation.Log.Should().NotContain(entry => entry.Type == OperationEntry.TypeCancelled);
    }

    [Test]
    public async Task Operation_CompletedAt_SetOnCompletion()
    {
        var registry = new UriRegistry();
        var uri = RepoUri.Parse("file:///src/App.cs");
        registry.TryRegisterDiscovered(uri);
        var manager = new OperationManager(registry);

        var operation = manager.CreateOperation("index: timestamp", new[] { uri });
        operation.CompletedAt.Should().BeNull();

        registry.SetIndexed(uri, new Dictionary<RepoUri, string>().AsReadOnly());
        registry.SetEmbedded(uri, 1);

        await AwaitCompletionAsync(operation, DefaultTimeout);

        operation.CompletedAt.Should().NotBeNull();
        operation.CompletedAt!.Value.Should().BeAfter(operation.CreatedAt);
    }

    [Test]
    public void OperationManager_GetOperation_ReturnsCorrectOperation()
    {
        var registry = new UriRegistry();
        var manager = new OperationManager(registry);

        var operation = manager.CreateOperation("test", Array.Empty<RepoUri>());

        manager.GetOperation(operation.Id).Should().Be(operation);
    }

    [Test]
    public void OperationManager_GetOperation_ReturnsNullForInvalidId()
    {
        var registry = new UriRegistry();
        var manager = new OperationManager(registry);

        manager.GetOperation("nonexistent").Should().BeNull();
        manager.GetOperation("").Should().BeNull();
        manager.GetOperation(null!).Should().BeNull();
    }

    [Test]
    public void OperationManager_Operations_ReturnsAllOperations()
    {
        var registry = new UriRegistry();
        var manager = new OperationManager(registry);

        var op1 = manager.CreateOperation("test1", Array.Empty<RepoUri>());
        var op2 = manager.CreateOperation("test2", Array.Empty<RepoUri>());

        manager.Operations.Should().Contain(op1);
        manager.Operations.Should().Contain(op2);
        manager.Operations.Should().HaveCount(2);
    }

    [Test]
    public async Task OperationManager_ActiveOperations_FiltersCompleted()
    {
        var registry = new UriRegistry();
        var uri = RepoUri.Parse("file:///src/App.cs");
        registry.TryRegisterDiscovered(uri);
        var manager = new OperationManager(registry);

        var completedOp = manager.CreateOperation("completed", Array.Empty<RepoUri>());
        var runningOp = manager.CreateOperation("running", new[] { uri });

        await completedOp.Completion; // Empty scope completes immediately

        manager.ActiveOperations.Should().Contain(runningOp);
        manager.ActiveOperations.Should().NotContain(completedOp);
    }

    private static async Task<OperationProgress> AwaitCompletionAsync(IOperation operation, TimeSpan timeout)
    {
        var completed = await Task.WhenAny(operation.Completion, Task.Delay(timeout));
        if (completed != operation.Completion)
            throw new TimeoutException("Operation did not complete within the timeout.");

        return await operation.Completion;
    }
}
