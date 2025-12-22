using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RepoQL.Contracts;
using RepoQL.Indexing.Hosting;

namespace RepoQL.Formats.Terraform;

public static class TerraformServiceCollectionExtensions
{
    public static IServiceCollection AddTerraformFormat(this IServiceCollection services)
    {
        services.AddSingleton<TerraformLoader>(sp => new TerraformLoader(
            logger: sp.GetService<ILogger<TerraformLoader>>()));

        services.AddSingleton<FormatDescriptor>(sp =>
        {
            var loader = sp.GetRequiredService<TerraformLoader>();
            return new FormatDescriptor(
                TerraformMediaTypes.Terraform,
                loader,
                analyzer: null,
                loader,
                ["tf", "tfvars"]);
        });

        services.AddIndexingProcessor<TerraformParser>();

        return services;
    }
}
