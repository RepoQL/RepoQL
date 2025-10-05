using System.Runtime.CompilerServices;

namespace RepoQL.Contracts;

public interface IFormatLoader
{
    Task<bool> CanLoadAsync(DiscoveredArtifact artifact, CancellationToken cancellationToken = default);

    Task<DocumentModel> LoadAsync(DiscoveredArtifact artifact, CancellationToken cancellationToken = default);

    async IAsyncEnumerable<EmbeddedFragment> DiscoverEmbedsAsync(DocumentModel document, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        yield break;
    }
}
