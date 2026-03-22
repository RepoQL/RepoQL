namespace RepoQL.Formats.GraphQL;

internal sealed record GraphQLEnumValueInfo(
    Guid NodeId,
    Guid SpanId,
    string Name,
    bool IsDeprecated,
    string? DeprecationReason,
    GraphQLSpan Span,
    bool HasDescription);