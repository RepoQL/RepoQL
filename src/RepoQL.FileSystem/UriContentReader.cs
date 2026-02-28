using System.Text;
using Microsoft.Extensions.FileProviders;
using RepoQL.Contracts;
using RepoQL.FileSystem.Abstractions;

namespace RepoQL.FileSystem;

/// <summary>
/// Purpose: Resolve any RepoURI to a read-only text stream with size metadata.
/// Complexity: Delegates URI routing to IMultiFileSystem, strips fragments to container
/// URIs, and provides a disposable wrapper that owns both stream and reader lifetimes.
/// </summary>
public sealed class UriContentReader(IMultiFileSystem fileSystem)
{
    private readonly IMultiFileSystem _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));

    public UriTextContent Open(RepoUri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        if (!RepoUri.TryParse(uri.Container.AbsoluteUri, out var target))
            throw new InvalidOperationException($"Unable to resolve container URI for '{uri}'.");

        var file = _fileSystem.GetFile(target);
        if (!file.Exists || file.IsDirectory)
            throw new FileNotFoundException($"No readable file found for URI '{target}'.", target.AbsoluteUri);

        return UriTextContent.Create(uri, file);
    }

    public bool TryOpen(RepoUri uri, out UriTextContent? content)
    {
        content = null;
        if (uri is null)
            return false;

        try
        {
            content = Open(uri);
            return true;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>
/// Purpose: Own a read-only text stream and associated metadata for URI content access.
/// Complexity: Wraps stream + StreamReader, preserving content size and disposing both
/// resources together so callers can safely pass it across layers.
/// </summary>
public sealed class UriTextContent : IDisposable
{
    private UriTextContent(
        RepoUri requestedUri,
        RepoUri sourceUri,
        long sizeBytes,
        Stream stream,
        StreamReader reader)
    {
        RequestedUri = requestedUri;
        SourceUri = sourceUri;
        SizeBytes = sizeBytes;
        Stream = stream;
        Reader = reader;
    }

    public RepoUri RequestedUri { get; }

    public RepoUri SourceUri { get; }

    public long SizeBytes { get; }

    public Stream Stream { get; }

    public StreamReader Reader { get; }

    internal static UriTextContent Create(RepoUri requestedUri, IFileInfo file)
    {
        if (!RepoUri.TryParse(requestedUri.Container.AbsoluteUri, out var source))
            throw new InvalidOperationException($"Unable to resolve container URI for '{requestedUri}'.");

        var stream = file.CreateReadStream();
        var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: false);
        return new UriTextContent(requestedUri, source, file.Length, stream, reader);
    }

    public void Dispose()
    {
        Reader.Dispose();
    }
}
