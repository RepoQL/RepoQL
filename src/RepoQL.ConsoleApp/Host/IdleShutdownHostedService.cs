using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RepoQL.Contracts.Data;

namespace RepoQL.ConsoleApp.Host;

internal sealed class HostState
{
    public required string RepositoryPath { get; init; }
    public required bool ImplicitStart { get; init; }
    public required DateTime StartedAtUtc { get; init; }
}

internal static class LeaseRegistry
{
    internal sealed record LeaseEntry(string ClientId, DateTime LastBeatUtc);

    private static readonly ConcurrentDictionary<string, LeaseEntry> Leases = new(StringComparer.OrdinalIgnoreCase);

    public static int Count => Leases.Count;
    public static void Upsert(string clientId, DateTime beatUtc)
        => Leases.AddOrUpdate(clientId, new LeaseEntry(clientId, beatUtc), (_, _) => new LeaseEntry(clientId, beatUtc));
    public static void Remove(string clientId) => Leases.TryRemove(clientId, out _);
    public static IEnumerable<LeaseEntry> Snapshot() => Leases.Values.ToArray();
}

internal sealed class IdleShutdownHostedService(
    IHostApplicationLifetime lifetime,
    ILogger<IdleShutdownHostedService> logger,
    HostState state,
    IDatabaseWriter writer,
    HostMetrics metrics
) : BackgroundService
{
    private readonly TimeSpan _poll = TimeSpan.FromSeconds(5);
    private readonly TimeSpan _leaseTtl = TimeSpan.FromSeconds(GetEnvInt("REPOQL_LEASE_TTL_SECONDS", 30));
    private readonly TimeSpan _idleGrace = TimeSpan.FromSeconds(GetEnvInt("REPOQL_IDLE_GRACE_SECONDS", 45));
    private double _idleSecondsRemaining = -1;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        metrics.SetLeaseCountProvider(() => LeaseRegistry.Count);
        metrics.SetWriterPendingProvider(() => writer.GetStatus().PendingCount);
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
                var writerIdle = writer.GetStatus().PendingCount == 0;

                if (active == 0 && writerIdle)
                {
                    idleStartUtc ??= now;
                    var remaining = _idleGrace - (now - idleStartUtc.Value);
                    _idleSecondsRemaining = Math.Max(0, remaining.TotalSeconds);
                    if (remaining <= TimeSpan.Zero)
                    {
                        logger.LogInformation("IdleShutdown: no clients and writer idle for {Elapsed}s — shutting down", _idleGrace.TotalSeconds);
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
}
