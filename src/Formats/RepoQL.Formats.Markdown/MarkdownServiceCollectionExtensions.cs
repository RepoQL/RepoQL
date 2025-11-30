using Microsoft.Extensions.DependencyInjection;
using RepoQL.Contracts;
using RepoQL.Indexing.Hosting;

namespace RepoQL.Formats.Markdown;

public static class MarkdownServiceCollectionExtensions
{
    public static IServiceCollection AddMarkdownFormat(this IServiceCollection services)
    {
        services.AddSingleton<MarkdownLoader>();
        services.AddSingleton<MarkdownAnalyzer>();

        services.AddSingleton<FormatDescriptor>(sp =>
        {
            var loader = sp.GetRequiredService<MarkdownLoader>();
            var analyzer = sp.GetRequiredService<MarkdownAnalyzer>();
            return new FormatDescriptor(
                SemanticMediaType.Create("text", "markdown").WithKind("markdown.doc"),
                loader,
                analyzer,
                loader,
                new[] { "markdown" });
        });

        services.AddIndexingProcessor<MarkdownClassifier>();

        return services;
    }
}
