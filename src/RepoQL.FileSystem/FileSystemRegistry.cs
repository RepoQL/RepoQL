using RepoQL.Contracts;
using RepoQL.FileSystem.Abstractions;

namespace RepoQL.FileSystem;

/// <summary>
/// Registry that resolves content stores by scheme.
/// </summary>
/// <remarks>Create a registry with the supplied stores.</remarks>
public sealed class FileSystemRegistry(IEnumerable<IVirtualFileSystem> stores) : IFileSystemRegistry
{
    private readonly Dictionary<string, IVirtualFileSystem> _byScheme = stores.ToDictionary(s => s.Scheme, s => s, StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc/>
    public IVirtualFileSystem Resolve(RepoUri uri)
    {
        if (_byScheme.TryGetValue(uri.Scheme, out var s)) return s;
        throw new NotSupportedException($"No content store registered for scheme '{uri.Scheme}'.");
    }
}