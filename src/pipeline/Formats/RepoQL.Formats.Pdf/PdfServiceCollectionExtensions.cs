using Microsoft.Extensions.DependencyInjection;
using RepoQL.Contracts;
using RepoQL.Formats.Pdf.TextExtraction;
using RepoQL.Indexing.Hosting;

namespace RepoQL.Formats.Pdf;

/// <summary>
/// DI registration extensions for PDF format support.
///
/// Purpose: Registers loader and indexing processors for PDF documents.
///
/// Complexity: Standard registration only.
/// </summary>
public static class PdfServiceCollectionExtensions
{
    public static IServiceCollection AddPdfFormat(this IServiceCollection services)
    {
        services.AddSingleton<PdfLoader>();
        services.AddSingleton<IFormatSchemaProvider>(sp => sp.GetRequiredService<PdfLoader>());
        services.AddSingleton<PdfTextExtractor>();

        services.AddSingleton<FormatDescriptor>(sp =>
        {
            var loader = sp.GetRequiredService<PdfLoader>();
            return new FormatDescriptor(
                PdfMediaTypes.Document,
                loader,
                analyzer: null!,
                loader,
                new[] { "pdf" });
        });

        services.AddIndexingProcessor<PdfClassifier>();
        services.AddIndexingProcessor<PdfParser>();

        return services;
    }
}
