using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;

namespace RepoQL.Embedding.Writer;

/// <summary>
/// Purpose: Maps Cloud Scheduler and Cloud Tasks callbacks onto compaction work.
/// Complexity: Trigger parsing, bad-message acknowledgement, and retryable error mapping.
/// </summary>
internal static class CompactionEndpoint
{
    public static async Task<IResult> HandleAsync(
        CompactionJob? job,
        HttpContext ctx,
        ILoggerFactory? loggerFactory = null)
    {
        CompactionRequest? request;

        try
        {
            request = await ctx.Request.ReadFromJsonAsync<CompactionRequest>(cancellationToken: ctx.RequestAborted);
        }
        catch (JsonException)
        {
            return Results.Ok();
        }

        ArgumentNullException.ThrowIfNull(job);

        try
        {
            if (string.Equals(request?.Trigger, CompactionRequest.NightlyTrigger, StringComparison.Ordinal))
            {
                await job.RunNightlyAsync(ctx.RequestAborted);
                return Results.Ok();
            }

            if (!CompactionShardInfo.TryCreate(request?.Source, request?.Model, out var shard))
                return Results.Ok();

            await job.RunShardAsync(shard, ctx.RequestAborted);
            return Results.Ok();
        }
        catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger(typeof(CompactionEndpoint));
            logger.LogError(ex, "Retryable compaction failure.");
            return Results.StatusCode(StatusCodes.Status500InternalServerError);
        }
    }

    private sealed class CompactionRequest
    {
        internal const string NightlyTrigger = "nightly-compaction";

        public string? Trigger { get; init; }
        public string? Source { get; init; }
        public string? Model { get; init; }
    }
}
