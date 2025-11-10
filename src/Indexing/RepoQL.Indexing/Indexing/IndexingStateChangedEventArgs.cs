namespace RepoQL.Indexing.Indexing;

public sealed class IndexingStateChangedEventArgs : EventArgs
{
    public IndexingStateChangedEventArgs(IndexingState oldState, IndexingState newState)
    {
        OldState = oldState;
        NewState = newState;
    }

    public IndexingState OldState { get; }
    public IndexingState NewState { get; }
}
