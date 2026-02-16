namespace RepoQL.ConsoleApp.Host;

internal sealed class HostState
{
    public required string RepositoryPath { get; init; }
    public required bool ImplicitStart { get; init; }
    public required DateTime StartedAtUtc { get; init; }
    public string? DashboardUrl { get; set; }
    public bool InitialIndexingCompleted { get; set; }
    public ServiceDegradationState Degradation { get; } = new();
}
