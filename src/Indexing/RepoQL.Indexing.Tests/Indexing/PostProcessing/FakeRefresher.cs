using RepoQL.Indexing.Indexing.PostProcessing;

namespace RepoQL.Indexing.Tests.Indexing.PostProcessing;

internal sealed class FakeRefresher : IEmbeddingRefreshRunner
{
    public int Invocations { get; private set; }
    public int TargetedInvocations { get; private set; }
    public IReadOnlyList<Guid> LastDocumentIds { get; private set; } = [];

    public Task<bool> RefreshAsync(CancellationToken cancellationToken)
    {
        Invocations++;
        LastDocumentIds = [];
        return Task.FromResult(true);
    }

    public Task<bool> RefreshAsync(IReadOnlyList<Guid> documentIds, CancellationToken cancellationToken)
    {
        TargetedInvocations++;
        LastDocumentIds = [.. documentIds];
        return Task.FromResult(true);
    }
}
