namespace RepoQL.Indexing.Indexing.PostProcessing;

public interface IVectorIndexRefresher
{
    Task<bool> RefreshAsync(CancellationToken cancellationToken);
    Task<bool> RefreshAsync(IReadOnlyList<Guid> documentIds, CancellationToken cancellationToken);
}
