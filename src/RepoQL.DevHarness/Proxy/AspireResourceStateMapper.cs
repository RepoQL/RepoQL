namespace RepoQL.DevHarness.Proxy;

/// <summary>
/// Purpose: Translate Aspire resource states into harness host states.
/// Complexity: Deterministic mapping with explicit fallback to unknown.
/// </summary>
internal static class AspireResourceStateMapper
{
    public static string MapToHostState(string? resourceState)
    {
        if (string.IsNullOrWhiteSpace(resourceState))
            return HostState.Unknown;

        if (string.Equals(resourceState, "Running", StringComparison.OrdinalIgnoreCase))
            return HostState.Ready;

        if (string.Equals(resourceState, "Stopped", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(resourceState, "Exited", StringComparison.OrdinalIgnoreCase))
            return HostState.Stopped;

        return HostState.Unknown;
    }
}
