using RepoQL.Commands;
using RepoQL.ConsoleApp.Helpers;
using RepoQL.Contracts;

namespace RepoQL.ConsoleApp.CommandImplementations;

/// <summary>
/// Purpose: Provide surgical queue control for one URI (cancel, skip, retry).
/// Complexity: Thin gRPC client wrapper over host-side QueueControl execution.
/// </summary>
[CommandClass]
internal sealed class QueueCommand(RepoQlClientProvider clientProvider)
{
    [Command("queue.cancel", Description = "Cancel one URI at the next stage boundary")]
    public Task<CommandResult> Cancel(
        [CommandParam("File URI (e.g., file:///src/App.cs)")] string uri,
        CancellationToken cancel)
        => ExecuteAsync(QueueControlAction.Cancel, uri, cancel);

    [Command("queue.skip", Description = "Skip one URI and persist it in .repoql/skip-list.txt")]
    public Task<CommandResult> Skip(
        [CommandParam("File URI (e.g., file:///src/App.cs)")] string uri,
        CancellationToken cancel)
        => ExecuteAsync(QueueControlAction.Skip, uri, cancel);

    [Command("queue.retry", Description = "Re-enqueue one failed or skipped URI")]
    public Task<CommandResult> Retry(
        [CommandParam("File URI (e.g., file:///src/App.cs)")] string uri,
        CancellationToken cancel)
        => ExecuteAsync(QueueControlAction.Retry, uri, cancel);

    private async Task<CommandResult> ExecuteAsync(QueueControlAction action, string uri, CancellationToken cancel)
    {
        try
        {
            var client = await clientProvider.GetClientAsync(cancel).ConfigureAwait(false);
            var response = await client.QueueControlAsync(action, uri, cancel).ConfigureAwait(false);
            return response.Success
                ? CommandResult.Success(response.Message)
                : CommandResult.Error(response.Message);
        }
        catch (Exception ex)
        {
            return CommandResult.Error($"Queue command failed: {ex.Message}");
        }
    }
}
