namespace RepoQL.Contracts.Data;

public sealed record WriterStatus
{
    public int PendingCount { get; init; }
    public long TotalProcessed { get; init; }
}