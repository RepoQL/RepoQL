namespace RepoQL.Core;

public sealed record PipelineStageSnapshot(
    PipelineStage Stage,
    int Depth,
    int Capacity,
    long Scheduled,
    long Completed)
{
    public bool IsIdle => Depth <= 0;
    public long Outstanding => Scheduled - Completed;
}