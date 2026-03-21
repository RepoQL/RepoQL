using System.IO.Hashing;
using Microsoft.Extensions.FileProviders;
using RepoQL.Contracts;
namespace RepoQL.Core;

/// <summary>
/// xxHash64 hasher that reads the provided stream and returns 8-byte digest.
/// </summary>
public sealed class XxHasher : IHasher
{
    /// <inheritdoc/>
    public async ValueTask<byte[]> HashAsync(IFileInfo file, CancellationToken ct)
    {
        var algo = new XxHash64();
        await using var stream = file.CreateReadStream();
        await algo.AppendAsync(stream, ct).ConfigureAwait(false);
        return algo.GetCurrentHash();
    }
}