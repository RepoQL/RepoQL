using Microsoft.Extensions.FileProviders;
using RepoQL.Contracts;

namespace RepoQL.FileSystem.Abstractions;

/// <summary>
/// A content store serves resources for a given URI scheme.
/// Implementations must be safe to call concurrently.
/// </summary>
public interface IVirtualFileSystem
{
    /// <summary>Scheme handled by this store, lower-case (e.g. "file", "embed").</summary>
    string Scheme { get; }

    /// <summary>
    /// Enumerate all canonical resource URIs 
    /// The implementation should yield results lazily and respect <paramref name="ct"/>.
    /// </summary>
    IAsyncEnumerable<IFileInfo> EnumerateAsync(CancellationToken ct);

    /// <summary>Open a read-only stream for the canonical resource URI. Caller disposes the stream.</summary>
    IFileInfo GetFile(RepoUri uri);

    /// <summary>Create a watcher that emits canonical resource changes.</summary>
    IFileSystemWatcher Watch();
}