namespace RepoQL.Formats.GraphQL;

internal sealed record GraphQLTypeInfo(
    Guid NodeId,
    Guid SpanId,
    GraphQLTypeKind Kind,
    string Name,
    IReadOnlyList<string> Implements,
    IReadOnlyList<GraphQLFieldInfo> Fields,
    IReadOnlyList<GraphQLEnumValueInfo> EnumValues,
    IReadOnlyList<string> UnionMembers,
    GraphQLSpan Span,
    bool HasDescription);