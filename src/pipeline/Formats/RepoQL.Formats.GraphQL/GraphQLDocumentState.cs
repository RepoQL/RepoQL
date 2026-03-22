using RepoQL.Contracts;

namespace RepoQL.Formats.GraphQL;

internal sealed class GraphQLDocumentState
{
    public required Guid DocumentId { get; init; }
    public required string Digest { get; init; }
    public required long Size { get; init; }
    public required SemanticMediaType MediaType { get; init; }
    public required string StoreUri { get; init; }
    public required IReadOnlyList<GraphQLOperationInfo> Operations { get; init; }
    public required IReadOnlyList<GraphQLFragmentInfo> Fragments { get; init; }
    public required IReadOnlyList<GraphQLTypeInfo> Types { get; init; }
    public required IReadOnlyList<GraphQLDirectiveInfo> Directives { get; init; }
    public required GraphQLCounts Counts { get; init; }
    public bool HasSchemaDefinition { get; init; }
}