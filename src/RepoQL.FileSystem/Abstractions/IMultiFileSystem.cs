using Microsoft.Extensions.FileProviders;
using RepoQL.Contracts;

namespace RepoQL.FileSystem.Abstractions;

/// <summary>
/// Composite hub over multiple <see cref="IVirtualFileSystem"/>s that can enumerate all files,
/// resolve a file by RepoURI, and expose a merged watcher for change events.
/// </summary>
public interface IMultiFileSystem
{
    /// <summary>
    /// Enumerate files across all registered stores, yielding their associated RepoURI.
    /// </summary>
    IAsyncEnumerable<EnumeratedResource> EnumerateAsync(CancellationToken ct);

    /// <summary>
    /// Resolve and return a file for the given RepoURI via the appropriate store.
    /// </summary>
    IFileInfo GetFile(RepoUri uri);

    /// <summary>
    /// Create a watcher that fans-in change events from all stores.
    /// </summary>
    IFileSystemWatcher WatchAll();
}

/// <summary>
/// A file discovered during enumeration together with its canonical RepoURI.
/// </summary>
/// <param name="File">The file handle.</param>
/// <param name="Uri">The canonical RepoURI for the file.</param>
public sealed record EnumeratedResource(IFileInfo File, RepoUri Uri);

