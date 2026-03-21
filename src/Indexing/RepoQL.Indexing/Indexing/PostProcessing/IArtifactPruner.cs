using RepoQL.Contracts;
namespace RepoQL.Indexing.Indexing.PostProcessing;

public readonly record struct PruningResult(IReadOnlyList<RepoUri> DeletedArtifacts)
{
    public static readonly PruningResult None = new(Array.Empty<RepoUri>());
}

public interface IArtifactPruner
{
    Task<PruningResult> PruneAsync(IReadOnlyCollection<RepoUri> observedUris, CancellationToken cancellationToken);
}

public sealed class NullArtifactPruner : IArtifactPruner
{
    public static IArtifactPruner Instance { get; } = new NullArtifactPruner();

    private NullArtifactPruner()
    {
    }

    public Task<PruningResult> PruneAsync(IReadOnlyCollection<RepoUri> observedUris, CancellationToken cancellationToken)
    {
        return Task.FromResult(PruningResult.None);
    }
}
