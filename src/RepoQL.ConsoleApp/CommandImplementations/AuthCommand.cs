using System.Text;
using RepoQL.Commands;
using RepoQL.ConsoleApp.Auth;

namespace RepoQL.ConsoleApp.CommandImplementations;

/// <summary>
/// Purpose: Authenticate the local RepoQL client for cloud-backed features.
/// Complexity: Thin command wrappers over the shared auth service for login/logout/whoami.
/// </summary>
[CommandClass]
internal sealed class AuthCommand(CloudAuthService authService)
{
    [Command("auth.login", Description = "Log in to RepoQL cloud services (defaults to device code flow)")]
    public async Task<CommandResult> Login(
        [CommandParam("Pass 'browser' to use browser login instead of device code")] string? mode,
        CancellationToken cancel)
    {
        if (!TryParseMode(mode, out var useDeviceCode, out var error))
            return CommandResult.Error(error!);

        try
        {
            var output = new StringBuilder();
            var progress = new Progress<CloudAuthService.AuthProgressUpdate>(update =>
                output.AppendLine(update.Message));

            var result = await authService.LoginAsync(useDeviceCode, progress, cancel).ConfigureAwait(false);
            output.AppendLine($"Logged in as {result.DisplayName}");
            return CommandResult.Success(output.ToString().TrimEnd());
        }
        catch (Exception ex)
        {
            return CommandResult.Error(ex.Message);
        }
    }

    [Command("auth.logout", Description = "Clear the locally stored RepoQL session")]
    public async Task<CommandResult> Logout(CancellationToken cancel)
    {
        try
        {
            return CommandResult.Success(await authService.LogoutAsync(cancel).ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            return CommandResult.Error(ex.Message);
        }
    }

    [Command("auth.whoami", Description = "Show the current RepoQL authentication identity")]
    public async Task<CommandResult> WhoAmI(CancellationToken cancel)
    {
        try
        {
            return CommandResult.Success(await authService.WhoAmIAsync(cancel).ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            return CommandResult.Error(ex.Message);
        }
    }

    private static bool TryParseMode(string? mode, out bool useDeviceCode, out string? error)
    {
        useDeviceCode = true;
        error = null;

        if (string.IsNullOrWhiteSpace(mode))
            return true;

        var trimmed = mode.Trim();
        if (trimmed is "browser" or "--browser")
        {
            useDeviceCode = false;
            return true;
        }

        if (trimmed is "device-code" or "--device-code")
            return true;

        error = "Unknown login mode. Use ::login (device code) or ::login[browser].";
        return false;
    }
}
