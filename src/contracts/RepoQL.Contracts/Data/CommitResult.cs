namespace RepoQL.Contracts.Data;

public sealed record CommitResult
{
    public bool Success { get; init; }
    public Exception? Error { get; init; }
}