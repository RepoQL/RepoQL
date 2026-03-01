using AwesomeAssertions;
using FakeItEasy;
using RepoQL.Contracts;
using RepoQL.Indexing.Indexing;
using RepoQL.Indexing.Indexing.Pipelines;
using RepoQL.Indexing.Indexing.Pipelines.Classification;
using RepoQL.Indexing.Indexing.State;
using RepoQL.Testing.Indexing;

namespace RepoQL.Indexing.Tests.Indexing;

public sealed class IndexingEngineDiagnosticsProviderTests
{
    [Test]
    [Timeout(15_000)]
    [DisplayName("CreatedAt is set for classification and propagated to queued diagnostics EnqueuedAt")]
    public async Task Given_QueuedItem_When_DiagnosticsQueried_Then_EnqueuedAtMatchesCreatedAt(CancellationToken token)
    {
        var classifierEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseClassifier = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        DateTimeOffset observedCreatedAt = default;

        var classifier = A.Fake<ClassificationPipeline>();
        A.CallTo(() => classifier.ProcessItemAsync(A<IndexItem>._, A<CancellationToken>._))
            .ReturnsLazily(async call =>
            {
                var item = call.GetArgument<IndexItem>(0);
                observedCreatedAt = item.CreatedAt;
                classifierEntered.TrySetResult(true);
                await releaseClassifier.Task.ConfigureAwait(false);
                return PipelineResult.Success;
            });

        var context = IndexingEngineTestFactory.Create(builder => builder.WithClassifier(classifier));
        await using var engine = context.Engine;

        var indexItem = new IndexItem(
            IndexingTestItemFactory.CreateRawArtifact("file:///repo/created-at-propagation.cs"),
            IndexItemOptions.Default);

        indexItem.CreatedAt.Should().NotBe(default);

        var enqueued = await engine.EnqueueIndexItemAsync(indexItem, token);
        enqueued.Should().BeTrue();

        await classifierEntered.Task.WaitAsync(token);

        observedCreatedAt.Should().NotBe(default);
        observedCreatedAt.Should().Be(indexItem.CreatedAt);

        var diagnostics = new IndexingEngineDiagnosticsProvider(engine);
        var queuedItem = diagnostics.GetQueuedItems()
            .Single(x => string.Equals(x.Uri, indexItem.Uri.ToString(), StringComparison.Ordinal));

        queuedItem.EnqueuedAt.Should().Be(indexItem.CreatedAt);

        releaseClassifier.TrySetResult(true);
        await engine.WaitForAsync(IndexingState.AllIdle, token);
    }
}
