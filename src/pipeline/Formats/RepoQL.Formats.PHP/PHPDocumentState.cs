using RepoQL.Contracts;
using RepoQL.Formats.PHP.Surface;

namespace RepoQL.Formats.PHP;

internal sealed class PHPDocumentState
{
    public required Guid DocumentId { get; init; }
    public required PhpDocumentSurface Surface { get; init; }
    public required string Digest { get; init; }
    public required long Size { get; init; }
    public required SemanticMediaType MediaType { get; init; }
    public required string StoreUri { get; init; }
}
