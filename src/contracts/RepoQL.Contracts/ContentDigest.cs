using System.IO.Hashing;

namespace RepoQL.Contracts;

/// <summary>
///     Helpers for computing deterministic content digests.
/// </summary>
public static class ContentDigest
{
    /// <summary>
    ///     Compute an xxHash64 digest string ("xxh64:&lt;hex&gt;") from the supplied bytes.
    /// </summary>
    public static string FromBytes(ReadOnlySpan<byte> data)
    {
        Span<byte> hash = stackalloc byte[8];
        XxHash64.Hash(data, hash);
        return "xxh64:" + Convert.ToHexString(hash).ToLowerInvariant();
    }
}
