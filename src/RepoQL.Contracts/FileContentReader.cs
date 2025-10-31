using System.Text;
using Microsoft.Extensions.FileProviders;

namespace RepoQL.Contracts;

/// <summary>
///     Utility helpers for loading artifact content once while also computing digests.
/// </summary>
public static class FileContentReader
{
    public static async Task<LoadedText> ReadAllTextWithDigestAsync(
        IFileInfo file,
        Encoding? encoding = null,
        bool detectEncodingFromByteOrderMarks = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        encoding ??= Encoding.UTF8;

        await using var stream = file.CreateReadStream();
        using var buffer = new MemoryStream(capacity: stream.CanSeek ? (int)Math.Min(stream.Length, int.MaxValue) : 0);
        await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);

        ReadOnlySpan<byte> dataSpan;
        byte[]? rented = null;
        if (buffer.TryGetBuffer(out var segment))
        {
            dataSpan = segment.AsSpan(0, (int)buffer.Length);
        }
        else
        {
            rented = buffer.ToArray();
            dataSpan = rented.AsSpan();
        }

        var digest = ContentDigest.FromBytes(dataSpan);

        buffer.Position = 0;
        using var reader = new StreamReader(buffer, encoding, detectEncodingFromByteOrderMarks, bufferSize: 1024, leaveOpen: true);
        var text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

        return new LoadedText(text, digest, file.Length);
    }
}

public sealed record LoadedText(string Text, string Digest, long ByteLength);
