using System.Diagnostics.CodeAnalysis;

namespace RepoQL.Core;

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
