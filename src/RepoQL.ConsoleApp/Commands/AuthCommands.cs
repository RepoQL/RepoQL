using ConsoleAppFramework;
using RepoQL.ConsoleApp.Auth;
using Spectre.Console;

namespace RepoQL.ConsoleApp.Commands;

/// <summary>
/// Purpose: Expose cloud authentication workflows as CLI verbs.
/// Complexity: Thin wrappers over CloudAuthService for login/logout/whoami.
/// </summary>
[RegisterCommands]
internal sealed class AuthCommands(IAnsiConsole console, CloudAuthService authService)
{
    /// <summary>
    /// Log in with a browser-based OAuth flow.
    /// </summary>
    /// <param name="deviceCode">Use the device authorization flow instead of opening a browser callback.</param>
    /// <param name="cancel">Cancellation token.</param>
    public async Task Login(bool deviceCode = false, CancellationToken cancel = default)
    {
        var progress = new Progress<CloudAuthService.AuthProgressUpdate>(update =>
        {
            switch (update.Kind)
            {
                case CloudAuthService.AuthProgressKind.Warning:
                    console.MarkupLine($"[yellow]{Markup.Escape(update.Message)}[/]");
                    break;
                default:
                    console.WriteLine(update.Message);
                    break;
            }
        });

        var result = await authService.LoginAsync(deviceCode, progress, cancel).ConfigureAwait(false);
        console.WriteLine($"Logged in as {result.DisplayName}");
    }

    /// <summary>
    /// Remove the locally stored RepoQL session.
    /// </summary>
    /// <param name="cancel">Cancellation token.</param>
    public async Task Logout(CancellationToken cancel = default)
    {
        var message = await authService.LogoutAsync(cancel).ConfigureAwait(false);
        console.WriteLine(message);
    }

    /// <summary>
    /// Show the currently authenticated RepoQL identity.
    /// </summary>
    /// <param name="cancel">Cancellation token.</param>
    public async Task Whoami(CancellationToken cancel = default)
    {
        var message = await authService.WhoAmIAsync(cancel).ConfigureAwait(false);
        console.WriteLine(message);
    }
}
