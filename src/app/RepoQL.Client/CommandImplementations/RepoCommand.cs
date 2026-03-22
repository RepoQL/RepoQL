using RepoQL.Commands;
using RepoQL.Client.Helpers;
using RepoQL.Contracts;

namespace RepoQL.Client.CommandImplementations;

/// <summary>
/// Purpose: Switch the active repository without restarting the MCP server.
/// Complexity: Path validation, repo marker walk-up, repeat-to-confirm for unmarked directories,
/// client dispose/reconnect with rollback on failure.
/// </summary>
[CommandClass]
internal sealed class RepoCommand(RepoQlClientProvider clientProvider)
{
    private static string? _pendingConfirmPath;
    private static readonly object PendingConfirmSync = new();

    [Command("repo", Description = "Switch to a different repository")]
    public async Task<CommandResult> Execute(
        [CommandParam("Path to repository directory")] string path,
        CancellationToken cancel)
    {
        if (string.IsNullOrWhiteSpace(path))
            return CommandResult.Error("Path is required. Usage: ::repo[C:\\Source\\MyRepo]");

        string resolvedPath;
        try
        {
            resolvedPath = Path.GetFullPath(path.Trim());
        }
        catch (Exception ex)
        {
            return CommandResult.Error($"Invalid path: {ex.Message}");
        }

        if (!Directory.Exists(resolvedPath))
            return CommandResult.Error($"Directory not found: {resolvedPath}");

        // Walk up to repo root if markers exist; otherwise use given path
        string targetPath;
        if (RepoLocator.TryFindRepoRoot(resolvedPath, out var repoRoot, out _, allowFallback: false))
        {
            targetPath = repoRoot!;
            ClearPendingConfirmation();
        }
        else
        {
            // No markers — require repeat-to-confirm
            var normalized = NormalizePath(resolvedPath);
            var isConfirmed = false;
            lock (PendingConfirmSync)
            {
                if (string.Equals(_pendingConfirmPath, normalized, PathComparison))
                {
                    _pendingConfirmPath = null;
                    isConfirmed = true;
                }
                else
                {
                    _pendingConfirmPath = normalized;
                }
            }

            if (!isConfirmed)
            {
                return CommandResult.Success(
                    $"No repository markers (.git/.repoql) found at {resolvedPath}. " +
                    $"RepoQL will create a new index here. Call ::repo[{path.Trim()}] again to confirm.");
            }

            targetPath = resolvedPath;
        }

        // Switch: dispose current, set new path, connect
        try
        {
            await clientProvider.DisposeAsync().ConfigureAwait(false);
            clientProvider.SetWorkingDirectory(targetPath);
            await clientProvider.GetClientAsync(cancel).ConfigureAwait(false);
            return CommandResult.Success($"Switched to repository: {targetPath}");
        }
        catch (Exception ex)
        {
            return CommandResult.Error($"Failed to connect to repository at {targetPath}: {ex.Message}");
        }
    }

    /// <summary>
    /// Reset confirmation state (for testing).
    /// </summary>
    internal static void ResetConfirmation() => ClearPendingConfirmation();

    private static void ClearPendingConfirmation()
    {
        lock (PendingConfirmSync)
        {
            _pendingConfirmPath = null;
        }
    }

    private static string NormalizePath(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
