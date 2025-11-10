using System;
using System.Threading.Tasks;
using RepoQL.Indexing.Indexing;

namespace RepoQL.Testing;

public static class HotPathIdleTestExtensions
{
    public static Task<long> AwaitHotPathIdleAsync(this IndexingEngine engine, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(engine);

        var tcs = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);

        void Handler(object? sender, HotPathIdleEventArgs args)
        {
            engine.HotPathIdle -= Handler;
            tcs.TrySetResult(args.Epoch);
        }

        engine.HotPathIdle += Handler;

        if (cancellationToken.CanBeCanceled)
        {
            cancellationToken.Register(() =>
            {
                engine.HotPathIdle -= Handler;
                tcs.TrySetCanceled(cancellationToken);
            });
        }

        return tcs.Task;
    }
}
