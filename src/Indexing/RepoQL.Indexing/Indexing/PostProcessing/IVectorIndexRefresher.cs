namespace RepoQL.Indexing.Indexing.PostProcessing;

public interface IVectorIndexRefresher
{
    Task RefreshAsync(CancellationToken cancellationToken);
}
