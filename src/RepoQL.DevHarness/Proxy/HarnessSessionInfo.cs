namespace RepoQL.DevHarness.Proxy;

/// <summary>
/// Purpose: Captures per-session identity and timing for the harness lifecycle.
/// Complexity: Simple record to centralize session metadata.
/// </summary>
internal sealed record HarnessSessionInfo(string SessionId, DateTimeOffset StartedAt);
