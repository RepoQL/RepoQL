using System.Diagnostics;
using System.Text;
using RepoQL.Commands;
using RepoQL.ConsoleApp.Helpers;

namespace RepoQL.ConsoleApp.CommandImplementations;

/// <summary>
/// Purpose: Trigger a reindex, optionally scoped to a URI glob pattern.
/// Complexity: Streams progress from the gRPC ReindexAll RPC, collects per-phase timing.
/// </summary>
[CommandClass]
internal sealed class ReindexCommand(RepoQlClientProvider clientProvider)
{
    [Command("reindex", Description = "Reindex files, optionally scoped to a URI pattern")]
    public Task<CommandResult> Execute(
        [CommandParam("URI glob pattern (e.g., file:///src/**/*.cs, github://owner/repo/**). Omit to reindex ALL file systems including imports.")] string? scope,
        CancellationToken cancel)
        => RunReindex(clear: false, scope, cancel);

    [Command("reindex.clear", Description = "Clear existing data then reindex from scratch")]
    public Task<CommandResult> ExecuteClear(
        [CommandParam("URI glob pattern (e.g., file:///src/**/*.cs, github://owner/repo/**). Omit to reindex ALL file systems including imports.")] string? scope,
        CancellationToken cancel)
        => RunReindex(clear: true, scope, cancel);

    private async Task<CommandResult> RunReindex(bool clear, string? scope, CancellationToken cancel)
    {
        try
        {
            var client = await clientProvider.GetClientAsync(cancel).ConfigureAwait(false);
            var sw = Stopwatch.StartNew();

            long totalItems = 0;
            var phases = new Dictionary<string, PhaseStats>();
            uint failedCount = 0;
            List<string>? failureDetails = null;
            List<string>? milestones = null;

            await foreach (var progress in client.ReindexAllAsync(clear: clear, scope: scope, cancellationToken: cancel).ConfigureAwait(false))
            {
                totalItems = Math.Max(totalItems, (long)progress.TotalItems);
                var phase = progress.Phase.ToString().Replace("ReindexPhase", "");
                phases[phase] = new PhaseStats((long)progress.ProcessedItems, progress.PhaseElapsedMs);

                if (progress.FailedCount > 0)
                    failedCount = progress.FailedCount;
                if (progress.FailureDetails.Count > 0)
                    failureDetails = [..progress.FailureDetails];
                if (progress.Milestones.Count > 0)
                    milestones = [..progress.Milestones];
            }

            sw.Stop();

            var sb = new StringBuilder();
            var scopeInfo = string.IsNullOrWhiteSpace(scope) ? "" : $" (scope: {scope})";
            var failedInfo = failedCount > 0 ? $", {failedCount} failed" : "";
            sb.AppendLine($"Reindex complete: {totalItems:N0} items in {sw.Elapsed.TotalSeconds:F1}s{failedInfo}{scopeInfo}.");

            if (phases.Count > 1)
            {
                sb.AppendLine();
                var maxName = phases.Keys.Max(k => k.Length);
                foreach (var (phase, stats) in phases)
                {
                    var elapsed = TimeSpan.FromMilliseconds(stats.ElapsedMs);
                    var items = stats.ProcessedItems > 0 ? $"{stats.ProcessedItems:N0} items" : "";
                    sb.AppendLine($"  {phase.PadRight(maxName)}  {elapsed.TotalSeconds,6:F1}s  {items}");
                }
            }

            if (milestones is { Count: > 0 })
            {
                sb.AppendLine();
                sb.AppendLine("Milestones:");
                foreach (var m in milestones)
                    sb.AppendLine($"  {m}");
            }

            if (failureDetails is { Count: > 0 })
            {
                sb.AppendLine();
                sb.AppendLine("Failures:");
                foreach (var f in failureDetails)
                    sb.AppendLine($"  {f}");
            }

            return CommandResult.Success(sb.ToString().TrimEnd());
        }
        catch (Exception ex)
        {
            return CommandResult.Error($"Reindex failed: {ex.Message}");
        }
    }

    private readonly record struct PhaseStats(long ProcessedItems, ulong ElapsedMs);
}
