using RepoQL.Contracts;
using RepoQL.Formats.Docx.Surface;

namespace RepoQL.Formats.Docx;

internal sealed class DocxDocumentState
{
    public required DocumentSurface Surface { get; init; }
    public required string Digest { get; init; }
    public required long Size { get; init; }
    public required SemanticMediaType MediaType { get; init; }
    public required string StoreUri { get; init; }
}
