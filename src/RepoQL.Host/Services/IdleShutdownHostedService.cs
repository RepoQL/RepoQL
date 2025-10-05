using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using RepoQL.Contracts.Data;

namespace RepoQL.Host.Services;

public sealed class HostState
{
    public required string RepositoryPath { get; init; }
    public required bool ImplicitStart { get; init; }
    public required DateTime StartedAtUtc { get; init; }
}

internal static class LeaseRegistry
{
    public sealed record LeaseEntry(string ClientId, DateTime LastBeatUtc);

    private static readonly ConcurrentDictionary<string, LeaseEntry> _leases = new(StringComparer.OrdinalIgnoreCase);

    public static int Count => _leases.Count;

    public static void Upsert(string clientId, DateTime beatUtc)
        => _leases.AddOrUpdate(clientId, new LeaseEntry(clientId, beatUtc), (_, _) => new LeaseEntry(clientId, beatUtc));

    public static void Remove(string clientId)
        => _leases.TryRemove(clientId, out _);

    public static IEnumerable<LeaseEntry> Snapshot() => _leases.Values.ToArray();
}

/// <summary>
/// Shuts down the implicit host when there are no active client leases and the writer is idle for a grace period.
/// </summary>
internal sealed class IdleShutdownHostedService(
    IHostApplicationLifetime lifetime,
    ILogger<IdleShutdownHostedService> logger,
    HostState state,
    IDatabaseWriter writer
) : BackgroundService
{
    private readonly TimeSpan _poll = TimeSpan.FromSeconds(5);
    private readonly TimeSpan _leaseTtl = TimeSpan.FromSeconds(GetEnvInt("REPOQL_LEASE_TTL_SECONDS", 30));
    private readonly TimeSpan _idleGrace = TimeSpan.FromSeconds(GetEnvInt("REPOQL_IDLE_GRACE_SECONDS", 45));
    private readonly Meter _meter = new("RepoQL.Host");
    private double _idleSecondsRemaining = -1;
    // no explicit ctor; primary ctor used. Gauges are created in ExecuteAsync.

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Metrics: report active leases, writer pending, implicit flag, and seconds until shutdown (if applicable)
        _meter.CreateObservableGauge("repoql.host.leases.active", () => LeaseRegistry.Count, unit: "count", description: "Active client leases");
        _meter.CreateObservableGauge("repoql.host.writer.pending", () => writer.GetStatus().PendingCount, unit: "items", description: "Pending items in writer queue");
        _meter.CreateObservableGauge("repoql.host.implicit", () => state.ImplicitStart ? 1 : 0, unit: "bool", description: "1 when host was started implicitly");
        _meter.CreateObservableGauge("repoql.host.idle.seconds_until_shutdown", () => _idleSecondsRemaining, unit: "s", description: "Seconds until idle shutdown; -1 when not counting down");

        if (!state.ImplicitStart)
            return; // only supervise implicit hosts for shutdown

        logger.LogInformation("IdleShutdown: supervising implicit host; grace={Grace}s ttl={Ttl}s", _idleGrace.TotalSeconds, _leaseTtl.TotalSeconds);

        var idleStartUtc = (DateTime?)null;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Expire old leases
                var now = DateTime.UtcNow;
                foreach (var l in LeaseRegistry.Snapshot())
                {
                    if ((now - l.LastBeatUtc) > _leaseTtl)
                        LeaseRegistry.Remove(l.ClientId);
                }

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
                else
                {
                    idleStartUtc = null;
                    _idleSecondsRemaining = -1;
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "IdleShutdown loop error");
            }

            try { await Task.Delay(_poll, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { /* expected on shutdown */ }
        }
    }

    private static int GetEnvInt(string name, int dflt)
        => int.TryParse(Environment.GetEnvironmentVariable(name), out var v) && v > 0 ? v : dflt;
}
