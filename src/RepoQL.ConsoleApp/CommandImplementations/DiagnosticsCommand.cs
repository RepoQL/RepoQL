using RepoQL.Commands;
using RepoQL.ConsoleApp.Diagnostics;

namespace RepoQL.ConsoleApp.CommandImplementations;

/// <summary>
/// Purpose: Expose system health diagnostics as a ::diagnostics command.
/// Complexity: Thin wrapper over SelfTestRunner, mapping depth parameter to collection mode.
/// </summary>
[CommandClass]
internal sealed class DiagnosticsCommand(SelfTestRunner runner)
{
    [Command("diagnostics", Description = "Run system health diagnostics")]
    public async Task<CommandResult> Execute(
        [CommandParam("'fast' for quick checks, omit for full")] string? depth,
        CancellationToken cancel)
    {
        var mode = string.Equals(depth, "fast", StringComparison.OrdinalIgnoreCase)
            ? DiagnosticCollectionMode.Fast
            : DiagnosticCollectionMode.Full;

        return CommandResult.Success(await runner.RunAsync(mode, cancel));
    }
}
