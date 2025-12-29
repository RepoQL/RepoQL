using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RepoQL.Data.DuckDB;

namespace RepoQL.Mcp.Client;

/// <summary>
/// Background service that connects to MCP servers asynchronously at startup
/// and registers SQL macros for discovered tools.
/// </summary>
public sealed class McpHostedService : IHostedService
{
    private readonly McpClientRegistry _registry;
    private readonly DuckDbDataStore _store;
    private readonly ILogger _logger;

    public McpHostedService(
        McpClientRegistry registry,
        DuckDbDataStore store,
        ILogger<McpHostedService>? logger = null)
    {
        _registry = registry;
        _store = store;
        _logger = logger ?? NullLogger<McpHostedService>.Instance;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting MCP integration...");

        try
        {
            // 1. Connect to MCP servers asynchronously (parallel)
            await _registry.StartAsync(cancellationToken).ConfigureAwait(false);

            // 2. Discover tools from connected servers
            var tools = await _registry.DiscoverToolsAsync(cancellationToken).ConfigureAwait(false);

            if (tools.Count == 0)
            {
                _logger.LogInformation("No MCP tools discovered");
                return;
            }

            // 3. Generate and execute macros (McpClientRegistry implements IMcpToolCaller for UDF integration)
            var sql = McpMacroGenerator.GenerateMacros(tools);
            _store.ExecuteRaw(sql);

            _logger.LogInformation("MCP integration complete. Registered {ToolCount} tool macros from {ServerCount} servers",
                tools.Count,
                tools.Select(t => t.ServerName).Distinct().Count());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MCP integration failed - tools will not be available via SQL");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        // Cleanup is handled by McpClientRegistry.DisposeAsync
        return Task.CompletedTask;
    }
}
