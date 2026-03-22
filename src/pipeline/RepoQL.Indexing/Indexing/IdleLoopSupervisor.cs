using Microsoft.Extensions.Logging;

namespace RepoQL.Indexing.Indexing;

internal static class IdleLoopSupervisor
{
    internal static readonly TimeSpan[] RestartBackoff =
    [
        TimeSpan.FromMilliseconds(250),
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(5)
    ];

    internal static async Task RunAsync(
        string loopName,
        Func<CancellationToken, Task> runLoopAsync,
        Action<Exception> onFailure,
        ILogger logger,
        CancellationToken cancellationToken,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(loopName);
        ArgumentNullException.ThrowIfNull(runLoopAsync);
        ArgumentNullException.ThrowIfNull(onFailure);
        ArgumentNullException.ThrowIfNull(logger);

        delayAsync ??= static (delay, token) => Task.Delay(delay, token);

        var restartAttempt = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            Exception failure;
            try
            {
                await runLoopAsync(cancellationToken).ConfigureAwait(false);
                if (cancellationToken.IsCancellationRequested)
                    return;

                failure = new InvalidOperationException($"{loopName} exited unexpectedly.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failure = ex;
            }

            onFailure(failure);
            logger.LogCritical(failure, "{LoopName} failed unexpectedly.", loopName);

            if (restartAttempt >= RestartBackoff.Length)
                return;

            var delay = RestartBackoff[restartAttempt++];
            try
            {
                await delayAsync(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
        }
    }
}
