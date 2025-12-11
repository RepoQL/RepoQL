using System.Collections.Generic;
using System.Linq;
using FakeItEasy;
using RepoQL.Contracts;
using RepoQL.Contracts.Data;
using RepoQL.Indexing.Indexing.Pipelines;
using RepoQL.Indexing.Indexing.PostProcessing;
using RepoQL.Indexing.Indexing.State;
using RepoQL.Testing.Indexing;

namespace RepoQL.Testing;

public enum InvocationExpectation
{
    None,
    Once,
    AtLeastOnce
}

public readonly record struct PipelineInvocationPlan(
    InvocationExpectation? Classifier = InvocationExpectation.Once,
    InvocationExpectation? Parser = InvocationExpectation.Once,
    InvocationExpectation? SingleFileAnalyzer = InvocationExpectation.Once,
    InvocationExpectation? MultiFileAnalyzer = null,
    InvocationExpectation? IndexRebuilder = null,
    InvocationExpectation? Committer = null)
{
    public static PipelineInvocationPlan Success => new();
    public static PipelineInvocationPlan HotPathSuccess => new(Committer: InvocationExpectation.Once);
    public static PipelineInvocationPlan ShortCircuitAfterClassifier =>
        new(Parser: InvocationExpectation.None, SingleFileAnalyzer: InvocationExpectation.None, Committer: InvocationExpectation.None);
    public static PipelineInvocationPlan ShortCircuitAfterParser =>
        Success with { SingleFileAnalyzer = InvocationExpectation.None, Committer = InvocationExpectation.None };
}

public readonly record struct CatalogInvocationPlan(
    InvocationExpectation? EnsureInitialized = InvocationExpectation.Once,
    InvocationExpectation? Evaluate = InvocationExpectation.Once,
    InvocationExpectation? BeginProcessing = InvocationExpectation.Once,
    InvocationExpectation? CompleteProcessing = InvocationExpectation.Once)
{
    public static CatalogInvocationPlan Reindex => new();
    public static CatalogInvocationPlan SkipProcessing =>
        new(BeginProcessing: InvocationExpectation.None, CompleteProcessing: InvocationExpectation.None);
}

public static class IndexingAssertionExtensions
{
    public static void ShouldMatchPipeline(this IndexingEngineTestContext context, IndexItem item, PipelineInvocationPlan plan)
    {
        Verify(
            plan.Classifier,
            () => A.CallTo(() => context.Classifier.ProcessItemAsync(item, A<CancellationToken>._)).MustNotHaveHappened(),
            () => A.CallTo(() => context.Classifier.ProcessItemAsync(item, A<CancellationToken>._)).MustHaveHappenedOnceExactly(),
            () => A.CallTo(() => context.Classifier.ProcessItemAsync(item, A<CancellationToken>._)).MustHaveHappened());
        Verify(
            plan.Parser,
            () => A.CallTo(() => context.Parser.ProcessItemAsync(item, A<CancellationToken>._)).MustNotHaveHappened(),
            () => A.CallTo(() => context.Parser.ProcessItemAsync(item, A<CancellationToken>._)).MustHaveHappenedOnceExactly(),
            () => A.CallTo(() => context.Parser.ProcessItemAsync(item, A<CancellationToken>._)).MustHaveHappened());
        Verify(
            plan.SingleFileAnalyzer,
            () => A.CallTo(() => context.SingleFileAnalyzer.ProcessItemAsync(item, A<CancellationToken>._)).MustNotHaveHappened(),
            () => A.CallTo(() => context.SingleFileAnalyzer.ProcessItemAsync(item, A<CancellationToken>._)).MustHaveHappenedOnceExactly(),
            () => A.CallTo(() => context.SingleFileAnalyzer.ProcessItemAsync(item, A<CancellationToken>._)).MustHaveHappened());
        Verify(
            plan.MultiFileAnalyzer,
            () => A.CallTo(() => context.MultiFileAnalyzer.ProcessItemAsync(item, A<CancellationToken>._)).MustNotHaveHappened(),
            () => A.CallTo(() => context.MultiFileAnalyzer.ProcessItemAsync(item, A<CancellationToken>._)).MustHaveHappenedOnceExactly(),
            () => A.CallTo(() => context.MultiFileAnalyzer.ProcessItemAsync(item, A<CancellationToken>._)).MustHaveHappened());
        Verify(
            plan.IndexRebuilder,
            () => A.CallTo(() => context.IndexRebuilder.ProcessItemAsync(item, A<CancellationToken>._)).MustNotHaveHappened(),
            () => A.CallTo(() => context.IndexRebuilder.ProcessItemAsync(item, A<CancellationToken>._)).MustHaveHappenedOnceExactly(),
            () => A.CallTo(() => context.IndexRebuilder.ProcessItemAsync(item, A<CancellationToken>._)).MustHaveHappened());
        Verify(
            plan.Committer,
            () => A.CallTo(() => context.Committer.CommitAsync(item, A<CancellationToken>._)).MustNotHaveHappened(),
            () => A.CallTo(() => context.Committer.CommitAsync(item, A<CancellationToken>._)).MustHaveHappenedOnceExactly(),
            () => A.CallTo(() => context.Committer.CommitAsync(item, A<CancellationToken>._)).MustHaveHappened());
    }

    public static void ShouldMatch(this IDocumentCatalog catalog, RepoUri uri, CatalogInvocationPlan plan)
    {
        Verify(
            plan.EnsureInitialized,
            () => A.CallTo(() => catalog.EnsureInitializedAsync(A<CancellationToken>._)).MustNotHaveHappened(),
            () => A.CallTo(() => catalog.EnsureInitializedAsync(A<CancellationToken>._)).MustHaveHappenedOnceExactly(),
            () => A.CallTo(() => catalog.EnsureInitializedAsync(A<CancellationToken>._)).MustHaveHappened());
        Verify(
            plan.Evaluate,
            () => A.CallTo(() => catalog.Evaluate(uri, A<string>._)).MustNotHaveHappened(),
            () => A.CallTo(() => catalog.Evaluate(uri, A<string>._)).MustHaveHappenedOnceExactly(),
            () => A.CallTo(() => catalog.Evaluate(uri, A<string>._)).MustHaveHappened());
        Verify(
            plan.BeginProcessing,
            () => A.CallTo(() => catalog.BeginProcessing(uri, A<string>._)).MustNotHaveHappened(),
            () => A.CallTo(() => catalog.BeginProcessing(uri, A<string>._)).MustHaveHappenedOnceExactly(),
            () => A.CallTo(() => catalog.BeginProcessing(uri, A<string>._)).MustHaveHappened());
        Verify(
            plan.CompleteProcessing,
            () => A.CallTo(() => catalog.CompleteProcessing(uri)).MustNotHaveHappened(),
            () => A.CallTo(() => catalog.CompleteProcessing(uri)).MustHaveHappenedOnceExactly(),
            () => A.CallTo(() => catalog.CompleteProcessing(uri)).MustHaveHappened());
    }

    public static void ShouldHaveDeletedDocuments(this IRepoDatabase db, params RepoUri[] uris)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (uris is null || uris.Length == 0)
            return;

        foreach (var uri in uris)
        {
            A.CallTo(() => db.DeleteArtifact(uri))
                .MustHaveHappenedOnceExactly();
        }
    }

    public static void ShouldHaveAppliedVectorDeletes(this IVectorIndexCoordinator coordinator, IReadOnlyList<RepoUri> uris)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(uris);

        if (uris.Count == 0)
            return;

        A.CallTo(() => coordinator.ApplyDeletesAsync(
                A<IReadOnlyList<RepoUri>>.That.Matches(actual => actual.SequenceEqual(uris)),
                A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    public static void ShouldHaveAppliedVectors(this IVectorIndexCoordinator coordinator, InvocationExpectation expectation)
    {
        ArgumentNullException.ThrowIfNull(coordinator);

        Verify(
            expectation,
            () => A.CallTo(() => coordinator.ApplyAsync(A<IndexItem>._, A<CancellationToken>._)).MustNotHaveHappened(),
            () => A.CallTo(() => coordinator.ApplyAsync(A<IndexItem>._, A<CancellationToken>._)).MustHaveHappenedOnceExactly(),
            () => A.CallTo(() => coordinator.ApplyAsync(A<IndexItem>._, A<CancellationToken>._)).MustHaveHappened());
    }

    private static void Verify(
        InvocationExpectation? expectation,
        Action none,
        Action once,
        Action atLeast)
    {
        if (expectation is null)
        {
            return;
        }

        switch (expectation)
        {
            case InvocationExpectation.None:
                none();
                break;
            case InvocationExpectation.Once:
                once();
                break;
            case InvocationExpectation.AtLeastOnce:
                atLeast();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(expectation), expectation, "Unknown invocation expectation");
        }
    }
}
