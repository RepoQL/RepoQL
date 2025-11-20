using System.IO.Hashing;
using DotNext.Threading;
using Microsoft.Extensions.FileProviders;
using RepoQL.Contracts;
using RepoQL.FileSystem.Abstractions;
using RepoQL.Indexing.Extensions;

namespace RepoQL.Indexing;

/// <summary>
/// Wraps <see cref="IFileInfo"/> with lazy digest computation and provisional media type.
/// Represents a file discovered by <see cref="RepoqlHost"/> before classification.
/// </summary>
/// <remarks>
/// <para><strong>Lazy Digest</strong></para>
/// <para>
/// <see cref="Digest"/> is <see cref="AsyncLazy{T}"/>. Content hash (xxHash64) computed
/// only when accessed. Avoids hashing files that will be filtered out or skipped by catalog.
/// </para>
///
/// <para><strong>Provisional Media Type</strong></para>
/// <para>
/// <see cref="ProvisionalMediaType"/> guessed from file extension before classification runs.
/// Used as fallback if classifiers return null. Stored in <see cref="Lazy{T}"/> for efficient access.
/// </para>
///
/// <para><strong>RepoUri</strong></para>
/// <para>
/// <see cref="Uri"/> computed from <see cref="FileSystem"/> and <paramref name="file"/> path.
/// Represents canonical identifier for this file across the repository.
/// </para>
///
/// <para><strong>Lifecycle</strong></para>
/// <para>
/// Created by <see cref="RepoqlHost"/> for each discovered file. Wrapped in <see cref="IndexItem"/>
/// and flows through pipeline. Digest accessed during catalog check in <see cref="IndexingEngine.IndexItemAsync"/>.
/// </para>
/// </remarks>
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

    public bool IsReadOnly { get; } = file is IFileAnalysisMetadata meta && meta.IsReadOnly;
}
