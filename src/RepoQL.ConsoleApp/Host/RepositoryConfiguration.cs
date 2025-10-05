namespace RepoQL.App.Host;

public record RepositoryConfiguration
{
    public required string Path { get; init; }
}

