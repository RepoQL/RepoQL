using Microsoft.Extensions.FileProviders;

namespace RepoQL.SnapshotGenerator;

/// <summary>
/// Purpose: Adapts <see cref="FileInfo"/> to <see cref="IFileInfo"/> for the format loader.
/// Complexity: Trivial delegation.
/// </summary>
internal sealed class PhysicalFileInfoAdapter(FileInfo fi) : IFileInfo
{
    public bool Exists => fi.Exists;
    public long Length => fi.Length;
    public string? PhysicalPath => fi.FullName;
    public string Name => fi.Name;
    public DateTimeOffset LastModified => fi.LastWriteTimeUtc;
    public bool IsDirectory => false;
    public Stream CreateReadStream() => fi.OpenRead();
}
