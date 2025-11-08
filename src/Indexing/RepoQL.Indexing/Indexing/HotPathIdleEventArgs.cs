using System;

namespace RepoQL.Indexing.Indexing;

public sealed class HotPathIdleEventArgs : EventArgs
{
    public HotPathIdleEventArgs(long epoch)
    {
        Epoch = epoch;
    }

    public long Epoch { get; }
}
