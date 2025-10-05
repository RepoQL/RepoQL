namespace RepoQL.Contracts;

public interface IAnalysisWorkspace
{
    Task<DocumentModel?> LoadAsync(RepoUri uri, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<EmbeddedFragment>> DiscoverEmbedsAsync(DocumentModel document, CancellationToken cancellationToken = default);
}
