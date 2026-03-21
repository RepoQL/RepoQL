using System.Collections.Concurrent;
using RepoQL.Contracts.Configuration;

namespace RepoQL.ConsoleApp.Host;

/// <summary>
/// Purpose: Track active gRPC requests so diagnostics can detect hanging calls.
/// Complexity: Lock-free scope tracking with snapshot aggregation and method exclusions.
/// </summary>
internal sealed class RpcActivityTracker
{
    private const string HealthCheckMethod = "/grpc.health.v1.Health/Check";
    private const string HealthWatchMethod = "/grpc.health.v1.Health/Watch";
    private const string HoldClientLeaseMethod = "/repoql.v1.RepoQL/HoldClientLease";
    private const string WatchStatusMethod = "/repoql.v1.RepoQL/WatchStatus";
    private static readonly IDisposable NullScope = new NullDisposable();
    private readonly ConcurrentDictionary<long, ActiveCall> _activeCalls = new();
    private readonly TimeSpan _hangThreshold;
    private long _nextId;

    public RpcActivityTracker(RepoQlConfig? config = null, TimeSpan? hangThreshold = null)
    {
        _hangThreshold = hangThreshold ?? ResolveThreshold(config?.Host?.RpcHangThresholdMs);
    }

    public IDisposable BeginScope(string? method, DateTime? startedAtUtc = null)
    {
        if (!ShouldTrackMethod(method))
            return NullScope;

        var id = Interlocked.Increment(ref _nextId);
        _activeCalls[id] = new ActiveCall(method!, startedAtUtc ?? DateTime.UtcNow);
        return new Scope(this, id);
    }

    public RpcActivitySnapshot CaptureSnapshot(DateTime nowUtc)
    {
        if (_activeCalls.IsEmpty)
            return new RpcActivitySnapshot(0, 0, (long)_hangThreshold.TotalMilliseconds, null, null);

        var activeCount = 0;
        var hangingCount = 0;
        long? oldestAgeMs = null;
        string? oldestMethod = null;
        var thresholdMs = (long)_hangThreshold.TotalMilliseconds;

        foreach (var call in _activeCalls.Values)
        {
            activeCount++;
            var ageMs = Math.Max(0, (long)(nowUtc - call.StartedAtUtc).TotalMilliseconds);
            if (ageMs >= thresholdMs)
                hangingCount++;

            if (!oldestAgeMs.HasValue || ageMs > oldestAgeMs.Value)
            {
                oldestAgeMs = ageMs;
                oldestMethod = call.Method;
            }
        }

        return new RpcActivitySnapshot(activeCount, hangingCount, thresholdMs, oldestAgeMs, oldestMethod);
    }

    private void EndScope(long id)
    {
        _activeCalls.TryRemove(id, out _);
    }

    private static bool ShouldTrackMethod(string? method)
    {
        if (string.IsNullOrWhiteSpace(method))
            return false;

        return !string.Equals(method, HealthCheckMethod, StringComparison.Ordinal)
               && !string.Equals(method, HealthWatchMethod, StringComparison.Ordinal)
               && !string.Equals(method, HoldClientLeaseMethod, StringComparison.Ordinal)
               && !string.Equals(method, WatchStatusMethod, StringComparison.Ordinal);
    }

    private static TimeSpan ResolveThreshold(int? configuredMs)
    {
        var ms = configuredMs is > 0 ? configuredMs.Value : 30_000;
        return TimeSpan.FromMilliseconds(ms);
    }

    private readonly record struct ActiveCall(string Method, DateTime StartedAtUtc);

    /// <summary>
    /// Purpose: Carry a point-in-time summary of tracked gRPC activity.
    /// Complexity: Value-only snapshot consumed by health trailer diagnostics.
    /// </summary>
    internal readonly record struct RpcActivitySnapshot(
        int ActiveCount,
        int HangingCount,
        long HangThresholdMs,
        long? OldestRequestAgeMs,
        string? OldestRequestMethod);

    private sealed class Scope(RpcActivityTracker owner, long id) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            owner.EndScope(id);
        }
    }

    private sealed class NullDisposable : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
