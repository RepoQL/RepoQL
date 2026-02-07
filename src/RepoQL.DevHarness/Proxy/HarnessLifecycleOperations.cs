using System.Diagnostics;
using RepoQL.Contracts;

namespace RepoQL.DevHarness.Proxy;

/// <summary>
/// Purpose: Execute build, restart, and deploy flows for the harness lifecycle tools.
/// Complexity: Coordinates Aspire commands with local publish/copy operations.
/// </summary>
internal sealed class HarnessLifecycleOperations : IHarnessLifecycleOperations
{
    private const string HostResourceName = "host";
    private const string BuildCommandName = "rebuild_and_restart";
    private const string RestartCommandName = "resource-restart";
    private const string DebugConfiguration = "Debug";
    private const string PublishRuntime = "win-x64";
    private const string DeployPathEnvVar = "REPOQL_DEPLOY_PATH";
    private static readonly TimeSpan HostReadyTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan HostReadyPollInterval = TimeSpan.FromMilliseconds(500);

    private readonly IAspireTelemetryClient _aspireClient;
    private readonly IHostStateProvider _hostStateProvider;

    public HarnessLifecycleOperations(IAspireTelemetryClient aspireClient, IHostStateProvider hostStateProvider)
    {
        _aspireClient = aspireClient;
        _hostStateProvider = hostStateProvider;
    }

    public async Task<HarnessLifecycleResult> BuildAsync(CancellationToken cancellationToken)
    {
        var result = await _aspireClient.ExecuteResourceCommandAsync(HostResourceName, BuildCommandName, cancellationToken)
            .ConfigureAwait(false);

        if (result.Success)
            return HarnessLifecycleResult.Succeeded(result.Message ?? "Build and restart completed successfully.");

        // Surface the Aspire error message as output so the agent can see build/compiler errors.
        return HarnessLifecycleResult.Failed(result.Error ?? "Build failed.", result.Error);
    }

    public async Task<HarnessLifecycleResult> RestartAsync(CancellationToken cancellationToken)
    {
        var result = await _aspireClient.ExecuteResourceCommandAsync(HostResourceName, RestartCommandName, cancellationToken)
            .ConfigureAwait(false);
        if (!result.Success)
            return HarnessLifecycleResult.Failed(result.Error ?? "Failed to restart host.");

        if (!await WaitForHostReadyAsync(cancellationToken).ConfigureAwait(false))
            return HarnessLifecycleResult.Failed("Host did not reach ready state before timeout.");

        return HarnessLifecycleResult.Succeeded("Host restarted successfully.");
    }

    public async Task<HarnessLifecycleResult> DeployAsync(CancellationToken cancellationToken)
    {
        var publishResult = await PublishAsync(cancellationToken).ConfigureAwait(false);
        if (!publishResult.Success)
            return HarnessLifecycleResult.Failed(publishResult.Error ?? "Publish failed.", publishResult.Output);
        if (string.IsNullOrWhiteSpace(publishResult.PublishOutputPath))
            return HarnessLifecycleResult.Failed("Publish output path was not reported.");

        try
        {
            CopyPublishArtifacts(publishResult.PublishOutputPath, ResolveDeployPath());
        }
        catch (Exception ex)
        {
            return HarnessLifecycleResult.Failed($"Failed to copy publish artifacts: {ex.Message}");
        }

        var restartResult = await RestartAsync(cancellationToken).ConfigureAwait(false);
        return restartResult.Success
            ? HarnessLifecycleResult.Succeeded("Deploy completed successfully.")
            : restartResult;
    }

    private async Task<PublishResult> PublishAsync(CancellationToken cancellationToken)
    {
        var repoRoot = RepoLocator.FindRepoRoot();
        var projectPath = Path.Combine(repoRoot, "src", "RepoQL.ConsoleApp", "RepoQL.ConsoleApp.csproj");

        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"publish \"{projectPath}\" -c {DebugConfiguration} -r {PublishRuntime} --nologo -v q",
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
                return PublishResult.Fail("dotnet publish failed to start.");
        }
        catch (Exception ex)
        {
            return PublishResult.Fail($"dotnet publish failed to start: {ex.Message}");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            var output = CombineOutput(stdout, stderr);
            return PublishResult.Fail($"Publish failed (exit code {process.ExitCode}).", output);
        }

        return PublishResult.Ok(ResolvePublishOutputPath(repoRoot));
    }

    private static string ResolvePublishOutputPath(string repoRoot)
    {
        var configLower = DebugConfiguration.ToLowerInvariant();
        return Path.Combine(repoRoot, "artifacts", "publish", "RepoQL.ConsoleApp", $"{configLower}_{PublishRuntime}");
    }

    private static string ResolveDeployPath()
    {
        var configured = Environment.GetEnvironmentVariable(DeployPathEnvVar);
        if (!string.IsNullOrWhiteSpace(configured))
            return Path.GetFullPath(configured);

        var repoRoot = RepoLocator.FindRepoRoot();
        return Path.Combine(repoRoot, "artifacts", "publish");
    }

    private static void CopyPublishArtifacts(string sourceDirectory, string destinationDirectory)
    {
        if (!Directory.Exists(sourceDirectory))
            throw new DirectoryNotFoundException($"Publish output directory not found: {sourceDirectory}");

        Directory.CreateDirectory(destinationDirectory);

        foreach (var directory in Directory.EnumerateDirectories(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDirectory, directory);
            Directory.CreateDirectory(Path.Combine(destinationDirectory, relative));
        }

        foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDirectory, file);
            var target = Path.Combine(destinationDirectory, relative);
            var targetDir = Path.GetDirectoryName(target);
            if (!string.IsNullOrWhiteSpace(targetDir))
                Directory.CreateDirectory(targetDir);
            File.Copy(file, target, overwrite: true);
        }
    }

    private async Task<bool> WaitForHostReadyAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + HostReadyTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var snapshot = _hostStateProvider.GetSnapshot();
            if (string.Equals(snapshot.State, HostState.Ready, StringComparison.Ordinal))
                return true;

            await Task.Delay(HostReadyPollInterval, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    private static string? CombineOutput(string? stdout, string? stderr)
    {
        var hasStdout = !string.IsNullOrWhiteSpace(stdout);
        var hasStderr = !string.IsNullOrWhiteSpace(stderr);
        if (!hasStdout && !hasStderr) return null;
        if (!hasStderr) return stdout!.Trim();
        if (!hasStdout) return stderr!.Trim();
        return $"{stderr!.Trim()}\n{stdout!.Trim()}";
    }

    private sealed record PublishResult(bool Success, string? PublishOutputPath, string? Error, string? Output = null)
    {
        public static PublishResult Ok(string publishOutputPath)
            => new(true, publishOutputPath, null);

        public static PublishResult Fail(string error, string? output = null)
            => new(false, null, error, output);
    }
}

/// <summary>
/// Purpose: Contract for harness lifecycle operations used by the tool router.
/// Complexity: Minimal async surface to enable test doubles.
/// </summary>
internal interface IHarnessLifecycleOperations
{
    Task<HarnessLifecycleResult> BuildAsync(CancellationToken cancellationToken);
    Task<HarnessLifecycleResult> RestartAsync(CancellationToken cancellationToken);
    Task<HarnessLifecycleResult> DeployAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Purpose: Normalize build/restart/deploy results for tool payloads.
/// Complexity: Small success/error carrier for JSON serialization.
/// </summary>
internal sealed record HarnessLifecycleResult(bool Success, string? Message, string? Error, string? Output = null)
{
    public static HarnessLifecycleResult Succeeded(string message)
        => new(true, message, null);

    public static HarnessLifecycleResult Failed(string error, string? output = null)
        => new(false, null, error, output);
}
