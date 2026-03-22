using Microsoft.Extensions.DependencyInjection;
using RepoQL.Contracts;
using RepoQL.Indexing.Hosting;

namespace RepoQL.Formats.Csv;

/// <summary>
/// DI registration extensions for CSV/TSV/PSV format support.
///
/// Purpose: Registers CsvLoader and pipeline processors so delimited text files
/// participate in classification, parsing, and schema macro registration.
///
/// Complexity: None. Standard service registration wiring only.
/// </summary>
public static class CsvServiceCollectionExtensions
{
    /// <summary>
    /// Adds CSV/TSV/PSV format support to the indexing pipeline.
    /// </summary>
    public static IServiceCollection AddCsvFormat(this IServiceCollection services)
    {
        services.AddSingleton<CsvLoader>();
        services.AddSingleton<IFormatSchemaProvider>(sp => sp.GetRequiredService<CsvLoader>());
        services.AddSingleton<FormatDescriptor>(sp =>
        {
            var loader = sp.GetRequiredService<CsvLoader>();
            return new FormatDescriptor(
                SemanticMediaType.Create("text", "csv").WithKind("csv.table"),
                loader,
                analyzer: null,
                loader,
                new[] { "csv", "tsv", "psv" });
        });

        services.AddIndexingProcessor<CsvClassifier>();
        services.AddIndexingProcessor<CsvParser>();

        return services;
    }
}
