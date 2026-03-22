using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RepoQL.Contracts;
using RepoQL.Indexing.Hosting;

namespace RepoQL.Formats.Ruby;

public static class RubyServiceCollectionExtensions
{
    public static IServiceCollection AddRubyFormat(this IServiceCollection services)
    {
        services.AddSingleton<RubyLoader>(sp => new RubyLoader(
            logger: sp.GetService<ILogger<RubyLoader>>()));
        services.AddSingleton<IFormatSchemaProvider>(sp => sp.GetRequiredService<RubyLoader>());

        services.AddSingleton<FormatDescriptor>(sp =>
        {
            var loader = sp.GetRequiredService<RubyLoader>();
            return new FormatDescriptor(
                RubyMediaTypes.Ruby,
                loader,
                analyzer: null!,
                loader,
                new[] { "rb", "rake", "gemspec", "Gemfile", "Rakefile", "Guardfile", "Dangerfile" });
        });

        services.AddIndexingProcessor<RubyClassifier>();
        services.AddIndexingProcessor<RubyParser>();

        return services;
    }
}
