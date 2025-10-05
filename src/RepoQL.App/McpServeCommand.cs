using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Server;
using RepoQL.Contracts;
using Spectre.Console.Cli;

internal sealed class McpServeCommand : AsyncCommand<McpServeSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, McpServeSettings settings)
    {
        var repo = ProgramHelpers.ResolveRepo(settings.Repo);
        var socket = ProgramHelpers.ResolveSocketPath(repo);
        var client = RepoQlClient.Create(new RepoQlClientOptions { RepositoryPath = repo, SocketPath = socket });

        var builder = Host.CreateEmptyApplicationBuilder(settings: null);
        builder.Services.AddMcpServer().WithStdioServerTransport();
        builder.Services.AddSingleton<IRepoQlClient>(client);
        // Inline tools (copy of RepoQlMcpTools to avoid access restrictions)
        builder.Services.AddSingleton<McpServerTool>(sp => McpToolFactory.CreateQueryTool(sp.GetRequiredService<IRepoQlClient>()));
        builder.Services.AddSingleton<McpServerTool>(sp => McpToolFactory.CreateSqlTool(sp.GetRequiredService<IRepoQlClient>()));
        builder.Services.AddSingleton<McpServerTool>(sp => McpToolFactory.CreateXRayTool(sp.GetRequiredService<IRepoQlClient>()));
        await builder.Build().RunAsync();
        return 0;
    }
}