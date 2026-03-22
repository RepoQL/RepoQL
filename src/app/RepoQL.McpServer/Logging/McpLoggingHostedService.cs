using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace RepoQL.McpServer.Logging;

/// <summary>
/// Forwards <see cref="ILogger"/> events to connected MCP clients by
/// registering the MCP-provided logger provider alongside the existing sinks.
/// </summary>
internal sealed class McpLoggingHostedService(ModelContextProtocol.Server.McpServer server, ILoggerFactory loggerFactory) : IHostedService, IDisposable
{
    private readonly ModelContextProtocol.Server.McpServer _server = server ?? throw new ArgumentNullException(nameof(server));
    private readonly ILoggerFactory _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
    private ILoggerProvider? _provider;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _provider = _server.AsClientLoggerProvider();
        _loggerFactory.AddProvider(_provider);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        DisposeProvider();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        DisposeProvider();
    }

    private void DisposeProvider()
    {
        var provider = Interlocked.Exchange(ref _provider, null);
        provider?.Dispose();
    }
}
