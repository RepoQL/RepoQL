namespace RepoQL.Formats.GraphQL;

internal sealed record GraphQLOperationInfo(
    Guid NodeId,
    Guid SpanId,
    string? Name,
    GraphQLOperationKind Kind,
    IReadOnlyList<GraphQLVariableInfo> Variables,
    IReadOnlyList<string> TopLevelFields,
    IReadOnlyList<GraphQLFragmentUsage> FragmentUsages,
    IReadOnlyList<GraphQLVariableUsage> VariableUsages,
    int DirectiveCount,
    GraphQLSpan Span);