using System.Runtime.CompilerServices;
using RepoQL.Contracts.Embeddings;

namespace RepoQL.Core.Embeddings;

/// <summary>
/// A lightweight, local embedding provider that produces a deterministic hashed bag-of-words vector.
/// This is a placeholder for a true semantic model; it is local-only and fast.
/// </summary>
public sealed class HashedEmbeddingProvider : IEmbeddingProvider
{
    public string Model { get; }
    public int Dimension { get; }
    public bool Enabled => true;

    public HashedEmbeddingProvider(int dimension = 384, string? modelName = null)
    {
        Dimension = dimension > 0 ? dimension : 384;
        Model = string.IsNullOrWhiteSpace(modelName) ? $"hashed-{Dimension}" : modelName!;
    }

    public Task<float[]?> EmbedAsync(string text, CancellationToken cancellationToken = default)
    {
        var vec = new float[Dimension];
        if (string.IsNullOrWhiteSpace(text))
            return Task.FromResult<float[]?>(vec);

        var span = text.AsSpan();
        var i = 0;
        while (i < span.Length)
        {
            // skip non-alnum
            while (i < span.Length && !IsTokenChar(span[i])) i++;
            var start = i;
            while (i < span.Length && IsTokenChar(span[i])) i++;
            if (i > start)
            {
                var token = span.Slice(start, i - start);
                var idx = (int)(Hash64Lower(token) % (uint)Dimension);
                if (idx < 0) idx = -idx;
                vec[idx] += 1f;
            }
        }

        // L2 normalize
        var ss = vec.Aggregate<float, double>(0, (current, t) => current + t * t);

        var norm = (float)Math.Sqrt(ss);
        if (!(norm > 0)) 
            return Task.FromResult<float[]?>(vec);
        for (var d = 0; d < vec.Length; d++) vec[d] /= norm;
        return Task.FromResult<float[]?>(vec);

        static bool IsTokenChar(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '-';
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Hash64Lower(ReadOnlySpan<char> s)
    {
        // FNV-1a 64-bit, then fold to 32-bit
        const ulong fnvOffset = 1469598103934665603UL;
        const ulong fnvPrime = 1099511628211UL;
        var h = fnvOffset;
        foreach (var t in s)
        {
            var b = (byte)char.ToLowerInvariant(t);
            h ^= b;
            h *= fnvPrime;
        }
        return (uint)(h ^ (h >> 32));
    }
}

