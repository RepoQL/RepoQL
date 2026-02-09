using Microsoft.Extensions.DependencyInjection;
using RepoQL.Contracts;
using RepoQL.Indexing.Hosting;

namespace RepoQL.Formats.Docx;

/// <summary>
/// DI registration extensions for DOCX format support.
///
/// Purpose: Registers loader and indexing processors for Word documents.
///
/// Complexity: Standard registration only.
/// </summary>
public static class DocxServiceCollectionExtensions
{
    public static IServiceCollection AddDocxFormat(this IServiceCollection services)
    {
        services.AddSingleton<DocxLoader>();

        services.AddSingleton<FormatDescriptor>(sp =>
        {
            var loader = sp.GetRequiredService<DocxLoader>();
            return new FormatDescriptor(
                DocxMediaTypes.Document,
                loader,
                analyzer: null!,
                loader,
                new[] { "docx", "docm", "dotx" });
        });

        services.AddIndexingProcessor<DocxClassifier>();
        services.AddIndexingProcessor<DocxParser>();

        return services;
    }
}
