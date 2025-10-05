using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Server;
using RepoQL.Contracts;
using RepoQL.McpServer.Tools;

// Build configuration (env first, optional appsettings.json)
var configuration = new ConfigurationBuilder()
    .SetBasePath(Directory.GetCurrentDirectory())
    .AddEnvironmentVariables()
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
    .Build();

// Create RepoQL client instance
var repoPath = RepoLocator.FindRepoRoot(args.Length == 1 ? args[0] : null);
var socket = ResolveSocketPath(repoPath);
var repoQlClient = RepoQlClient.Create(new RepoQlClientOptions
{
    RepositoryPath = repoPath,
    SocketPath = socket
});

// Host + MCP server (stdio transport, mirroring existing pattern)
var builder = Host.CreateEmptyApplicationBuilder(settings: null);
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport();

// Make RepoQL client available via DI
builder.Services.AddSingleton<IRepoQlClient>(repoQlClient);

// Register MCP-native tools (no Semantic Kernel)
builder.Services.AddSingleton<McpServerTool>(sp => RepoQlMcpTools.CreateQueryTool(sp.GetRequiredService<IRepoQlClient>()));
builder.Services.AddSingleton<McpServerTool>(sp => RepoQlMcpTools.CreateSqlTool(sp.GetRequiredService<IRepoQlClient>()));
builder.Services.AddSingleton<McpServerTool>(sp => RepoQlMcpTools.CreateXRayTool(sp.GetRequiredService<IRepoQlClient>()));

await builder.Build().RunAsync();

static string? ResolveSocketPath(string repoPath)
{
    // 1) Environment override
    var env = Environment.GetEnvironmentVariable("REPOQL_SOCKET");
    if (!string.IsNullOrWhiteSpace(env)) return env;

    // 2) WSL mapping file (repo/.repoql/socket.path)
    var mapFile = Path.Combine(repoPath, ".repoql", "socket.path");
    if (File.Exists(mapFile))
    {
        try
        {
            var p = (File.ReadAllText(mapFile) ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(p)) return p;
        }
        catch
        {
            // ignore and fallback
        }
    }

    // 3) Default local socket path (repo/.repoql/repoql.sock)
    return Path.Combine(repoPath, ".repoql", "repoql.sock");
}
