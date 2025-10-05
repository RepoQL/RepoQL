using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using RepoQL.Contracts;

namespace RepoQL.Core;

public interface IRepositoryIndexer : IHostedService, IObservable<IndexerEvent>, IAsyncDisposable
{
    Task QueueForIndexingAsync(IFileInfo[] files);
    Task QueueForIndexingAsync(RepoUri[] uris);
    Task WaitForWriterIdle(CancellationToken cancellationToken = default);
    Task WaitForIdle(CancellationToken cancellationToken = default);

    record ItemDiscoveredEvent(IFileInfo FileInfo, RepoUri CurrentUri) : IndexerEvent(FileInfo, CurrentUri);
    record ItemDeletedEvent(IFileInfo FileInfo, RepoUri CurrentUri) : IndexerEvent(FileInfo, CurrentUri);
    record ItemMovedEvent(IFileInfo FileInfo, RepoUri CurrentUri, RepoUri PreviousUri) : IndexerEvent(FileInfo, CurrentUri);
    record ItemUpdatedEvent(IFileInfo FileInfo, RepoUri CurrentUri) : IndexerEvent(FileInfo, CurrentUri);
    record ItemClassifiedEvent(IFileInfo FileInfo, RepoUri CurrentUri, SemanticMediaType MediaType) : IndexerEvent(FileInfo, CurrentUri);

    record ItemIndexedEvent(IFileInfo FileInfo, RepoUri CurrentUri, SemanticMediaType MediaType) : IndexerEvent(FileInfo, CurrentUri);
}
