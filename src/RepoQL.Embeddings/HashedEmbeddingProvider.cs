using System.Runtime.CompilerServices;
using RepoQL.Contracts.Embeddings;

namespace RepoQL.Embeddings;

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
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<float[]?>(EmbedCore(text));
    }

    public Task<float[]?[]> EmbedBatchAsync(IReadOnlyList<string>? texts, CancellationToken cancellationToken = default)
    {
        if (texts is null || texts.Count == 0)
            return Task.FromResult(Array.Empty<float[]?>());

        var results = new float[]?[texts.Count];
        for (var index = 0; index < texts.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results[index] = EmbedCore(texts[index]);
        }
        return Task.FromResult(results);
    }

    private float[] EmbedCore(string text)
    {
        var vec = new float[Dimension];
        if (string.IsNullOrWhiteSpace(text))
            return vec;

        var span = text.AsSpan();
        var i = 0;
        while (i < span.Length)
        {
            while (i < span.Length && !IsTokenChar(span[i])) i++;
            var start = i;
            while (i < span.Length && IsTokenChar(span[i])) i++;
            if (i <= start)
                continue;

            var token = span.Slice(start, i - start);
            var idx = (int)(Hash64Lower(token) % (uint)Dimension);
            if (idx < 0) idx = -idx;
            vec[idx] += 1f;
        }

        double sumSquares = 0;
        for (var index = 0; index < vec.Length; index++)
        {
            var value = vec[index];
            sumSquares += (double)value * value;
        }

        var norm = (float)Math.Sqrt(sumSquares);
        if (!(norm > 0))
            return vec;

        for (var d = 0; d < vec.Length; d++) vec[d] /= norm;
        return vec;

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
