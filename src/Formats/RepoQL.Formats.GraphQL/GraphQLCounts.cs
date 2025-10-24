namespace RepoQL.Formats.GraphQL;

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