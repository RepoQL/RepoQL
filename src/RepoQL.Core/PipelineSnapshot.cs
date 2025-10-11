using System.Diagnostics.CodeAnalysis;

namespace RepoQL.Core;

[Flags]
public enum PipelineStage
{
    None = 0,
    Discovery = 1 << 0,
    Parsing = 1 << 1,
    Analysis = 1 << 2,
    Writer = 1 << 3,
    All = Discovery | Parsing | Analysis,
    Ready = Discovery | Parsing | Analysis | Writer
}

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

public sealed record PipelineSnapshot(
    DateTimeOffset CapturedAt,
    PipelineStageSnapshot Discovery,
    PipelineStageSnapshot Parsing,
    PipelineStageSnapshot Analysis,
    PipelineStageSnapshot? Writer,
    bool IsReindexing)
{
    public bool AllStagesIdle => Discovery.IsIdle && Parsing.IsIdle && Analysis.IsIdle;
    public bool WriterPending => Writer is { Depth: > 0 };
    public bool Ready => AllStagesIdle && !WriterPending;

    public bool TryGetStage(PipelineStage stage, [MaybeNullWhen(false)] out PipelineStageSnapshot snapshot)
    {
        snapshot = stage switch
        {
            PipelineStage.Discovery => Discovery,
            PipelineStage.Parsing => Parsing,
            PipelineStage.Analysis => Analysis,
            PipelineStage.Writer => Writer,
            _ => null
        };
        return snapshot is not null;
    }
}
