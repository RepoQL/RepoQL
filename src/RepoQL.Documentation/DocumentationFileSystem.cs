using Microsoft.Extensions.FileProviders;
using RepoQL.Contracts;
using RepoQL.FileSystem.Abstractions;
using RepoQL.FileSystem.Embedded;

namespace RepoQL.Documentation;

/// <summary>
/// Virtual file system exposing the embedded documentation bundle under the <c>help://</c> scheme. Registered
/// automatically by <c>AddRepoIndexer</c> so every host can reference <c>help:///…</c> without importing anything.
/// </summary>
public sealed class DocumentationFileSystem : IVirtualFileSystem
{
    public const string Scheme = "help";
    private readonly EmbeddedStore _store = new(typeof(DocumentationMarker).Assembly, Scheme);

    string IVirtualFileSystem.Scheme => Scheme;

    public IAsyncEnumerable<IFileInfo> EnumerateAsync(CancellationToken ct)
        => _store.EnumerateAsync(ct);

    public IFileInfo GetFile(RepoUri uri) => _store.GetFile(uri);

    public RepoUri GetUri(IFileInfo file) => _store.GetUri(file);

    public IFileSystemWatcher Watch() => _store.Watch();
}
