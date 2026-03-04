using RepoQL.Commands;
using RepoQL.ConsoleApp.Diagnostics;

namespace RepoQL.ConsoleApp.CommandImplementations;

/// <summary>
/// Purpose: Expose system health diagnostics as ::diagnostics and ::diagnostics.fast commands.
/// Complexity: Thin wrapper over SelfTestRunner. Two commands — full and fast — no parameters needed.
/// </summary>
[CommandClass]
internal sealed class DiagnosticsCommand(SelfTestRunner runner)
{
    [Command("diagnostics", Description = "Run full system health diagnostics")]
    public async Task<CommandResult> Execute(CancellationToken cancel)
    {
        return CommandResult.Success(await runner.RunAsync(DiagnosticCollectionMode.Full, cancel));
    }

    [Command("diagnostics.fast", Description = "Run quick system health checks")]
    public async Task<CommandResult> ExecuteFast(CancellationToken cancel)
    {
        return CommandResult.Success(await runner.RunAsync(DiagnosticCollectionMode.Fast, cancel));
    }
}
