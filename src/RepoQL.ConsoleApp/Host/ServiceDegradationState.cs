using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using RepoQL.Contracts;

namespace RepoQL.ConsoleApp.Host;

/// <summary>
/// Purpose: Track degraded services and surface warnings once per host lifecycle.
/// Complexity: Synchronizes degradation entries while keeping warnings sticky and single-shot.
/// </summary>
internal sealed class ServiceDegradationState
{
    private readonly ConcurrentDictionary<ServiceDegradationKind, ServiceDegradationEntry> _entries = new();
    private int _warningEmitted;

    public IReadOnlyList<ServiceDegradationEntry> Entries => _entries.Values.ToList();

    public bool HasDegradation => !_entries.IsEmpty;

    public bool MarkDegraded(ServiceDegradationKind kind, string message)
    {
        return _entries.TryAdd(kind, new ServiceDegradationEntry(kind, message));
    }

    public bool TryGetWarningMessage(out string message)
    {
        message = string.Empty;
        if (_entries.IsEmpty)
            return false;

        if (Interlocked.Exchange(ref _warningEmitted, 1) == 1)
            return false;

        var services = string.Join(", ", _entries.Keys.Select(k => k.ToString().ToLowerInvariant()));
        message = $"RepoQL running with degraded services: {services}. Some features may be limited.";
        return true;
    }
}
