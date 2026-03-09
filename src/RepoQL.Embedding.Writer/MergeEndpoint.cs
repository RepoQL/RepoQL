using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;

namespace RepoQL.Embedding.Writer;

/// <summary>
/// Purpose: Maps Cloud Tasks HTTP callbacks onto cache merge processing.
/// Complexity: JSON payload parsing, bad-message acknowledgement, retryable error mapping.
/// </summary>
internal static class MergeEndpoint
{
    public static async Task<IResult> HandleAsync(
        CacheMergeHandler? handler,
        HttpContext ctx,
        ILoggerFactory? loggerFactory = null)
    {
        MergeRequest? request;

        try
        {
            request = await ctx.Request.ReadFromJsonAsync<MergeRequest>(cancellationToken: ctx.RequestAborted);
        }
        catch (JsonException)
        {
            return Results.Ok();
        }

        var path = request?.Path;
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

    private sealed class MergeRequest
    {
        public string? Path { get; init; }
    }
}
