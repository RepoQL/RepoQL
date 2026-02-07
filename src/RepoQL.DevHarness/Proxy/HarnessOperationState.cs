using System.Globalization;

namespace RepoQL.DevHarness.Proxy;

/// <summary>
/// Purpose: Define lifecycle operations that block concurrent tool execution.
/// Complexity: Small enum aligned with harness state expectations.
/// </summary>
internal enum HarnessOperationKind
{
    None,
    Building,
    Deploying,
    Restarting
}

/// <summary>
/// Purpose: Snapshot the currently running harness operation.
/// Complexity: Immutable record with helpers for JSON payload construction.
/// </summary>
internal sealed record HarnessOperationSnapshot(HarnessOperationKind Operation, DateTimeOffset? StartedAt)
{
    public bool IsInProgress => Operation != HarnessOperationKind.None;

    public string? OperationName => Operation switch
    {
        HarnessOperationKind.Building => "building",
        HarnessOperationKind.Deploying => "deploying",
        HarnessOperationKind.Restarting => "restarting",
        _ => null
    };

    public string? DisplayName => Operation switch
    {
        HarnessOperationKind.Building => "Build",
        HarnessOperationKind.Deploying => "Deploy",
        HarnessOperationKind.Restarting => "Restart",
        _ => null
    };
}

/// <summary>
/// Purpose: Provide a shared operation state tracker for the proxy and tool router.
/// Complexity: Thread-safe state transitions with minimal surface area.
/// </summary>
internal interface IHarnessOperationState
{
    HarnessOperationSnapshot GetSnapshot();
    bool TryBegin(HarnessOperationKind kind, DateTimeOffset startedAt);
    void Complete(HarnessOperationKind kind);
}

/// <summary>
/// Purpose: Track build/deploy operations so the harness can gate tool calls.
/// Complexity: Simple lock-protected state to avoid concurrent operations.
/// </summary>
internal sealed class HarnessOperationState : IHarnessOperationState
{
    private readonly object _sync = new();
    private HarnessOperationSnapshot _snapshot = new(HarnessOperationKind.None, null);

    public HarnessOperationSnapshot GetSnapshot()
    {
        lock (_sync)
        {
            return _snapshot;
        }
    }

    public bool TryBegin(HarnessOperationKind kind, DateTimeOffset startedAt)
    {
        lock (_sync)
        {
            if (_snapshot.IsInProgress)
                return false;

            _snapshot = new HarnessOperationSnapshot(kind, startedAt);
            return true;
        }
    }

    public void Complete(HarnessOperationKind kind)
    {
        lock (_sync)
        {
            if (_snapshot.Operation != kind)
                return;

            _snapshot = new HarnessOperationSnapshot(HarnessOperationKind.None, null);
        }
    }
}

/// <summary>
/// Purpose: Format UTC timestamps for harness payloads.
/// Complexity: Shared helper to keep formatting consistent across responses.
/// </summary>
internal static class HarnessTimestampFormatter
{
    public static string Format(DateTimeOffset timestamp)
        => timestamp.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}
