using System.Diagnostics;
using RepoQL.Contracts;

namespace RepoQL.DevHarness.Proxy;

/// <summary>
/// Purpose: Start the Aspire orchestrator if it isn't already running.
/// Complexity: Probes the dashboard endpoint, launches dotnet run if unreachable, waits for ready.
/// </summary>
internal static class OrchestratorLauncher
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    public static async Task<Process?> EnsureRunningAsync(Uri aspireEndpoint, CancellationToken cancellationToken)
    {
        if (await IsReachableAsync(aspireEndpoint, cancellationToken).ConfigureAwait(false))
        {
            await Console.Error.WriteLineAsync("[HARNESS] Orchestrator already running.").ConfigureAwait(false);
            return null;
        }

        await Console.Error.WriteLineAsync("[HARNESS] Orchestrator not running. Starting...").ConfigureAwait(false);

        var repoRoot = RepoLocator.FindRepoRoot();
        var projectPath = Path.Combine(repoRoot, "src", "RepoQL.Orchestrator", "RepoQL.Orchestrator.csproj");

        if (!File.Exists(projectPath))
        {
            await Console.Error.WriteLineAsync($"[HARNESS] Orchestrator project not found: {projectPath}").ConfigureAwait(false);
            return null;
        }

        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"run --project \"{projectPath}\" --launch-profile http",
                WorkingDirectory = repoRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        try
        {
            if (!process.Start())
            {
                await Console.Error.WriteLineAsync("[HARNESS] Failed to start orchestrator process.").ConfigureAwait(false);
                return null;
            }
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"[HARNESS] Failed to start orchestrator: {ex.Message}").ConfigureAwait(false);
            return null;
        }

        // Drain stdout/stderr to prevent buffer deadlocks.
        _ = DrainAsync(process.StandardOutput);
        _ = DrainAsync(process.StandardError);

        await Console.Error.WriteLineAsync($"[HARNESS] Orchestrator starting (PID {process.Id}). Waiting for dashboard...").ConfigureAwait(false);

        var baseUri = new Uri($"{aspireEndpoint.Scheme}://{aspireEndpoint.Authority}");
        if (await WaitForReadyAsync(baseUri, StartupTimeout, cancellationToken).ConfigureAwait(false))
        {
            await Console.Error.WriteLineAsync("[HARNESS] Orchestrator ready.").ConfigureAwait(false);
            return process;
        }

        await Console.Error.WriteLineAsync("[HARNESS] Orchestrator did not become ready in time. Continuing anyway.").ConfigureAwait(false);
        return process;
    }

    private static async Task<bool> IsReachableAsync(Uri endpoint, CancellationToken cancellationToken)
    {
        try
        {
            using var client = new HttpClient { Timeout = ProbeTimeout };
            var baseUri = new Uri($"{endpoint.Scheme}://{endpoint.Authority}");
            var response = await client.GetAsync(baseUri, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> WaitForReadyAsync(Uri baseUri, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        using var client = new HttpClient { Timeout = ProbeTimeout };

        while (Stopwatch.GetTimestamp() < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var response = await client.GetAsync(baseUri, cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                    return true;
            }
            catch
            {
                // Not ready yet.
            }

            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    private static async Task DrainAsync(StreamReader reader)
    {
        try { while (await reader.ReadLineAsync().ConfigureAwait(false) is not null) { } }
        catch { /* process exited */ }
    }
}
