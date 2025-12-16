using Microsoft.Extensions.DependencyInjection;
using RepoQL.Contracts;
using RepoQL.Indexing.Hosting;

namespace RepoQL.Formats.GraphQL;

public static class GraphQLServiceCollectionExtensions
{
    public static IServiceCollection AddGraphQLFormat(this IServiceCollection services)
    {
        services.AddSingleton<GraphQLLoader>();
        services.AddSingleton<GraphQLAnalyzer>();

        services.AddSingleton<FormatDescriptor>(sp =>
        {
            var loader = sp.GetRequiredService<GraphQLLoader>();
            var analyzer = sp.GetRequiredService<GraphQLAnalyzer>();
            return new FormatDescriptor(
                GraphQLMediaTypes.GraphQL,
                loader,
                analyzer,
                loader,
                ["graphql", "graphqls", "gql", "gqls"]);
        });

        services.AddIndexingProcessor<GraphQLClassifier>();
        services.AddIndexingProcessor<GraphQLDocumentParser>();

        return services;
    }
}
