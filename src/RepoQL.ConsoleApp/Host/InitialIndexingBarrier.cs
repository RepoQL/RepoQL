using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RepoQL.Indexing.Hosting;

namespace RepoQL.ConsoleApp.Host;

internal sealed class InitialIndexingBarrier(
    IIndexingCoordinator coordinator,
    ILogger<InitialIndexingBarrier>? logger = null)
    : BackgroundService, IInitialIndexingBarrier
{
    private readonly TaskCompletionSource<bool> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ILogger<InitialIndexingBarrier> _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<InitialIndexingBarrier>.Instance;

    public Task InitialScanCompleted => _tcs.Task;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await coordinator.WaitForIdleAsync(stoppingToken).ConfigureAwait(false);
            _logger.LogInformation("Initial indexing completed");
            _tcs.TrySetResult(true);
        }
        catch (OperationCanceledException)
        {
            _tcs.TrySetCanceled(stoppingToken);
        }
        catch (Exception ex)
        {
            _tcs.TrySetException(ex);
        }
    }
}
