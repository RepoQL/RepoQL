using System.Diagnostics;
using System.Text;
using RepoQL.Commands;
using RepoQL.ConsoleApp.Helpers;
using RepoQL.Protocol;

namespace RepoQL.ConsoleApp.CommandImplementations;

/// <summary>
/// Purpose: Restart the gRPC host without changing the active repository.
/// Complexity: Sends ShutdownHost RPC, waits for exit with kill fallback, reconnects
/// until serving, returns startup logs.
/// </summary>
[CommandClass]
internal sealed class HostRestartCommand(RepoQlClientProvider clientProvider)
{
    private static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(5);

    [Command("host.restart", Description = "Restart the repository host")]
    public async Task<CommandResult> Execute(CancellationToken cancel)
    {
        try
        {
            // Ask the host to shut down gracefully
            var client = await clientProvider.GetClientAsync(cancel).ConfigureAwait(false);
            var pid = await client.ShutdownHostAsync(cancel).ConfigureAwait(false);

            // Drop connection before waiting — host delays 500ms before stopping
            await clientProvider.DisposeAsync().ConfigureAwait(false);

            // Wait for exit, kill if it doesn't cooperate
            var killed = false;
            try
            {
                using var process = Process.GetProcessById(pid);
                if (!process.WaitForExit(ExitTimeout))
                {
                    process.Kill(entireProcessTree: true);
                    killed = true;
                }
            }
            catch (ArgumentException) { /* already exited */ }
            catch (InvalidOperationException) { /* already exited */ }

            // Reconnect — GetClientAsync waits for health check (host serving)
            var sw = Stopwatch.StartNew();
            await clientProvider.GetClientAsync(cancel).ConfigureAwait(false);
            sw.Stop();

            // Collect startup logs
            var host = RepoQlConnectionClient.GetHostDiagnostics();
            var method = killed ? "killed" : "stopped";
            var sb = new StringBuilder();
            sb.AppendLine($"Host restarted in {sw.Elapsed.TotalSeconds:F1}s (previous PID {pid} {method}, new PID {host.ProcessId}).");

            if (host.StderrTail.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Startup logs:");
                foreach (var line in host.StderrTail)
                    sb.AppendLine(line);
            }

            return CommandResult.Success(sb.ToString().TrimEnd());
        }
        catch (Exception ex)
        {
            return CommandResult.Error($"Failed to restart host: {ex.Message}");
        }
    }
}
