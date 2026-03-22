using Microsoft.Extensions.FileProviders;

namespace RepoQL.Contracts;

/// <summary>
/// Compute a compact content fingerprint. Implementations should stream the provided stream and return exactly 8 bytes.
/// </summary>
public interface IHasher
{
    /// <summary>Compute an 8-byte fingerprint from <paramref name="file"/>.</summary>
    ValueTask<byte[]> HashAsync(IFileInfo file, CancellationToken ct);
}