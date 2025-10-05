using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.FileProviders;
using RepoQL.Contracts;
using RepoQL.FileSystem.Abstractions;

namespace RepoQL.FileSystem.InMemory;

/// <summary>
/// In-memory virtual file system suitable for tests. URIs use the scheme "mem" and the
/// physical path format "{root}/{relative}" (e.g., mem://repo/docs/a.md).
/// </summary>
public sealed class MemoryFileSystem(string defaultRoot = "repo") : IVirtualFileSystem
{
    private readonly ConcurrentDictionary<string, MemoryFileInfo> _files = new(StringComparer.Ordinal);
    private readonly MemoryWatcher _watcher = new();

    public string DefaultRoot { get; } = string.IsNullOrWhiteSpace(defaultRoot) ? "repo" : defaultRoot;

    public string Scheme => "mem";

    public async IAsyncEnumerable<IFileInfo> EnumerateAsync([EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var kv in _files.ToArray())
        {
            if (ct.IsCancellationRequested) yield break;
            yield return kv.Value;
            await Task.Yield();
        }
    }

    public IFileInfo GetFile(RepoUri uri)
    {
        // Expected form: mem://{physicalPath}
        if (!string.Equals(uri.Scheme, Scheme, StringComparison.OrdinalIgnoreCase))
            return new NotFoundFileInfo(uri.AbsoluteUri);

        var key = ExtractPhysicalPath(uri);
        if (_files.TryGetValue(key, out var fi))
            return fi;
        return new NotFoundFileInfo(uri.AbsoluteUri);
    }

    public IFileSystemWatcher Watch() => _watcher;

    // --------- mutation API for tests ---------

    /// <summary>Add or update a text file at the given relative path under the default root.</summary>
    public void AddOrUpdateText(string relativePath, string content)
        => AddOrUpdate(DefaultRoot, relativePath, System.Text.Encoding.UTF8.GetBytes(content ?? string.Empty));

    /// <summary>Add or update a file at root/relative with the provided bytes.</summary>
    public void AddOrUpdate(string root, string relativePath, byte[] bytes)
    {
        var key = NormalizePath(root, relativePath);
        var exists = _files.ContainsKey(key);
        var now = DateTimeOffset.UtcNow;
        var fi = new MemoryFileInfo(key, bytes, now);
        _files[key] = fi;

        if (_watcher.IsStarted)
        {
            var uri = RepoUri.Parse($"{Scheme}://{key}");
            _watcher.SafeRaiseChange(exists ? ResourceEvent.Updated : ResourceEvent.Created, fi, uri);
        }
    }

    /// <summary>Delete a file at root/relative. Returns true if removed.</summary>
    public bool Delete(string root, string relativePath)
    {
        var key = NormalizePath(root, relativePath);
        if (_files.TryRemove(key, out var fi))
        {
            if (_watcher.IsStarted)
            {
                var uri = RepoUri.Parse($"{Scheme}://{key}");
                _watcher.SafeRaiseChange(ResourceEvent.Deleted, fi, uri);
            }
            return true;
        }
        return false;
    }

    internal static string ExtractPhysicalPath(RepoUri uri)
    {
        // For mem://root/rel/path -> Authority + AbsolutePath.TrimStart('/')
        var host = uri.Authority;
        var path = uri.AbsolutePath.TrimStart('/');
        return string.IsNullOrEmpty(host) ? path : host + (path.Length > 0 ? "/" + path : string.Empty);
    }

    private static string NormalizePath(string root, string relative)
    {
        var r = (root ?? string.Empty).Trim('/');
        var p = (relative ?? string.Empty).Replace('\\', '/').TrimStart('/');
        return string.IsNullOrEmpty(r) ? p : r + (p.Length > 0 ? "/" + p : string.Empty);
    }

    // --------- nested types ---------

    private sealed class MemoryWatcher : FileSystemWatcherBase
    {
        protected override Task OnStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        protected override Task OnStopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public new void SafeRaiseChange(ResourceEvent kind, IFileInfo file, RepoUri currentUri, RepoUri? previousUri = null)
            => base.SafeRaiseChange(kind, file, currentUri, previousUri);
        public new bool IsStarted => base.IsStarted;
    }

    private sealed class MemoryFileInfo(string physicalPath, byte[] content, DateTimeOffset lastModified)
        : IFileInfo
    {
        private readonly byte[] _content = content ?? [];

        public bool Exists => true;
        public long Length => _content.LongLength;
        public string PhysicalPath { get; } = physicalPath;
        public string Name { get; } = physicalPath.Split('/').LastOrDefault() ?? physicalPath;
        public DateTimeOffset LastModified { get; } = lastModified;
        public bool IsDirectory => false;
        public Stream CreateReadStream() => new MemoryStream(_content, writable: false);
    }
}