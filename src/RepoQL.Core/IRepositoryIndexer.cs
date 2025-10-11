using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using RepoQL.Contracts;

namespace RepoQL.Core;

public interface IRepositoryIndexer : IHostedService, IObservable<IndexerEvent>, IAsyncDisposable
{
    Task QueueForIndexingAsync(IEnumerable<IFileInfo> files, bool skipUnchanged = true);
    Task QueueForIndexingAsync(IEnumerable<RepoUri> uris, bool skipUnchanged = true);
    Task WaitForWriterIdle(CancellationToken cancellationToken = default);
    Task WaitForIdle(CancellationToken cancellationToken = default);
    Task WaitForStagesIdleAsync(PipelineStage stages, CancellationToken cancellationToken = default);
    Task WaitForAnyStageIdleAsync(PipelineStage stages, CancellationToken cancellationToken = default);

    PipelineSnapshot GetPipelineSnapshot();

    bool IsReindexing { get; }
    IDisposable EnterReindexScope();

    int ClassificationQueueDepth { get; }
    int ParsingQueueDepth { get; }
    int EnrichmentQueueDepth { get; }

    internal record ItemDiscoveredEvent(IFileInfo FileInfo, RepoUri CurrentUri) : IndexerEvent(FileInfo, CurrentUri);
    internal record ItemDeletedEvent(IFileInfo FileInfo, RepoUri CurrentUri) : IndexerEvent(FileInfo, CurrentUri);
    internal record ItemMovedEvent(IFileInfo FileInfo, RepoUri CurrentUri, RepoUri PreviousUri) : IndexerEvent(FileInfo, CurrentUri);
    internal record ItemUpdatedEvent(IFileInfo FileInfo, RepoUri CurrentUri) : IndexerEvent(FileInfo, CurrentUri);
    internal record ItemClassifiedEvent(IFileInfo FileInfo, RepoUri CurrentUri, SemanticMediaType MediaType) : IndexerEvent(FileInfo, CurrentUri);

    record ItemIndexedEvent(IFileInfo FileInfo, RepoUri CurrentUri, SemanticMediaType MediaType) : IndexerEvent(FileInfo, CurrentUri);
}
