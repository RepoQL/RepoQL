namespace RepoQL.Formats.GraphQL;

internal sealed record GraphQLFragmentUsage(
    Guid UsageSpanId,
    string Name,
    GraphQLSpan Span);