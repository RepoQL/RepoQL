using RepoQL.Contracts;
using RepoQL.Formats.Pdf.Surface;

namespace RepoQL.Formats.Pdf;

internal sealed class PdfDocumentState
{
    public required PdfDocumentSurface Surface { get; init; }
    public required string Digest { get; init; }
    public required long Size { get; init; }
    public required SemanticMediaType MediaType { get; init; }
    public required string StoreUri { get; init; }
}
