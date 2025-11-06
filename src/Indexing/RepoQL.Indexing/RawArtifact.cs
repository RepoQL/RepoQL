using System.IO.Hashing;
using DotNext.Threading;
using Microsoft.Extensions.FileProviders;
using RepoQL.Contracts;
using RepoQL.FileSystem.Abstractions;
using RepoQL.Indexing.Extensions;

namespace RepoQL.Indexing;

public class RawArtifact(IFileInfo file, IVirtualFileSystem sourceFileSystem) : IFileInfo
{
    private static async ValueTask<byte[]> HashAsync(IFileInfo file, CancellationToken ct)
    {
        var algo = new XxHash64();
        await using var stream = file.CreateReadStream();
        await algo.AppendAsync(stream, ct).ConfigureAwait(false);
        return algo.GetCurrentHash();
    }
    
    public IVirtualFileSystem FileSystem { get; } = sourceFileSystem;
    public Lazy<SemanticMediaType?> ProvisionalMediaType { get; } = new(file.GuessMediaTypeFromNamingConvention);
    
    public AsyncLazy<byte[]> Digest { get; } = new(async cancel => await HashAsync(file, cancel), true);
    public RepoUri Uri => FileSystem.GetUri(file);
    public Stream CreateReadStream() => file.CreateReadStream();

    public bool Exists => file.Exists;
    public long Length => file.Length;
    public string? PhysicalPath => file.PhysicalPath;
    public string Name => file.Name;
    public DateTimeOffset LastModified => file.LastModified;
    public bool IsDirectory =>  file.IsDirectory;
}