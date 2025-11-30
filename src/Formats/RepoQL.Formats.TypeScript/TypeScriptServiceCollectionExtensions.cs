using Microsoft.Extensions.DependencyInjection;
using RepoQL.Contracts;
using RepoQL.Indexing.Hosting;

namespace RepoQL.Formats.TypeScript;

public static class TypeScriptServiceCollectionExtensions
{
    public static IServiceCollection AddTypeScriptFormat(this IServiceCollection services)
    {
        services.AddSingleton<TypeScriptNodeClient>();
        services.AddSingleton<TypeScriptLoader>();
        services.AddSingleton<IFormatSchemaProvider>(sp => sp.GetRequiredService<TypeScriptLoader>());
        services.AddSingleton<TypeScriptAnalyzer>();

        services.AddSingleton<FormatDescriptor>(sp =>
        {
            var loader = sp.GetRequiredService<TypeScriptLoader>();
            var analyzer = sp.GetRequiredService<TypeScriptAnalyzer>();
            return new FormatDescriptor(
                SemanticMediaType.Create("text", "x-typescript").WithKind("code.typescript"),
                loader,
                analyzer,
                loader,
                new[] { "ts", "tsx", "js", "jsx" });
        });

        services.AddSingleton<FormatDescriptor>(sp =>
        {
            var loader = sp.GetRequiredService<TypeScriptLoader>();
            var analyzer = sp.GetRequiredService<TypeScriptAnalyzer>();
            return new FormatDescriptor(
                SemanticMediaType.Create("text", "x-typescript").WithKind("code.typescript.react"),
                loader,
                analyzer,
                loader,
                new[] { "tsx" });
        });

        services.AddSingleton<FormatDescriptor>(sp =>
        {
            var loader = sp.GetRequiredService<TypeScriptLoader>();
            var analyzer = sp.GetRequiredService<TypeScriptAnalyzer>();
            return new FormatDescriptor(
                SemanticMediaType.Create("text", "javascript").WithKind("code.javascript"),
                loader,
                analyzer,
                loader,
                new[] { "js" });
        });

        services.AddSingleton<FormatDescriptor>(sp =>
        {
            var loader = sp.GetRequiredService<TypeScriptLoader>();
            var analyzer = sp.GetRequiredService<TypeScriptAnalyzer>();
            return new FormatDescriptor(
                SemanticMediaType.Create("text", "javascript").WithKind("code.javascript.react"),
                loader,
                analyzer,
                loader,
                new[] { "jsx" });
        });

        services.AddIndexingProcessor<TypeScriptClassifier>();

        return services;
    }
}
