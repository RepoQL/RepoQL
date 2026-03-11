using Microsoft.Extensions.FileProviders;
using MimeDetective;
using MimeDetective.Definitions;
using MimeDetective.Engine;
using RepoQL.Contracts;
using RepoQL.FileSystem.Abstractions;

namespace RepoQL.FileSystem.Classification;

public class FileClassifier : IFileClassifier
{
    private const int InspectionByteCount = 64 * 1024;
    private static readonly SemanticMediaType PlainText = SemanticMediaType.Create("text", "plain");
    private static readonly SemanticMediaType OctetStream = SemanticMediaType.Create("application", "octet-stream");

    private readonly IContentInspector _inspector = new ContentInspectorBuilder
    {
        Definitions = DefaultDefinitions.All(),
        StringSegmentOptions = new StringSegmentOptionsBuilder
        {
            OptimizeFor = StringSegmentResourceOptimization.HighSpeed
        }
    }.Build();

    public SemanticMediaType GetMediaType(IFileInfo fileInfo)
    {
        var mapped = fileInfo.GuessMediaTypeFromNamingConvention();
        if (mapped is not null)
            return mapped;

        if (!TryReadPrefix(fileInfo, out var prefix, out var bytesRead))
            return OctetStream;

        var matches = _inspector.Inspect(prefix.AsSpan(0, bytesRead));
        var mimeType = matches.FirstOrDefault()?.Definition.File.MimeType;
        if (!string.IsNullOrWhiteSpace(mimeType)
            && !string.Equals(mimeType, "application/octet-stream", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                return SemanticMediaType.Parse(mimeType);
            }
            catch
            {
                // fall back to additional heuristics below
            }
        }

        if (LooksLikePlainText(prefix.AsSpan(0, bytesRead)))
            return PlainText;

        return OctetStream;
    }

    private static bool TryReadPrefix(IFileInfo fileInfo, out byte[] buffer, out int bytesRead)
    {
        var capacity = fileInfo.Length switch
        {
            <= 0 => InspectionByteCount,
            < InspectionByteCount => (int)fileInfo.Length,
            _ => InspectionByteCount
        };

        buffer = new byte[Math.Max(capacity, 1)];
        bytesRead = 0;

        try
        {
            using var stream = fileInfo.CreateReadStream();
            while (bytesRead < buffer.Length)
            {
                var read = stream.Read(buffer, bytesRead, buffer.Length - bytesRead);
                if (read <= 0)
                    break;

                bytesRead += read;
            }

            return true;
        }
        catch
        {
            bytesRead = 0;
            return false;
        }
    }

    private static bool LooksLikePlainText(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length == 0)
            return true; // Empty files treated as text

        for (var i = 0; i < buffer.Length; i++)
        {
            var b = buffer[i];
            if (b == 0)
                return false;
            if (b < 0x09)
                return false;
        }

        return true;
    }
}
