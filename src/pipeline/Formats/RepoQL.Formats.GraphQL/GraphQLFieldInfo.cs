namespace RepoQL.Formats.GraphQL;

internal sealed record GraphQLFieldInfo(
    Guid NodeId,
    Guid SpanId,
    string Name,
    string Type,
    int ArgumentCount,
    bool IsDeprecated,
    string? DeprecationReason,
    bool HasDescription,
    GraphQLSpan Span);