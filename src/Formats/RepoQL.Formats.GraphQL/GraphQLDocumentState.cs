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

internal enum GraphQLOperationKind
{
    Query,
    Mutation,
    Subscription,
    Anonymous
}

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

internal sealed record GraphQLVariableInfo(
    string Name,
    string Type,
    bool IsNonNull,
    bool HasDefaultValue,
    GraphQLSpan Span);

internal sealed record GraphQLFragmentInfo(
    Guid NodeId,
    Guid SpanId,
    string Name,
    string? TypeCondition,
    IReadOnlyList<GraphQLFragmentUsage> FragmentUsages,
    int DirectiveCount,
    GraphQLSpan Span);

internal sealed record GraphQLFragmentUsage(
    Guid UsageSpanId,
    string Name,
    GraphQLSpan Span);

internal sealed record GraphQLVariableUsage(
    Guid UsageSpanId,
    string Name,
    GraphQLSpan Span);

internal enum GraphQLTypeKind
{
    Object,
    Interface,
    InputObject,
    Enum,
    Union,
    Scalar
}

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

internal sealed record GraphQLEnumValueInfo(
    Guid NodeId,
    Guid SpanId,
    string Name,
    bool IsDeprecated,
    string? DeprecationReason,
    GraphQLSpan Span,
    bool HasDescription);

internal sealed record GraphQLDirectiveInfo(
    Guid NodeId,
    Guid SpanId,
    string Name,
    bool IsRepeatable,
    IReadOnlyList<string> Locations,
    int ArgumentCount,
    GraphQLSpan Span,
    bool HasDescription);

internal sealed record GraphQLCounts(
    int QueryCount,
    int MutationCount,
    int SubscriptionCount,
    int FragmentCount,
    int ObjectTypeCount,
    int InterfaceTypeCount,
    int InputTypeCount,
    int EnumTypeCount,
    int UnionTypeCount,
    int ScalarTypeCount,
    int DirectiveCount);

internal readonly record struct GraphQLSpan(int Start, int End)
{
    public int Length => Math.Max(0, End - Start);
}
