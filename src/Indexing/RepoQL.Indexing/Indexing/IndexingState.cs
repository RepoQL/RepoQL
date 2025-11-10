namespace RepoQL.Indexing.Indexing;

[Flags]
public enum IndexingState
{
    Started               = 0b0000_0100_0000_0000,
    ClassificationIdle      = 0b0000_0000_0000_0001,
    ParsingIdle             = 0b0000_0000_0000_0010,
    SingleFileAnalysisIdle  = 0b0000_0000_0000_0100,
    MultiFileAnalysisIdle   = 0b0000_0000_0000_1000,
    IndexRebuildIdle        = 0b0000_0000_0001_0000,
    ClassificationBusy      = 0b1000_0000_0000_0000,
    ParsingBusy             = 0b0100_0000_0000_0000,
    SingleFileAnalysisBusy  = 0b0010_0000_0000_0000,
    MultiFileAnalysisBusy   = 0b0001_0000_0000_0000,
    IndexRebuildBusy        = 0b0000_1000_0000_0000,
    AllIdle =  ClassificationIdle | ParsingIdle | SingleFileAnalysisIdle | MultiFileAnalysisIdle | IndexRebuildIdle
}
