using Microsoft.Extensions.DependencyInjection;
using RepoQL.Contracts;
using RepoQL.Indexing.Hosting;

namespace RepoQL.Formats.Xlsx;

/// <summary>
/// DI registration extensions for XLSX format support.
///
/// Purpose: Registers XlsxLoader and XlsxParser with the service container,
/// enabling XLSX file indexing in the pipeline.
///
/// Complexity: None - standard DI registration following established patterns.
/// </summary>
public static class XlsxServiceCollectionExtensions
{
    /// <summary>
    /// Adds XLSX format support to the indexing pipeline.
    /// </summary>
    public static IServiceCollection AddXlsxFormat(this IServiceCollection services)
    {
        services.AddSingleton<XlsxLoader>();
        services.AddSingleton<IFormatSchemaProvider>(sp => sp.GetRequiredService<XlsxLoader>());

        services.AddSingleton<FormatDescriptor>(sp =>
        {
            var loader = sp.GetRequiredService<XlsxLoader>();
            return new FormatDescriptor(
                SemanticMediaType.Create("application", "xlsx")
                    .WithKind("xlsx.workbook"),
                loader,
                analyzer: null,
                loader,
                new[] { "xlsx" });
        });

        services.AddIndexingProcessor<XlsxClassifier>();
        services.AddIndexingProcessor<XlsxParser>();

        return services;
    }
}
