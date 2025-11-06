using System.Runtime.CompilerServices;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.FileProviders.Physical;
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
        var resolved = FileUriPathResolver.Resolve(RootPath, uri);
        var relative = string.IsNullOrEmpty(resolved.RelativePath)
            ? "."
            : resolved.RelativePath;

        var info = _fileSystem.GetFileInfo(relative);
        return info is NotFoundFileInfo
            ? new PhysicalFileInfo(new FileInfo(resolved.AbsolutePath))
            : info;
    }

    /// <summary>Convert file:// URI to absolute filesystem path.</summary>
    private string ToAbsolutePath(RepoUri repoUri)
        => FileUriPathResolver.ToAbsolutePath(RootPath, repoUri);

    /// <summary>Convert absolute filesystem path under root to a file:// URI.</summary>
    public RepoUri ToRepoUri(string absolutePath)
    {
        var full = Path.GetFullPath(absolutePath);
        if (!full.StartsWith(RootPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Path not under repo root.");
        var rel = Path.GetRelativePath(RootPath, full).Replace('\\', '/');
        return RepoUri.Parse($"{Scheme}:///{rel}");
    }

    public RepoUri GetUri(IFileInfo file)
    {
        if (file?.PhysicalPath == null)
            throw new ArgumentException("File must have a PhysicalPath", nameof(file));

        // Use the existing ToRepoUri method to convert the physical path
        return ToRepoUri(file.PhysicalPath);
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
