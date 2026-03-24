using System.Diagnostics;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
using ConsoleAppFramework;
using RepoQL.Protocol;
using Spectre.Console;

namespace RepoQL.ConsoleApp.Commands;

/// <summary>
/// Purpose: Check for and apply RepoQL binary updates from the downloads CDN.
/// Complexity: Platform-aware binary download with atomic replacement (rename-swap on Windows),
/// version normalization, progress reporting, and running-host detection.
/// </summary>
[RegisterCommands]
internal sealed class UpdateCommand(IAnsiConsole console)
{
    private const string VersionUrl = "https://downloads.repoql.ai/latest/version.txt";
    private const string DownloadBaseUrl = "https://downloads.repoql.ai/latest";

    /// <summary>
    /// Check for updates and install the latest version of RepoQL.
    /// </summary>
    /// <param name="check">Only check for updates without installing.</param>
    /// <param name="force">Force reinstall even if the current version matches.</param>
    /// <param name="cancel">Cancellation token.</param>
    [Command("update")]
    public async Task Update(bool check = false, bool force = false, CancellationToken cancel = default)
    {
        var currentVersion = GetCurrentVersion();
        console.MarkupLine($"Current version: [bold]{Markup.Escape(FormatVersion(currentVersion))}[/]");

        var latestVersionString = await FetchLatestVersionAsync(cancel).ConfigureAwait(false);
        if (latestVersionString is null)
        {
            console.MarkupLine("[red]Could not check for updates. Please check your internet connection.[/]");
            return;
        }

        if (!Version.TryParse(latestVersionString, out var latestVersion))
        {
            console.MarkupLine($"[red]Invalid version format from server: {Markup.Escape(latestVersionString)}[/]");
            return;
        }

        console.MarkupLine($"Latest version:  [bold]{Markup.Escape(FormatVersion(latestVersion))}[/]");

        var isUpToDate = currentVersion is not null && Normalize(currentVersion) >= Normalize(latestVersion);
        if (isUpToDate && !force)
        {
            console.MarkupLine("[green]RepoQL is up to date.[/]");
            return;
        }

        if (check)
        {
            if (isUpToDate)
                console.MarkupLine("[green]RepoQL is up to date.[/]");
            else
                console.MarkupLine("[yellow]A new version is available. Run 'repoql update' to install it.[/]");
            return;
        }

        var binaryPath = ResolveCurrentBinaryPath();
        if (binaryPath is null)
        {
            console.MarkupLine("[red]Could not determine the installed binary location. Please reinstall manually:[/]");
            WriteManualInstallInstructions();
            return;
        }

        try
        {
            await DownloadAndReplaceAsync(binaryPath, cancel).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            console.MarkupLine($"[red]Download failed: {Markup.Escape(ex.Message)}[/]");
            console.MarkupLine("[yellow]Your current installation is unchanged. Check your internet connection and try again.[/]");
            return;
        }
        catch (IOException ex)
        {
            console.MarkupLine($"[red]Could not replace binary: {Markup.Escape(ex.Message)}[/]");
            WriteRecoveryInstructions(binaryPath);
            return;
        }
        catch (UnauthorizedAccessException ex)
        {
            console.MarkupLine($"[red]Permission denied: {Markup.Escape(ex.Message)}[/]");
            if (OperatingSystem.IsWindows())
                console.MarkupLine("[yellow]Try running as Administrator.[/]");
            else
                console.MarkupLine("[yellow]Try running with sudo.[/]");
            return;
        }

        if (isUpToDate)
            console.MarkupLine($"[green]Reinstalled {Markup.Escape(FormatVersion(latestVersion))}.[/]");
        else
            console.MarkupLine($"[green]Updated to {Markup.Escape(FormatVersion(latestVersion))}.[/]");

        await WarnIfHostRunningAsync(cancel).ConfigureAwait(false);
    }

    /// <summary>
    /// Normalize versions to 3 components (Major.Minor.Build) so that assembly version 1.5.3.0
    /// and version.txt 1.5.3 (Revision=-1) compare correctly.
    /// </summary>
    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));

    private static string FormatVersion(Version? v)
        => v is null ? "unknown" : $"{v.Major}.{v.Minor}.{Math.Max(v.Build, 0)}";

    private static Version? GetCurrentVersion()
        => Assembly.GetEntryAssembly()?.GetName().Version;

    private static async Task<string?> FetchLatestVersionAsync(CancellationToken cancel)
    {
        using var client = CreateHttpClient();
        try
        {
            var response = await client.GetStringAsync(VersionUrl, cancel).ConfigureAwait(false);
            var trimmed = response.Trim();
            // Defensive: version.txt should be tiny. Reject anything suspiciously large.
            return trimmed.Length > 50 ? null : trimmed;
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (TaskCanceledException) when (!cancel.IsCancellationRequested)
        {
            return null;
        }
    }

    private async Task DownloadAndReplaceAsync(string binaryPath, CancellationToken cancel)
    {
        var rid = GetRuntimeIdentifier();
        var binaryName = OperatingSystem.IsWindows() ? "repoql.exe" : "repoql";
        var downloadUrl = $"{DownloadBaseUrl}/{rid}/{binaryName}";

        var tempPath = binaryPath + ".update";
        var backupPath = binaryPath + ".old";

        try
        {
            await DownloadBinaryAsync(downloadUrl, tempPath, cancel).ConfigureAwait(false);

            // Sanity check: the downloaded file should be a reasonable size for an executable.
            var downloadedSize = new FileInfo(tempPath).Length;
            if (downloadedSize < 1024 * 100) // < 100 KB is not a real binary
            {
                TryDelete(tempPath);
                throw new HttpRequestException(
                    $"Downloaded file is suspiciously small ({downloadedSize:N0} bytes). " +
                    "The binary may not be available for this platform yet.");
            }

            // Rename-swap: current → .old, new → current.
            if (File.Exists(backupPath))
                File.Delete(backupPath);

            File.Move(binaryPath, backupPath);

            try
            {
                File.Move(tempPath, binaryPath);
            }
            catch
            {
                // Rollback: restore the original binary if the final move fails.
                try
                {
                    File.Move(backupPath, binaryPath);
                }
                catch (Exception rollbackEx)
                {
                    throw new IOException(
                        $"Failed to place new binary and rollback also failed. " +
                        $"Your original binary is at '{backupPath}' — rename it back to '{Path.GetFileName(binaryPath)}' manually. " +
                        $"Rollback error: {rollbackEx.Message}");
                }

                throw;
            }

            if (!OperatingSystem.IsWindows())
                EnsureExecutablePermission(binaryPath);

            // Clean up the backup. On Windows, the old binary may still be locked
            // by other processes — ignore failures silently.
            TryDelete(backupPath);
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    private async Task DownloadBinaryAsync(string url, string destination, CancellationToken cancel)
    {
        using var client = CreateHttpClient();
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength;
        await using var sourceStream = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
        await using var fileStream = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);

        await console.Progress()
            .AutoClear(true)
            .Columns(
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new TransferSpeedColumn(),
                new RemainingTimeColumn())
            .StartAsync(async ctx =>
            {
                var task = ctx.AddTask("Downloading", maxValue: totalBytes ?? 0);
                task.IsIndeterminate = totalBytes is null;

                var buffer = new byte[81920];
                int bytesRead;
                while ((bytesRead = await sourceStream.ReadAsync(buffer, cancel).ConfigureAwait(false)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancel).ConfigureAwait(false);
                    task.Increment(bytesRead);
                }

                task.StopTask();
            }).ConfigureAwait(false);
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("RepoQL", GetCurrentVersion()?.ToString() ?? "0.0.0"));
        client.Timeout = TimeSpan.FromMinutes(5);
        return client;
    }

    private static string GetRuntimeIdentifier()
    {
        if (OperatingSystem.IsWindows())
            return RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64";

        if (OperatingSystem.IsMacOS())
            return RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "osx-arm64" : "osx-x64";

        return RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "linux-arm64" : "linux-x64";
    }

    private static string? ResolveCurrentBinaryPath()
    {
        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath) && File.Exists(processPath) && LooksLikeRepoqlBinary(processPath))
            return processPath;

        return null;
    }

    private static bool LooksLikeRepoqlBinary(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        if (name.Contains("test", StringComparison.OrdinalIgnoreCase))
            return false;
        return name.Contains("repoql", StringComparison.OrdinalIgnoreCase);
    }

    private void WriteManualInstallInstructions()
    {
        if (OperatingSystem.IsWindows())
            console.MarkupLine("  [dim]irm https://downloads.repoql.ai/latest/install-repoql.ps1 | iex[/]");
        else
            console.MarkupLine("  [dim]curl -fsSL https://downloads.repoql.ai/latest/install-repoql.sh | bash[/]");
    }

    private void WriteRecoveryInstructions(string binaryPath)
    {
        var backupPath = binaryPath + ".old";
        console.MarkupLine("[yellow]If your installation is broken, try one of:[/]");
        console.MarkupLine($"  [dim]1. Rename '{Markup.Escape(Path.GetFileName(backupPath))}' back to '{Markup.Escape(Path.GetFileName(binaryPath))}'[/]");
        console.MarkupLine("  [dim]2. Reinstall:[/]");
        WriteManualInstallInstructions();
    }

    private async Task WarnIfHostRunningAsync(CancellationToken cancel)
    {
        IRepoQlClient? client = null;
        try
        {
            client = await RepoQlClient.TryCreateIfRunningAsync(cancellationToken: cancel).ConfigureAwait(false);
            if (client is not null)
                console.MarkupLine("[yellow]A RepoQL host is running. Restart it to use the new version.[/]");
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Best-effort — don't fail the update over a host detection issue.
        }
        finally
        {
            if (client is not null)
                await client.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static void EnsureExecutablePermission(string path)
    {
        try
        {
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = "chmod",
                ArgumentList = { "+x", path },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            if (process is null)
                throw new IOException("Failed to start chmod.");

            process.WaitForExit(5000);
            if (process.ExitCode != 0)
                throw new IOException($"chmod +x exited with code {process.ExitCode}.");
        }
        catch (Exception ex) when (ex is not IOException)
        {
            throw new IOException($"Could not set executable permission: {ex.Message}", ex);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Ignored — cleanup is best-effort.
        }
    }
}
