using ConsoleAppFramework;
using RepoQL.DevHarness.Proxy;

namespace RepoQL.DevHarness.Commands;

/// <summary>
/// Purpose: Exposes the `harness` command that launches the stdio proxy.
/// Complexity: Minimal command wiring that delegates all runtime work to the proxy.
/// </summary>
[RegisterCommands]
internal sealed class HarnessCommands
{
    public async Task Harness(CancellationToken cancel = default)
    {
        var proxy = new McpStdioProxy(RepoqlSubprocessOptions.CreateDefault());
        var exitCode = await proxy.RunAsync(cancel);
        Environment.ExitCode = exitCode;
    }
}
