using System.Runtime.CompilerServices;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.FileProviders.Physical;
using RepoQL.Contracts;
using RepoQL.FileSystem.Abstractions;
using IFileSystemWatcher = RepoQL.FileSystem.Abstractions.IFileSystemWatcher;

namespace RepoQL.FileSystem.Physical;

/// <summary>
/// A repository-backed content store with canonical URIs. By default it emits <c>file:///rel/path</c> URIs but the
/// constructor allows overriding scheme/authority/path prefixes so the same implementation can project directories as
/// <c>help:///...</c>, <c>github://owner/repo/... </c>, etc.
/// </summary>
/// <remarks>Create a repository store backed by <paramref name="rootPath"/>.</remarks>
public sealed class PhysicalFileSystem(
    string rootPath,
    string? scheme = null,
    string? uriPrefix = null,
    string? authority = null) : IVirtualFileSystem
{
    public string RootPath { get; } = Path.GetFullPath(rootPath);
    private readonly IFileProvider _fileSystem = new PhysicalFileProvider(rootPath);
    private readonly string _scheme = string.IsNullOrWhiteSpace(scheme)
        ? "file"
        : scheme.Trim().ToLowerInvariant();
    private readonly string? _uriPrefix = string.IsNullOrWhiteSpace(uriPrefix)
        ? null
        : NormalizeUriPath(uriPrefix.Trim('/'));
    private readonly string? _authority = string.IsNullOrWhiteSpace(authority)
        ? null
        : authority.Trim();

    /// <inheritdoc/>
    public string Scheme => _scheme;

    public IFileInfo GetFile(RepoUri uri)
    {
        var resolved = ResolveWithPrefix(uri);
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
        => ResolveWithPrefix(repoUri).AbsolutePath;

    /// <summary>Convert absolute filesystem path under root to a file:// URI.</summary>
    public RepoUri ToRepoUri(string absolutePath)
    {
        var full = Path.GetFullPath(absolutePath);
        if (!full.StartsWith(RootPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Path not under repo root.");
        var rel = NormalizeUriPath(Path.GetRelativePath(RootPath, full));
        var combined = string.IsNullOrEmpty(_uriPrefix)
            ? rel
            : string.IsNullOrEmpty(rel)
                ? _uriPrefix
                : $"{_uriPrefix}/{rel}";
        var uri = string.IsNullOrEmpty(_authority)
            ? $"{Scheme}:///{combined}"
            : $"{Scheme}://{_authority}/{combined}";
        return RepoUri.Parse(uri);
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
        if (!Directory.Exists(RootPath))
        {
            yield break;
        }

        var opts = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            // Skip hidden, system, and symlinks (ReparsePoint) to avoid indexing the same content twice
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint
        };

        // Enumerate all files recursively
        var files = Directory.EnumerateFiles(RootPath, "*", opts);

        foreach (var filePath in files)
        {
            if (ct.IsCancellationRequested)
                yield break;

            // Get relative path from the repository root
            var relativePath = Path.GetRelativePath(RootPath, filePath).Replace('\\', '/');

            // Exclude files within .git and .repoql directories (check relative path only)
            if (relativePath.Contains("/.git/") || relativePath.StartsWith(".git/") ||
                relativePath.Contains("/.repoql/") || relativePath.StartsWith(".repoql/"))
                continue;
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

    /// <summary>
    /// Normalizes a <see cref="RepoUri"/> by stripping any synthetic authority/prefix before delegating to the core
    /// resolver that enforces the repository root boundary.
    /// </summary>
    private FileUriPathResolver.ResolvedPath ResolveWithPrefix(RepoUri uri)
    {
        if (!string.Equals(uri.Scheme, Scheme, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"URI scheme must be '{Scheme}'.");

        var relativeSegment = uri.GetComponents(UriComponents.Path, UriFormat.Unescaped)
            .TrimStart('/');

        if (!string.IsNullOrEmpty(_authority))
        {
            if (!string.Equals(uri.Authority, _authority, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"URI '{uri}' does not match authority '{_authority}'.");
        }

        if (!string.IsNullOrEmpty(_uriPrefix))
        {
            var prefix = _uriPrefix!;
            if (!relativeSegment.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"URI '{uri}' does not belong to mount prefix '{prefix}'.");

            relativeSegment = relativeSegment[prefix.Length..].TrimStart('/');
        }

        var normalized = RepoUri.Parse($"{Scheme}:///{relativeSegment}");
        return FileUriPathResolver.Resolve(RootPath, normalized, Scheme);
    }

    private static string NormalizeUriPath(string path)
    {
        var normalized = path.Replace('\\', '/');
        if (OperatingSystem.IsWindows())
            return normalized.ToLowerInvariant();
        return normalized;
    }
}
