using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace RepoQL.ConsoleApp.Helpers;

/// <summary>
/// Triggers the RepoQL client warm-up as part of the host lifecycle without blocking startup.
/// </summary>
internal sealed class RepoQlClientWarmupService : IHostedService
{
    private readonly RepoQlClientProvider _provider;
    private readonly ILogger<RepoQlClientWarmupService> _logger;

    public RepoQlClientWarmupService(
        RepoQlClientProvider provider,
        ILogger<RepoQlClientWarmupService>? logger = null)
    {
        _provider = provider;
        _logger = logger ?? NullLogger<RepoQlClientWarmupService>.Instance;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var warmup = _provider.EnsureStarted();
        warmup.ContinueWith(t =>
        {
            if (t.IsFaulted)
            {
                _logger.LogWarning(t.Exception?.GetBaseException(), "RepoQL client warm-up failed");
            }
        }, TaskScheduler.Default);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
