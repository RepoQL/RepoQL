namespace RepoQL.DevHarness.Proxy;

/// <summary>
/// Purpose: Standardize host state labels used by the harness.
/// Complexity: Simple constants to avoid string drift across components.
/// </summary>
internal static class HostState
{
    public const string Ready = "ready";
    public const string Stopped = "stopped";
    public const string Unknown = "unknown";
}

/// <summary>
/// Purpose: Capture the latest Aspire-derived host state for fast reads by the proxy.
/// Complexity: Lightweight snapshot record with convenience flags for routing decisions.
/// </summary>
internal sealed record HostStateSnapshot(string State, bool AspireConnected, string? ResourceName, DateTimeOffset UpdatedAt)
{
    public bool IsStopped => string.Equals(State, HostState.Stopped, StringComparison.Ordinal);
}

/// <summary>
/// Purpose: Abstract access to the latest host state snapshot.
/// Complexity: Minimal contract to decouple polling from consumers.
/// </summary>
internal interface IHostStateProvider
{
    HostStateSnapshot GetSnapshot();
}
