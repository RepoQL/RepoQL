namespace RepoQL.Formats.GraphQL;

internal sealed record GraphQLDirectiveInfo(
    Guid NodeId,
    Guid SpanId,
    string Name,
    bool IsRepeatable,
    IReadOnlyList<string> Locations,
    int ArgumentCount,
    GraphQLSpan Span,
    bool HasDescription);