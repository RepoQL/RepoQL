namespace RepoQL.Web.Services;

/// <summary>
/// Holds the latest health snapshot of the RepoQL host for UI consumption.
/// </summary>
public sealed class HostStatusStore
{
    private readonly object _gate = new();
    private HostStatusSnapshot _snapshot = HostStatusSnapshot.Offline("Waiting for first poll");

    public event Action<HostStatusSnapshot>? SnapshotChanged;

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

    public void SetSnapshot(HostStatusSnapshot snapshot)
    {
        HostStatusSnapshot current;
        lock (_gate)
        {
            _snapshot = snapshot with { UpdatedAt = DateTimeOffset.UtcNow };
            current = _snapshot;
        }
        SnapshotChanged?.Invoke(current);
    }
}