namespace RepoQL.Formats.GraphQL;

internal sealed record GraphQLVariableUsage(
    Guid UsageSpanId,
    string Name,
    GraphQLSpan Span);