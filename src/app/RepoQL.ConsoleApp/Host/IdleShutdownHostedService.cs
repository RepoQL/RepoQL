using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RepoQL.Contracts.Configuration;

namespace RepoQL.ConsoleApp.Host;

internal sealed class IdleShutdownHostedService : BackgroundService
{
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<IdleShutdownHostedService> _logger;
    private readonly HostState _state;
    private readonly HostMetrics _metrics;
    private readonly TimeSpan _poll;
    private readonly TimeSpan _leaseTtl;
    private readonly TimeSpan _idleGrace;
    private readonly TimeSpan _shutdownWatchdog;
    private readonly Action _forceTerminate;
    private double _idleSecondsRemaining = -1;

    public IdleShutdownHostedService(
        IHostApplicationLifetime lifetime,
        ILogger<IdleShutdownHostedService> logger,
        HostState state,
        HostMetrics metrics,
        RepoQlConfig? config = null,
        TimeSpan? pollInterval = null,
        TimeSpan? leaseTtl = null,
        TimeSpan? idleGrace = null,
        TimeSpan? shutdownWatchdog = null,
        Action? forceTerminate = null)
    {
        _lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        var hostSettings = config?.Host;
        _poll = pollInterval ?? TimeSpan.FromSeconds(5);
        _leaseTtl = leaseTtl ?? TimeSpan.FromSeconds(ResolvePositiveInt(hostSettings?.LeaseTtlSeconds, 30));
        _idleGrace = idleGrace ?? GetIdleGrace(hostSettings);
        _shutdownWatchdog = shutdownWatchdog ?? GetImplicitShutdownWatchdog(hostSettings);
        _forceTerminate = forceTerminate ?? ForceTerminateCurrentProcess;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _metrics.SetLeaseCountProvider(() => LeaseRegistry.Count);
        _metrics.SetWriterPendingProvider(() => 0); // Always 0 with sync writes
        _metrics.SetImplicitStartProvider(() => _state.ImplicitStart ? 1 : 0);
        _metrics.SetIdleSecondsProvider(() => _idleSecondsRemaining);

        if (!_state.ImplicitStart)
            return;

        _logger.LogInformation(
            "IdleShutdown: supervising implicit host; grace={Grace}s ttl={Ttl}s watchdog={Watchdog}s",
            _idleGrace.TotalSeconds,
            _leaseTtl.TotalSeconds,
            _shutdownWatchdog.TotalSeconds);

        DateTime? idleStartUtc = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now = DateTime.UtcNow;
                foreach (var l in LeaseRegistry.Snapshot())
                {
                    if ((now - l.LastBeatUtc) > _leaseTtl)
                        LeaseRegistry.Remove(l.ClientId);
                }

                var active = LeaseRegistry.Count;
                if (active == 0)
                {
                    idleStartUtc ??= now;
                    var remaining = _idleGrace - (now - idleStartUtc.Value);
                    _idleSecondsRemaining = Math.Max(0, remaining.TotalSeconds);
                    if (remaining <= TimeSpan.Zero)
                    {
                        _logger.LogInformation("IdleShutdown: no clients for {Elapsed}s — shutting down", _idleGrace.TotalSeconds);
                        _lifetime.StopApplication();
                        ArmShutdownWatchdog(stoppingToken);
                        break;
                    }
                }
                else
                {
                    idleStartUtc = null;
                    _idleSecondsRemaining = -1;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "IdleShutdown loop error");
            }

            try
            {
                await Task.Delay(_poll, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private void ArmShutdownWatchdog(CancellationToken stoppingToken)
    {
        if (_shutdownWatchdog <= TimeSpan.Zero)
            return;

        _ = Task.Run(async () =>
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(
                stoppingToken,
                _lifetime.ApplicationStopped);

            try
            {
                await Task.Delay(_shutdownWatchdog, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                return;
            }

            try
            {
                _logger.LogCritical(
                    "IdleShutdown watchdog fired after {Timeout}s; process failed to exit after StopApplication. Forcing termination.",
                    _shutdownWatchdog.TotalSeconds);
                _forceTerminate();
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "IdleShutdown watchdog failed to force process termination.");
            }
        }, CancellationToken.None);
    }

    private static int ResolvePositiveInt(int? value, int dflt)
        => value is > 0 ? value.Value : dflt;

    private static int ResolveNonNegativeInt(int? value, int dflt)
        => value is >= 0 ? value.Value : dflt;

    private static TimeSpan GetIdleGrace(RepoQlConfig.HostSettings? settings)
    {
        if (IsMcpImplicitSource())
            return TimeSpan.FromSeconds(10); // Minimum grace for client to connect

        return TimeSpan.FromSeconds(ResolvePositiveInt(settings?.IdleGraceSeconds, 45));
    }

    private static TimeSpan GetImplicitShutdownWatchdog(RepoQlConfig.HostSettings? settings)
        => TimeSpan.FromSeconds(ResolveNonNegativeInt(settings?.ShutdownWatchdogSeconds, 15));

    private static void ForceTerminateCurrentProcess()
        => Process.GetCurrentProcess().Kill(entireProcessTree: true);

    private static bool IsMcpImplicitSource()
        => string.Equals(
            Environment.GetEnvironmentVariable("REPOQL_IMPLICIT_SOURCE"),
            "mcp",
            StringComparison.OrdinalIgnoreCase);
}
