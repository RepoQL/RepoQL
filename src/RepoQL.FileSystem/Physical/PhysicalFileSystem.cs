using System.Runtime.CompilerServices;
using Microsoft.Extensions.FileProviders;
using RepoQL.Contracts;
using RepoQL.FileSystem.Abstractions;
using IFileSystemWatcher = RepoQL.FileSystem.Abstractions.IFileSystemWatcher;

namespace RepoQL.FileSystem.Physical;

/// <summary>
/// A repository-backed content store with canonical <c>file:///rel/path</c> URIs.
/// Maps to a concrete filesystem root given at construction.
/// </summary>
/// <remarks>Create a repository store backed by <paramref name="rootPath"/></remarks>
public sealed class PhysicalFileSystem(string rootPath) : IVirtualFileSystem
{
    public string RootPath { get; } = Path.GetFullPath(rootPath);
    private readonly IFileProvider _fileSystem = new PhysicalFileProvider(rootPath);

    /// <inheritdoc/>
    public string Scheme => "file";

    public IFileInfo GetFile(RepoUri uri)
    {
        return _fileSystem.GetFileInfo(ToAbsolutePath(uri));
    }

    /// <summary>Convert file:// URI to absolute filesystem path.</summary>
    public string ToAbsolutePath(RepoUri repoUri)
    {
        if (!string.Equals(repoUri.Scheme, Scheme, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"URI scheme must be '{Scheme}'.");
        var rel = repoUri.AbsolutePath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        return Path.Combine(RootPath, rel);
    }

    /// <summary>Convert absolute filesystem path under root to a file:// URI.</summary>
    public RepoUri ToRepoUri(string absolutePath)
    {
        var full = Path.GetFullPath(absolutePath);
        if (!full.StartsWith(RootPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Path not under repo root.");
        var rel = Path.GetRelativePath(RootPath, full).Replace('\\', '/');
        return RepoUri.Parse($"{Scheme}:///{rel}");
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<IFileInfo> EnumerateAsync([EnumeratorCancellation] CancellationToken ct)
    {
        // Ensure the root path exists and is a directory
        if (!Directory.Exists(rootPath))
        {
            yield break;
        }

        var opts = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System
        };

        // Enumerate all files recursively, excluding .git and .repoql directories
        var files = Directory.EnumerateFiles(rootPath, "*", opts)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}") &&
                       !f.Contains($"{Path.DirectorySeparatorChar}.repoql{Path.DirectorySeparatorChar}"));

        foreach (var filePath in files)
        {
            if (ct.IsCancellationRequested)
                yield break;

            // Get relative path from the repository root
            var relativePath = Path.GetRelativePath(RootPath, filePath).Replace('\\', '/');

            // Create IFileInfo for the file
            var fileInfo = _fileSystem.GetFileInfo(relativePath);

            if (fileInfo.Exists)
            {
                yield return fileInfo;
            }

            // Yield control periodically to prevent blocking
            await Task.Yield();
        }
    }


    /// <inheritdoc/>
    public IFileSystemWatcher Watch() => new PhysicalFileSystemWatcher(this);
}