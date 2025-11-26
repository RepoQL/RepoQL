using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RepoQL.Contracts.Data;
using RepoQL.Indexing.Hosting;
using RepoQL.Data.DuckDB;

namespace RepoQL.ConsoleApp.Host;

internal sealed class InitialIndexingBarrier(
    IIndexingCoordinator coordinator,
    IGraphStore store,
    ILogger<InitialIndexingBarrier>? logger = null)
    : BackgroundService, IInitialIndexingBarrier
{
    private readonly TaskCompletionSource<bool> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ILogger<InitialIndexingBarrier> _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<InitialIndexingBarrier>.Instance;
    private static readonly ActivitySource Activity = new("RepoQL.Host");

    public Task InitialScanCompleted => _tcs.Task;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await coordinator.WaitForIdleAsync(stoppingToken).ConfigureAwait(false);
            try
            {
                if (store is DuckDbGraphStore duck)
                {
                    using var span = Activity.StartActivity("repoql.search.refresh");
                    span?.SetTag("repoql.search.refresh.phase", "initial");
                    span?.SetTag("repoql.search.refresh.trigger", "barrier");
                    var sw = Stopwatch.StartNew();
                    duck.RefreshSearchProjection(incrementalRefresh: false);
                    sw.Stop();
                    _logger.LogInformation("Initial search refresh completed in {DurationMs} ms", (long)sw.Elapsed.TotalMilliseconds);
                    span?.SetTag("otel.status_code", "OK");
                    span?.SetTag("repoql.search.refresh.duration_ms", sw.Elapsed.TotalMilliseconds);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Initial search refresh failed; continuing");
                var span = System.Diagnostics.Activity.Current;
                span?.SetTag("otel.status_code", "ERROR");
                span?.SetTag("otel.status_description", ex.Message);
            }
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
