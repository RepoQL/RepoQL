using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RepoQL.Data.DuckDB;

namespace RepoQL.Mcp.Client;

/// <summary>
/// Purpose: Connect to MCP servers and register SQL macros for discovered tools.
/// Complexity: Runs initialization in the background so it doesn't block host startup.
/// Cancellation is owned via a dedicated CTS; StopAsync awaits completion.
/// MCP is purely additive — failures are logged but never mark RepoQL as degraded.
/// </summary>
public sealed class McpHostedService : IHostedService
{
    private readonly McpClientRegistry _registry;
    private readonly DuckDbDataStore _store;
    private readonly ILogger _logger;
    private CancellationTokenSource? _cts;
    private Task? _initTask;

    public McpHostedService(
        McpClientRegistry registry,
        DuckDbDataStore store,
        ILogger<McpHostedService>? logger = null)
    {
        _registry = registry;
        _store = store;
        _logger = logger ?? NullLogger<McpHostedService>.Instance;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("MCP integration starting in background...");
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _initTask = InitializeAsync(_cts.Token);
        return Task.CompletedTask;
    }

    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        try
        {
            // 1. Connect to MCP servers in parallel
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("MCP integration cancelled during shutdown");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MCP integration failed — external tools will not be available via SQL");
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _cts?.Cancel();
        if (_initTask is not null)
        {
            // Respect the host's shutdown timeout — don't hang if init is stuck
            try
            {
                await _initTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected — either init was cancelled or shutdown timed out.
                // Don't dispose the CTS yet — init may still be running.
                // It will complete shortly once cancellation propagates, and GC handles the rest.
                return;
            }
        }
        _cts?.Dispose();
    }
}
