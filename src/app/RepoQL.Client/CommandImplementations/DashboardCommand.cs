using System.Diagnostics;
using RepoQL.Commands;
using RepoQL.Contracts;
using RepoQL.Client.Diagnostics;

namespace RepoQL.Client.CommandImplementations;

/// <summary>
/// Purpose: Open the live host dashboard in the default browser.
/// Complexity: Reads dashboard URL from diagnostics file (works from MCP client process),
/// tracks one-time open per session, supports force reopen.
/// </summary>
[CommandClass]
internal sealed class DashboardCommand
{
    private static bool _browserOpened;

    [Command("dashboard", Description = "Open the live dashboard in a browser")]
    public CommandResult Execute(
        [CommandParam("Pass 'open' to force re-open the browser")] string? force)
    {
        var url = ResolveDashboardUrl();
        if (url is null)
        {
            return CommandResult.Error(
                "Dashboard not available — no dashboard-bind.json found. " +
                "Is the host running? (The host must be started with an HTTP listener.)");
        }

        var shouldReopen = string.Equals(force, "force", StringComparison.OrdinalIgnoreCase)
                           || string.Equals(force, "open", StringComparison.OrdinalIgnoreCase);
        if (shouldReopen)
            _browserOpened = false;

        if (_browserOpened)
            return CommandResult.Success($"Dashboard is already open at {url}");

        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        _browserOpened = true;
        return CommandResult.Success($"Dashboard opened: {url}");
    }

    private static string? ResolveDashboardUrl()
    {
        try
        {
            var repoRoot = RepoLocator.FindRepoRoot();
            if (HostDiagnosticsStore.TryReadReport<DashboardBindReport>(repoRoot, "dashboard-bind.json", out var report)
                && report?.Url is not null)
            {
                return report.Url;
            }
        }
        catch
        {
            // RepoLocator may throw if no repo markers found — not fatal
        }

        return null;
    }
}


