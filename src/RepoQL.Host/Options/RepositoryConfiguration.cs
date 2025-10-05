namespace RepoQL.Host.Options;

public record RepositoryConfiguration
{
    public required string Path { get; init; }
}