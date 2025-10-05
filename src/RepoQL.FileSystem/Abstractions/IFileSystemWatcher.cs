using Microsoft.Extensions.Hosting;

namespace RepoQL.FileSystem.Abstractions;

/// <summary>
///     Watcher interface for a content store. The watcher exposes events and can be started/stopped.
/// </summary>
public interface IFileSystemWatcher : IObservable<ResourceChange>, IHostedService, IAsyncDisposable;