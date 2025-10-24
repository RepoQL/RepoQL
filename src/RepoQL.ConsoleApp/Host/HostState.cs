namespace RepoQL.ConsoleApp.Host;

internal sealed class HostState
{
    public required string RepositoryPath { get; init; }
    public required bool ImplicitStart { get; init; }
    public required DateTime StartedAtUtc { get; init; }
}