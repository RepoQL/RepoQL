using RepoQL.Indexing.Indexing.PostProcessing;

namespace RepoQL.Indexing.Tests.Indexing.PostProcessing;

internal sealed class FakeRefresher : IVectorIndexRefresher
{
    public int Invocations { get; private set; }

    public Task RefreshAsync(CancellationToken cancellationToken)
    {
        Invocations++;
        return Task.CompletedTask;
    }
}