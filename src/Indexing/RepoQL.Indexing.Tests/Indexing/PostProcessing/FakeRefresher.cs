using RepoQL.Indexing.Indexing.PostProcessing;

namespace RepoQL.Indexing.Tests.Indexing.PostProcessing;

internal sealed class FakeRefresher : IVectorIndexRefresher
{
    public int Invocations { get; private set; }
    public int TargetedInvocations { get; private set; }
    public IReadOnlyList<Guid> LastDocumentIds { get; private set; } = [];

    public Task RefreshAsync(CancellationToken cancellationToken)
    {
        Invocations++;
        LastDocumentIds = [];
        return Task.CompletedTask;
    }

    public Task RefreshAsync(IReadOnlyList<Guid> documentIds, CancellationToken cancellationToken)
    {
        TargetedInvocations++;
        LastDocumentIds = [.. documentIds];
        return Task.CompletedTask;
    }
}
