using RepoQL.Indexing.Indexing;

namespace RepoQL.Indexing.Extensions;

public static class IndexingStateExtensions
{
    public static void EnsureValid(this IndexingState state)
    {
        if (state.HasFlag(IndexingState.ClassificationIdle) && state.HasFlag(IndexingState.ClassificationBusy))
            throw new InvalidOperationException("Classification cannot be both idle and busy");
        if (state.HasFlag(IndexingState.ParsingIdle) && state.HasFlag(IndexingState.ParsingBusy))
            throw new InvalidOperationException("Parsing cannot be both idle and busy");
        if (state.HasFlag(IndexingState.SingleFileAnalysisIdle) && state.HasFlag(IndexingState.SingleFileAnalysisBusy))
            throw new InvalidOperationException("Single file analysis cannot be both idle and busy");
        if (state.HasFlag(IndexingState.MultiFileAnalysisIdle) && state.HasFlag(IndexingState.MultiFileAnalysisBusy))
            throw new InvalidOperationException("Multi file analysis cannot be both idle and busy");
        if (state.HasFlag(IndexingState.IndexRebuildIdle) && state.HasFlag(IndexingState.IndexRebuildBusy))
            throw new InvalidOperationException("Index rebuild cannot be both idle and busy");
    }
}