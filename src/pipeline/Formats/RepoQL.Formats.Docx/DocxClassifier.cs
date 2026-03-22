using DocumentFormat.OpenXml.Packaging;
using RepoQL.Contracts;
using RepoQL.Indexing.Indexing.Pipelines;
using RepoQL.Indexing.Indexing.Pipelines.Discovery;

namespace RepoQL.Formats.Docx;

/// <summary>
/// Classifier for Word OpenXML document files.
///
/// Purpose: Validates that .docx/.docm/.dotx files are readable OpenXML packages
/// and assigns a semantic kind for downstream parsing.
///
/// Complexity: Light content validation to avoid classifying invalid files.
/// </summary>
public sealed class DocxClassifier : IAsyncPipeline<IDiscoveredArtifact, SemanticMediaType?>
{
    public Task<(SemanticMediaType? Result, PipelineResult PipelineStatus)> ProcessAsync(
        IDiscoveredArtifact item,
        CallNextPipeline<IDiscoveredArtifact, SemanticMediaType?> next,
        CancellationToken token)
    {
        var extension = Path.GetExtension(item.Name);
        if (!DocxMediaTypes.TryResolveByExtension(extension, out var mediaType))
            return next(item);

        try
        {
            using var stream = item.CreateReadStream();
            using var document = WordprocessingDocument.Open(stream, false);

            if (document.MainDocumentPart?.Document?.Body is null)
                return next(item);

            return Task.FromResult<(SemanticMediaType?, PipelineResult)>((mediaType, PipelineResult.Success));
        }
        catch
        {
            // Invalid package; do not classify as DOCX.
            return next(item);
        }
    }
}
