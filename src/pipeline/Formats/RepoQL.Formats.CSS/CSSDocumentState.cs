using RepoQL.Contracts;

namespace RepoQL.Formats.CSS;

public sealed class CSSDocumentState
{
    public required Guid DocumentId { get; init; }
    public required CSSParseResult ParseResult { get; init; }
    public required string Digest { get; init; }
    public required long Size { get; init; }
    public required SemanticMediaType MediaType { get; init; }
    public required string StoreUri { get; init; }
}
