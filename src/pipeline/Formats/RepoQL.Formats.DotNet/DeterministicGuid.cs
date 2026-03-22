using System.Security.Cryptography;
using System.Text;

namespace RepoQL.Formats.DotNet;

internal static class DeterministicGuid
{
    public static Guid Create(params string[] parts)
    {
        if (parts.Length == 0)
            return Guid.Empty;

        var builder = new StringBuilder();
        for (var i = 0; i < parts.Length; i++)
        {
            if (i > 0)
                builder.Append('|');
            builder.Append(parts[i] ?? string.Empty);
        }

        var bytes = Encoding.UTF8.GetBytes(builder.ToString());
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes / 2];
        var fullHash = SHA256.HashData(bytes);
        fullHash.AsSpan(0, hash.Length).CopyTo(hash);
        return new Guid(hash);
    }
}
