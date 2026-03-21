using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;

namespace RepoQL.Embedding.Writer;

/// <summary>
/// Purpose: Maps merge triggers onto cache merge processing.
/// Complexity: Accepts both Eventarc CloudEvent payloads (production) and direct JSON (local dev),
/// bad-message acknowledgement, retryable error mapping.
/// </summary>
internal static class MergeEndpoint
{
    public static async Task<IResult> HandleAsync(
        CacheMergeHandler? handler,
        HttpContext ctx,
        ILoggerFactory? loggerFactory = null)
    {
        var path = await ExtractStagingPathAsync(ctx).ConfigureAwait(false);

        if (!CacheMergeHandler.TryParseStagingPath(path, out _))
            return Results.Ok();

        ArgumentNullException.ThrowIfNull(handler);

        try
        {
            await handler.HandleAsync(path!, ctx.RequestAborted);
            return Results.Ok();
        }
        catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger(typeof(MergeEndpoint));
            logger.LogError(ex, "Retryable writer failure for staging path {StagingPath}", path);
            return Results.StatusCode(StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<string?> ExtractStagingPathAsync(HttpContext ctx)
    {
        try
        {
            // Eventarc sends CloudEvents with ce-type header in binary content mode.
            if (ctx.Request.Headers.ContainsKey("ce-type"))
            {
                var gcsEvent = await ctx.Request.ReadFromJsonAsync<GcsObjectData>(cancellationToken: ctx.RequestAborted);
                return gcsEvent?.Name;
            }

            // Local dev: direct JSON from embedding service.
            var request = await ctx.Request.ReadFromJsonAsync<MergeRequest>(cancellationToken: ctx.RequestAborted);
            return request?.Path;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // CloudEvent binary content mode body for google.cloud.storage.object.v1.finalized
    private sealed class GcsObjectData
    {
        public string? Name { get; init; }
        public string? Bucket { get; init; }
    }

    // Direct invocation format (local dev)
    private sealed class MergeRequest
    {
        public string? Path { get; init; }
    }
}
