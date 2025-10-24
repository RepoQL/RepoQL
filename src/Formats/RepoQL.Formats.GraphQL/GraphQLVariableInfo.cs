namespace RepoQL.Formats.GraphQL;

internal sealed record GraphQLVariableInfo(
    string Name,
    string Type,
    bool IsNonNull,
    bool HasDefaultValue,
    GraphQLSpan Span);