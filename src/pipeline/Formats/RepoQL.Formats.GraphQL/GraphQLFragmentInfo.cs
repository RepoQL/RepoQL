namespace RepoQL.Formats.GraphQL;

internal sealed record GraphQLFragmentInfo(
    Guid NodeId,
    Guid SpanId,
    string Name,
    string? TypeCondition,
    IReadOnlyList<GraphQLFragmentUsage> FragmentUsages,
    int DirectiveCount,
    GraphQLSpan Span);