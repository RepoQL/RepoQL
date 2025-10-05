using RepoQL.Contracts;

namespace RepoQL.FileSystem.Abstractions;

/// <summary>
/// Registry that resolves a content store by a resource URI.
/// </summary>
public interface IFileSystemRegistry
{
    /// <summary>Resolve the store responsible for <paramref name="uri"/>.</summary>
    IVirtualFileSystem Resolve(RepoUri uri);
}