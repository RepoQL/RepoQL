using Microsoft.Extensions.FileProviders;

namespace RepoQL.FileSystem.Embedded;

internal sealed class EmbeddedLogicalFileInfo(IFileInfo inner, string virtualPath) : IFileInfo
{
    public bool Exists => inner.Exists;
    public long Length => inner.Length;
    public string PhysicalPath => virtualPath;
    public string Name => inner.Name;
    public DateTimeOffset LastModified => inner.LastModified;
    public bool IsDirectory => inner.IsDirectory;
    public Stream CreateReadStream() => inner.CreateReadStream();
}