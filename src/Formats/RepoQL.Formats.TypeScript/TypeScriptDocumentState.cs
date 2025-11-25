using RepoQL.Contracts;
using RepoQL.Contracts.Models;

namespace RepoQL.Formats.TypeScript;

internal sealed class TypeScriptDocumentState
{
    public required Guid DocumentId { get; init; }

    public required Guid ArtifactId { get; init; }

    public required string Digest { get; init; }

    public required long Size { get; init; }

    public required SemanticMediaType MediaType { get; init; }

    public required string StoreUri { get; init; }

    public required TypeScriptParseResult Parse { get; init; }

    public required TextLineMap LineMap { get; init; }
}
