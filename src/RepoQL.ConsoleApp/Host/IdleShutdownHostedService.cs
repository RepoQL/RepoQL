using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace RepoQL.ConsoleApp.Host;

internal sealed class IdleShutdownHostedService(
    IHostApplicationLifetime lifetime,
    ILogger<IdleShutdownHostedService> logger,
    HostState state,
    HostMetrics metrics
) : BackgroundService
{
    private readonly TimeSpan _poll = TimeSpan.FromSeconds(5);
    private readonly TimeSpan _leaseTtl = TimeSpan.FromSeconds(GetEnvInt("REPOQL_LEASE_TTL_SECONDS", 30));
    private readonly TimeSpan _idleGrace = GetIdleGrace();
    private double _idleSecondsRemaining = -1;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        metrics.SetLeaseCountProvider(() => LeaseRegistry.Count);
        metrics.SetWriterPendingProvider(() => 0); // Always 0 with sync writes
        metrics.SetImplicitStartProvider(() => state.ImplicitStart ? 1 : 0);
        metrics.SetIdleSecondsProvider(() => _idleSecondsRemaining);

        if (!state.ImplicitStart) return;

        logger.LogInformation("IdleShutdown: supervising implicit host; grace={Grace}s ttl={Ttl}s", _idleGrace.TotalSeconds, _leaseTtl.TotalSeconds);

        var idleStartUtc = (DateTime?)null;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now = DateTime.UtcNow;
                foreach (var l in LeaseRegistry.Snapshot())
                    if ((now - l.LastBeatUtc) > _leaseTtl) LeaseRegistry.Remove(l.ClientId);

                var active = LeaseRegistry.Count;

                if (active == 0)
                {
                    idleStartUtc ??= now;
                    var remaining = _idleGrace - (now - idleStartUtc.Value);
                    _idleSecondsRemaining = Math.Max(0, remaining.TotalSeconds);
                    if (remaining <= TimeSpan.Zero)
                    {
                        logger.LogInformation("IdleShutdown: no clients for {Elapsed}s — shutting down", _idleGrace.TotalSeconds);
                        lifetime.StopApplication();
                        break;
                    }
                }
                else { idleStartUtc = null; _idleSecondsRemaining = -1; }
            }
            catch (Exception ex) { logger.LogError(ex, "IdleShutdown loop error"); }

            try { await Task.Delay(_poll, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
    }

    private static int GetEnvInt(string name, int dflt)
        => int.TryParse(Environment.GetEnvironmentVariable(name), out var v) && v > 0 ? v : dflt;

    private static TimeSpan GetIdleGrace()
    {
        if (IsMcpImplicitSource())
            return TimeSpan.FromSeconds(10); // Minimum grace for client to connect

        return TimeSpan.FromSeconds(GetEnvInt("REPOQL_IDLE_GRACE_SECONDS", 45));
    }

    private static bool IsMcpImplicitSource()
        => string.Equals(
            Environment.GetEnvironmentVariable("REPOQL_IMPLICIT_SOURCE"),
            "mcp",
            StringComparison.OrdinalIgnoreCase);
}
