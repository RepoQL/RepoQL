using RepoQL.Contracts;

namespace RepoQL.Formats.GraphQL;

internal static class GraphQLMediaTypes
{
    public static readonly SemanticMediaType GraphQL =
        SemanticMediaType.Create("text", "graphql").WithKind("graphql.doc");

    public static bool TryResolve(string extension, out SemanticMediaType? mediaType)
    {
        mediaType = extension switch
        {
            ".graphql" => GraphQL,
            ".graphqls" => GraphQL,
            ".gql" => GraphQL,
            ".gqls" => GraphQL,
            _ => null
        };

        return mediaType is not null;
    }
}
