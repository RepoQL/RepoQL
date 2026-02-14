using RepoQL.Commands;
using RepoQL.ConsoleApp.Helpers;

namespace RepoQL.ConsoleApp.CommandImplementations;

/// <summary>
/// Purpose: Trigger a reindex, optionally scoped to a URI glob pattern.
/// Complexity: Streams progress from the gRPC ReindexAll RPC, reports final counts.
/// </summary>
[CommandClass]
internal sealed class ReindexCommand(RepoQlClientProvider clientProvider)
{
    [Command("reindex", Description = "Reindex files, optionally scoped to a URI pattern")]
    public async Task<CommandResult> Execute(
        [CommandParam("URI glob pattern (e.g., file:///src/**/*.cs). Omit for all.")] string? scope,
        CancellationToken cancel)
    {
        try
        {
            var client = await clientProvider.GetClientAsync(cancel).ConfigureAwait(false);

            long totalItems = 0;
            long processedItems = 0;
            string lastPhase = "unknown";

            await foreach (var progress in client.ReindexAllAsync(clear: false, scope: scope, cancellationToken: cancel).ConfigureAwait(false))
            {
                totalItems = Math.Max(totalItems, (long)progress.TotalItems);
                processedItems = Math.Max(processedItems, (long)progress.ProcessedItems);
                lastPhase = progress.Phase.ToString();
            }

            var scopeInfo = string.IsNullOrWhiteSpace(scope)
                ? ""
                : $" (scope: {scope})";

            return CommandResult.Success($"Reindex complete: {processedItems}/{totalItems} items{scopeInfo}.");
        }
        catch (Exception ex)
        {
            return CommandResult.Error($"Reindex failed: {ex.Message}");
        }
    }
}
