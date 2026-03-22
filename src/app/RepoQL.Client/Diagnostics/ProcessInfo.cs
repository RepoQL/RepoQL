namespace RepoQL.Client.Diagnostics;

/// <summary>
/// Purpose: Capture process identity details for diagnostics reports.
/// Complexity: Lightweight snapshot of PID and name without live process handles.
/// </summary>
public sealed record ProcessInfo(int ProcessId, string? ProcessName);
