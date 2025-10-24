namespace RepoQL.Contracts.Data;

public sealed record FlushResult
{
    public int OperationsFlushed { get; init; }
}