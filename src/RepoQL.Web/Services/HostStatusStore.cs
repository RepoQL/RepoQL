using RepoQL.Contracts;

namespace RepoQL.Web.Services;

/// <summary>
/// Holds the latest status of the RepoQL host for UI consumption.
/// Receives real-time updates from the status stream.
/// </summary>
internal sealed class HostStatusStore
{
    private readonly object _gate = new();
    private HostStatusSnapshot _snapshot = HostStatusSnapshot.Offline("Waiting for connection...");
    private PipelineStatusEvent? _pipelineStatus;
    private StatsSnapshotEvent? _stats;
    private readonly List<HealthEvent> _healthEvents = new();

    public event EventHandler<HostStatusSnapshot>? SnapshotChanged;
    public event EventHandler<PipelineStatusEvent>? PipelineStatusChanged;
    public event EventHandler<StatsSnapshotEvent>? StatsChanged;
    public event EventHandler<HealthEvent>? HealthEventAdded;

    public HostStatusSnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                return _snapshot;
            }
        }
    }

    public PipelineStatusEvent? PipelineStatus
    {
        get
        {
            lock (_gate)
            {
                return _pipelineStatus;
            }
        }
    }

    public StatsSnapshotEvent? Stats
    {
        get
        {
            lock (_gate)
            {
                return _stats;
            }
        }
    }

    public IReadOnlyList<HealthEvent> HealthEvents
    {
        get
        {
            lock (_gate)
            {
                return _healthEvents.ToList();
            }
        }
    }

    public void SetSnapshot(HostStatusSnapshot snapshot)
    {
        HostStatusSnapshot current;
        lock (_gate)
        {
            _snapshot = snapshot with { UpdatedAt = DateTimeOffset.UtcNow };
            current = _snapshot;
        }
        SnapshotChanged?.Invoke(this, current);
    }

    public void SetPipelineStatus(PipelineStatusEvent status)
    {
        lock (_gate)
        {
            _pipelineStatus = status;
        }
        PipelineStatusChanged?.Invoke(this, status);
    }

    public void AddHealthEvent(HealthEvent health)
    {
        lock (_gate)
        {
            _healthEvents.Add(health);
            // Keep only recent health events
            if (_healthEvents.Count > 20)
            {
                _healthEvents.RemoveAt(0);
            }
        }
        HealthEventAdded?.Invoke(this, health);
    }

    public void SetStats(StatsSnapshotEvent stats)
    {
        lock (_gate)
        {
            _stats = stats;
        }
        StatsChanged?.Invoke(this, stats);
    }
}
