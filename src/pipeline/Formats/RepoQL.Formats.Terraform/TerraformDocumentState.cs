using RepoQL.Contracts;

namespace RepoQL.Formats.Terraform;

public sealed class TerraformDocumentState
{
    public required Guid DocumentId { get; init; }
    public required TerraformParseResult ParseResult { get; init; }
    public required string Digest { get; init; }
    public required long Size { get; init; }
    public required SemanticMediaType MediaType { get; init; }
    public required string StoreUri { get; init; }
}
