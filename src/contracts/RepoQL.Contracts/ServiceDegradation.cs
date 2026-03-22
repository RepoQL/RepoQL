using System.Collections.Generic;

namespace RepoQL.Contracts;

/// <summary>
/// Purpose: Enumerate degradable host services for diagnostics and health reporting.
/// Complexity: Simple taxonomy used to keep degradation reporting consistent.
/// </summary>
public enum ServiceDegradationKind
{
    Embeddings,
    Mcp,
    Mounts,
    Indexer,
    Watcher
}

/// <summary>
/// Purpose: Capture a single degradation event for reporting.
/// Complexity: Stores a service identifier and a human-readable message.
/// </summary>
public sealed record ServiceDegradationEntry(ServiceDegradationKind Kind, string Message);

/// <summary>
/// Purpose: Allow host services to report degradations without depending on host-specific types.
/// Complexity: Keeps callers decoupled while enabling sticky degradation tracking.
/// </summary>
public interface IServiceDegradationTracker
{
    IReadOnlyList<ServiceDegradationEntry> Entries { get; }

    void MarkDegraded(ServiceDegradationKind kind, string message);
}
