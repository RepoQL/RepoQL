using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using RepoQL.Embedding;

namespace RepoQL.Embedding.Service.Cache;

internal static class EmbeddingCachePrimitives
{
    public static string ComputeChunkFingerprint(string context, string chunkText)
    {
        var value = string.Concat(context, "\0", chunkText);
        return ComputeSha256Hex(value);
    }

    public static string ComputeSourceHash(string source)
        => ComputeSha256Hex(source);

    public static IReadOnlyList<ChunkFingerprint> BuildFingerprints(EmbedChunksRequest request)
    {
        var fingerprints = new List<ChunkFingerprint>();
        var flatIndex = 0;

        foreach (var group in request.Groups)
        {
            foreach (var chunk in group.Chunks)
            {
                fingerprints.Add(new ChunkFingerprint(
                    flatIndex,
                    ComputeChunkFingerprint(group.Context, chunk),
                    chunk,
                    group.Context));
                flatIndex++;
            }
        }

        return fingerprints;
    }

    public static byte[] NarrowVectorToBytes(IReadOnlyList<float> vector)
    {
        var bytes = new byte[vector.Count];

        for (var i = 0; i < vector.Count; i++)
        {
            var value = vector[i];
            if (!float.IsFinite(value))
                throw new InvalidOperationException($"Vector element {i} is not finite.");

            if (value < sbyte.MinValue || value > sbyte.MaxValue)
                throw new InvalidOperationException(
                    $"Vector element {i} value {value.ToString(CultureInfo.InvariantCulture)} is outside [{sbyte.MinValue}, {sbyte.MaxValue}].");

            var integral = MathF.Truncate(value);
            if (value != integral)
                throw new InvalidOperationException(
                    $"Vector element {i} value {value.ToString(CultureInfo.InvariantCulture)} is not an int8-valued float.");

            bytes[i] = unchecked((byte)(sbyte)integral);
        }

        return bytes;
    }

    public static float[] WidenVectorToFloats(IReadOnlyList<byte> bytes)
    {
        var floats = new float[bytes.Count];
        for (var i = 0; i < bytes.Count; i++)
            floats[i] = unchecked((sbyte)bytes[i]);

        return floats;
    }

    public static byte[] ReadVectorBytes(object? value)
    {
        return value switch
        {
            null => [],
            byte[] bytes => bytes,
            sbyte[] sbytes => sbytes.Select(static b => unchecked((byte)b)).ToArray(),
            List<byte> byteList => [.. byteList],
            List<sbyte> sbyteList => sbyteList.Select(static b => unchecked((byte)b)).ToArray(),
            IEnumerable<byte> byteEnumerable => [.. byteEnumerable],
            IEnumerable<sbyte> sbyteEnumerable => sbyteEnumerable.Select(static b => unchecked((byte)b)).ToArray(),
            Array array => ConvertArrayToBytes(array),
            _ => [],
        };
    }

    private static byte[] ConvertArrayToBytes(Array array)
    {
        var bytes = new byte[array.Length];
        for (var i = 0; i < array.Length; i++)
        {
            var value = array.GetValue(i);
            if (value is null)
                throw new InvalidOperationException("Encountered null vector element when decoding DuckDB result.");

            bytes[i] = unchecked((byte)Convert.ToSByte(value, CultureInfo.InvariantCulture));
        }

        return bytes;
    }

    private static string ComputeSha256Hex(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexStringLower(hash);
    }
}
