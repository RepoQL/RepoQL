using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RepoQL.Contracts.Data;
using RepoQL.Core;
using RepoQL.Data.DuckDB;

namespace RepoQL.ConsoleApp.Host;

public interface IInitialIndexingBarrier
{
    Task InitialScanCompleted { get; }
}

internal sealed class InitialIndexingBarrier(
    RepositoryIndexer indexer,
    IGraphStore store,
    Contracts.Embeddings.IEmbeddingProvider embeddingProvider,
    IDuckDBConnectionFactory connectionFactory,
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
            await indexer.WaitForIdle(stoppingToken).ConfigureAwait(false);
            try
            {
                if (store is DuckDbGraphStore duck)
                {
                    using var span = Activity.StartActivity("repoql.search.refresh");
                    span?.SetTag("repoql.search.refresh.phase", "initial");
                    span?.SetTag("repoql.search.refresh.trigger", "barrier");
                    duck.RefreshSearchProjection(incrementalRefresh: false);
                    span?.SetTag("otel.status_code", "OK");
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
            _ = Task.Run(() => BackgroundEmbedAsync(stoppingToken));
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

    private async Task BackgroundEmbedAsync(CancellationToken ct)
    {
        if (!embeddingProvider.Enabled) return;
        try
        {
            await Task.Yield(); // Run on background thread
            using var span = Activity.StartActivity("repoql.embed.refresh", ActivityKind.Internal);
            using var conn = connectionFactory.CreateConnection();
            using var duck = new DuckDbGraphStore(conn, enableExtensions: true, registerUdfs: false, logger: null, embeddingProvider: embeddingProvider);
            _logger.LogInformation("Background embedding refresh started (max_tokens may be reduced for speed)");
            duck.RefreshDocumentEmbeddings(embeddingProvider, ct);
            span!.SetTag("otel.status_code", "OK");
            _logger.LogInformation("Background embedding refresh completed");
        }
        catch (OperationCanceledException) { _logger.LogInformation("Background embedding refresh canceled"); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Background embedding refresh failed");
            var span = System.Diagnostics.Activity.Current;
            span?.SetTag("otel.status_code", "ERROR");
            span?.SetTag("otel.status_description", ex.Message);
        }
    }
}

