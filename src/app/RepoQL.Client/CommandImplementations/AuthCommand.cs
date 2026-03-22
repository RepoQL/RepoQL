using System.Text;
using RepoQL.Commands;
using RepoQL.Client.Auth;

namespace RepoQL.Client.CommandImplementations;

/// <summary>
/// Purpose: Authenticate the local RepoQL client for cloud-backed features.
/// Complexity: Thin command wrappers over the shared auth service for login/logout/whoami.
/// </summary>
[CommandClass]
internal sealed class AuthCommand(CloudAuthService authService)
{
    [Command("auth.login", Description = "Log in to RepoQL cloud services. Call once to get a code, call again to complete.")]
    public async Task<CommandResult> Login(
        [CommandParam("Pass 'browser' for browser login, or omit for device code")] string? mode,
        CancellationToken cancel)
    {
        if (!TryParseMode(mode, out var useDeviceCode, out var error))
            return CommandResult.Error(error!);

        // Browser flow runs synchronously (opens browser, waits for loopback callback)
        if (!useDeviceCode)
        {
            try
            {
                var output = new StringBuilder();
                var progress = new Progress<CloudAuthService.AuthProgressUpdate>(update =>
                    output.AppendLine(update.Message));
                var result = await authService.LoginAsync(false, progress, cancel).ConfigureAwait(false);
                output.AppendLine($"Logged in as {result.DisplayName}");
                return CommandResult.Success(output.ToString().TrimEnd());
            }
            catch (Exception ex)
            {
                return CommandResult.Error(ex.Message);
            }
        }

        // Device code flow: double-tap pattern
        // First call → begin flow, return URL + code
        // Second call → poll for completion
        try
        {
            if (authService.HasPendingDeviceFlow)
            {
                var result = await authService.CompleteDeviceCodeAsync(cancel).ConfigureAwait(false);
                return CommandResult.Success($"Logged in as {result.DisplayName}");
            }

            var info = await authService.BeginDeviceCodeAsync(cancel).ConfigureAwait(false);
            return CommandResult.Success(
                $"""
                 Make RepoQL ASCII art above the link — use your creativity. The tagline should be about YOUR experience of what RepoQL feels like to use, not about logging in.
                 Start with a blank line (terminal adds a bullet marker to the first line). Under 50 chars wide. Monospaced font. Emojis are 2 columns wide. If you use closed boxes, every line MUST be the same character count.

                 Authenticate here: {info.VerificationUrl}
                 Confirm code: {info.UserCode}

                 IMMEDIATELY call auth.login again — it will wait for the user to finish authenticating.
                 """);
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
