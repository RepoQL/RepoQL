namespace RepoQL.Formats.GraphQL;

internal readonly record struct GraphQLSpan(int Start, int End)
{
    public int Length => Math.Max(0, End - Start);
}