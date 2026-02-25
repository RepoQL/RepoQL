using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RepoQL.Contracts;
using RepoQL.Formats.Cpp.Analysis;
using RepoQL.Formats.Cpp.TreeSitter;
using RepoQL.Indexing.Hosting;

namespace RepoQL.Formats.Cpp;

public static class CppServiceCollectionExtensions
{
    private static readonly string[] CLabels = ["c"];
    private static readonly string[] CppLabels = ["cpp", "cc", "cxx"];
    private static readonly string[] CppHeaderLabels = ["h", "hpp", "hh", "hxx"];
    private static readonly string[] CppInlineLabels = ["ipp", "tpp", "inl"];

    public static IServiceCollection AddCppFormat(this IServiceCollection services)
    {
        services.AddSingleton<CppTreeSitterClient>(sp => new CppTreeSitterClient(
            logger: sp.GetService<ILogger<CppTreeSitterClient>>()));
        services.AddSingleton<MacroInterferenceDetector>();
        services.AddSingleton<CppXRayGenerator>();
        services.AddSingleton<CppMaterializer>(sp => new CppMaterializer(
            client: sp.GetRequiredService<CppTreeSitterClient>(),
            xrayGenerator: sp.GetRequiredService<CppXRayGenerator>(),
            macroInterferenceDetector: sp.GetRequiredService<MacroInterferenceDetector>(),
            logger: sp.GetService<ILogger<CppMaterializer>>()));
        services.AddSingleton<CppAnalyzer>();
        services.AddSingleton<CppSingleFileAnalyzer>();

        services.AddSingleton<CppSchemaProvider>(_ => new CppSchemaProvider(enableViews: true));
        services.AddSingleton<IFormatSchemaProvider>(sp => sp.GetRequiredService<CppSchemaProvider>());

        AddDescriptor(services, CppMediaTypes.C, CLabels);
        AddDescriptor(services, CppMediaTypes.Cpp, CppLabels);
        AddDescriptor(services, CppMediaTypes.CppHeader, CppHeaderLabels);
        AddDescriptor(services, CppMediaTypes.CppInline, CppInlineLabels);

        services.AddIndexingProcessor<CppClassifier>();
        services.AddIndexingProcessor<CppParser>();
        services.AddIndexingProcessor<CppSingleFileAnalyzer>();

        return services;
    }

    private static void AddDescriptor(IServiceCollection services, SemanticMediaType mediaType, IReadOnlyCollection<string> labels)
    {
        services.AddSingleton<FormatDescriptor>(sp =>
        {
            var materializer = sp.GetRequiredService<CppMaterializer>();
            var analyzer = sp.GetRequiredService<CppAnalyzer>();
            return new FormatDescriptor(
                mediaType,
                materializer,
                analyzer,
                materializer,
                labels);
        });
    }
}
