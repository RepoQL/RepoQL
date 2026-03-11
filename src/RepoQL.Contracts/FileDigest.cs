using System.Buffers;
using System.Buffers.Binary;
using System.IO.Hashing;
using Microsoft.Extensions.FileProviders;

namespace RepoQL.Contracts;

/// <summary>
/// Computes deterministic file digests and bounds large-file work with head/tail sampling.
/// Centralizes digest scheme naming and size thresholds shared by indexing and fallback loaders.
/// </summary>
public static class FileDigest
{
    public const long SampledDigestThresholdBytes = 32L * 1024 * 1024;
    public const int SampleWindowBytes = 16 * 1024 * 1024;

    private const string FullScheme = "xxh64";
    private const string SampledScheme = "xxh64-sampled:v1";

    public static async ValueTask<string> ComputeAsync(IFileInfo file, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);

        if (file.Length <= SampledDigestThresholdBytes)
        {
            return await ComputeFullAsync(file, cancellationToken).ConfigureAwait(false);
        }

        return await ComputeSampledAsync(file, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<string> ComputeFullAsync(IFileInfo file, CancellationToken cancellationToken)
    {
        var hasher = new XxHash64();
        await using var stream = file.CreateReadStream();
        await hasher.AppendAsync(stream, cancellationToken).ConfigureAwait(false);
        return FormatDigest(FullScheme, hasher.GetCurrentHash());
    }

    private static async ValueTask<string> ComputeSampledAsync(IFileInfo file, CancellationToken cancellationToken)
    {
        await using var seekProbe = file.CreateReadStream();
        if (!seekProbe.CanSeek)
        {
            return await ComputeFullAsync(file, cancellationToken).ConfigureAwait(false);
        }

        var hasher = new XxHash64();

        Span<byte> lengthBytes = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(lengthBytes, file.Length);
        hasher.Append(lengthBytes);

        var headLength = (int)Math.Min(SampleWindowBytes, file.Length);
        await AppendRangeAsync(hasher, file, 0, headLength, cancellationToken).ConfigureAwait(false);

        var tailLength = (int)Math.Min(SampleWindowBytes, Math.Max(0L, file.Length - headLength));
        if (tailLength > 0)
        {
            await AppendRangeAsync(hasher, file, file.Length - tailLength, tailLength, cancellationToken).ConfigureAwait(false);
        }

        return FormatDigest(SampledScheme, hasher.GetCurrentHash());
    }

    private static async ValueTask AppendRangeAsync(XxHash64 hasher, IFileInfo file, long offset, int length, CancellationToken cancellationToken)
    {
        if (length <= 0)
        {
            return;
        }

        var rented = ArrayPool<byte>.Shared.Rent(Math.Min(length, 1024 * 1024));

        try
        {
            await using var stream = file.CreateReadStream();
            if (offset > 0)
            {
                stream.Seek(offset, SeekOrigin.Begin);
            }

            var remaining = length;
            while (remaining > 0)
            {
                var read = await stream.ReadAsync(rented.AsMemory(0, Math.Min(remaining, rented.Length)), cancellationToken).ConfigureAwait(false);
                if (read <= 0)
                {
                    throw new EndOfStreamException($"Unexpected end of stream while sampling '{file.Name}'.");
                }

                hasher.Append(rented.AsSpan(0, read));
                remaining -= read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static string FormatDigest(string scheme, byte[] hash)
        => scheme + ":" + Convert.ToHexString(hash).ToLowerInvariant();
}
