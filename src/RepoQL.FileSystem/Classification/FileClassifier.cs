using Microsoft.Extensions.FileProviders;
using MimeDetective;
using MimeDetective.Definitions;
using MimeDetective.Engine;
using RepoQL.Contracts;

namespace RepoQL.FileSystem;

public class FileClassifier : IFileClassifier
{
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
        using var stream = fileInfo.CreateReadStream();
        var matches = _inspector.Inspect(stream, true);
        var mimeType = matches.FirstOrDefault()?.Definition.File.MimeType;
        if (string.IsNullOrWhiteSpace(mimeType))
            mimeType = "application/octet-stream"; // Default for unknown types
        return SemanticMediaType.Parse(mimeType);
    }
}