using RepoQL.Contracts;
namespace RepoQL.Formats.Mermaid;

internal sealed class MermaidDocumentState
{
    public required Guid DocumentId { get; init; }
    public required MDocument Ast { get; init; }
    public required string Digest { get; init; }
    public required long Size { get; init; }
    public required SemanticMediaType MediaType { get; init; }
    public required string StoreUri { get; init; }
}
