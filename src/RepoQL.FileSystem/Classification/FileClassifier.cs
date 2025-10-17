using System.IO;
using Microsoft.Extensions.FileProviders;
using MimeDetective;
using MimeDetective.Definitions;
using MimeDetective.Engine;
using RepoQL.Contracts;
using RepoQL.FileSystem.Abstractions;

namespace RepoQL.FileSystem.Classification;

public class FileClassifier : IFileClassifier
{
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
        if (MediaTypeMappings.TryGetMappedMediaType(fileInfo, out var mapped))
            return mapped;

        using var stream = fileInfo.CreateReadStream();
        var matches = _inspector.Inspect(stream, true);
        var mimeType = matches.FirstOrDefault()?.Definition.File.MimeType;
        if (!string.IsNullOrWhiteSpace(mimeType) && !string.Equals(mimeType, "application/octet-stream", StringComparison.OrdinalIgnoreCase))
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

        if (LooksLikePlainText(fileInfo))
            return PlainText;

        return OctetStream;
    }

    private static bool LooksLikePlainText(IFileInfo fileInfo)
    {
        try
        {
            using var stream = fileInfo.CreateReadStream();
            Span<byte> buffer = stackalloc byte[512];
            var read = stream.Read(buffer);
            if (read == 0)
                return true; // Empty files treated as text

            for (var i = 0; i < read; i++)
            {
                var b = buffer[i];
                if (b == 0)
                    return false;
                if (b < 0x09)
                    return false;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }
}
