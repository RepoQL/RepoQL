using System.Collections.Concurrent;

namespace RepoQL.Core;

/// <summary>
/// Debouncer keyed by TKey. Each Push coalesces rapid updates and fires the action once the window elapses.
/// </summary>
/// <remarks>Create a debouncer with the provided time window.</remarks>
public sealed class KeyedDebouncer<TKey>(TimeSpan window) where TKey : notnull
{
    private readonly ConcurrentDictionary<TKey, (long ver, CancellationTokenSource cts)> _map = new();

    /// <summary>Push a key and schedule <paramref name="action"/> to run after the window unless pushed again.</summary>
    public void Push(TKey key, Action action)
    {
        CancellationTokenSource? toDispose = null;
        var entry = _map.AddOrUpdate(key,
            _ => (1L, new CancellationTokenSource()),
            (_, prev) =>
            {
                try
                {
                    prev.cts.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // Already disposed, ignore
                }
                toDispose = prev.cts;
                return (prev.ver + 1, new CancellationTokenSource());
            });

        // Dispose the old CTS outside of the update function to avoid race conditions
        if (toDispose != null)
        {
            _ = Task.Run(async () =>
            {
                // Give a bit of time for any pending operations
                await Task.Delay(10, CancellationToken.None).ConfigureAwait(false);
                try { toDispose.Dispose(); } catch { }
            }, toDispose.Token);
        }

        var cts = entry.cts;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(window, cts.Token).ConfigureAwait(false);
                _map.TryRemove(key, out _);
                action();
            }
            catch (OperationCanceledException) { }
            finally
            {
                // Delay disposal slightly to avoid race conditions
                await Task.Delay(10).ConfigureAwait(false);
                try { cts.Dispose(); } catch { }
            }
        }, cts.Token);
    }

    /// <summary>Cancel and remove pending debounce for <paramref name="key"/>.</summary>
    public void Clear(TKey key)
    {
        if (!_map.TryRemove(key, out var e)) return;
        try
        {
            if (!e.cts.IsCancellationRequested)
                e.cts.Cancel();
        }
        catch (ObjectDisposedException) { }
        finally
        {
            try { e.cts.Dispose(); } catch { }
        }
    }
}