using Microsoft.Extensions.FileProviders;
using RepoQL.FileSystem.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RepoQL.Contracts;

namespace RepoQL.FileSystem;

/// <summary>
/// Default composite hub over multiple virtual file systems.
/// </summary>
public sealed class MultiFileSystem(IFileSystemRegistry registry, IEnumerable<IVirtualFileSystem> stores, ILogger<MultiFileSystem>? logger = null)
    : IMultiFileSystem
{
    private readonly IFileSystemRegistry _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    private readonly IVirtualFileSystem[] _stores = stores?.ToArray() ?? [];
    private readonly ILogger<MultiFileSystem> _logger = logger ?? NullLogger<MultiFileSystem>.Instance;

    public async IAsyncEnumerable<EnumeratedResource> EnumerateAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        _logger.LogTrace("EnumerateAsync starting over {Count} stores", _stores.Length);
        foreach (var s in _stores)
        {
            await foreach (var file in s.EnumerateAsync(ct))
            {
                if (ct.IsCancellationRequested) yield break;

                RepoUri uri;
                if (string.Equals(s.Scheme, "file", StringComparison.OrdinalIgnoreCase)
                    && s is Physical.PhysicalFileSystem pfs
                    && !string.IsNullOrEmpty(file.PhysicalPath))
                {
                    uri = pfs.ToRepoUri(file.PhysicalPath);
                }
                else
                {
                    // For logical stores (embed/mem/etc.) construct scheme://{physicalPath}
                    var path = file.PhysicalPath ?? file.Name;
                    uri = RepoUri.Parse($"{s.Scheme}://{path}");
                }

                yield return new EnumeratedResource(file, uri);
                await Task.Yield();
            }
        }
    }

    public IFileInfo GetFile(RepoUri uri)
    {
        var store = _registry.Resolve(uri);
        return store.GetFile(uri);
    }

    public IFileSystemWatcher WatchAll()
    {
        return new CompositeWatcher(_stores);
    }

    private sealed class CompositeWatcher(IEnumerable<IVirtualFileSystem> stores) : FileSystemWatcherBase
    {
        private readonly IVirtualFileSystem[] _stores = stores.ToArray();
        private readonly List<(IFileSystemWatcher watcher, IDisposable sub)> _subs = [];

        protected override async Task OnStartAsync(CancellationToken cancellationToken)
        {
            foreach (var s in _stores)
            {
                var w = s.Watch();
                var sub = w.Subscribe(new Forwarder(this));
                _subs.Add((w, sub));
                await w.StartAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        protected override async Task OnStopAsync(CancellationToken cancellationToken)
        {
            foreach (var (w, sub) in _subs.ToArray())
            {
                try { sub.Dispose(); } catch { /* Suppress*/ }
                try { await w.StopAsync(cancellationToken).ConfigureAwait(false); } catch (Exception) {  }
            }
        }

        protected override async ValueTask OnDisposeAsync()
        {
            foreach (var (w, sub) in _subs.ToArray())
            {
                try { sub.Dispose(); } catch (Exception) {  }
                try { await w.DisposeAsync().ConfigureAwait(false); } catch (Exception) {  }
            }
            _subs.Clear();
        }

        private sealed class Forwarder(CompositeWatcher owner) : IObserver<ResourceChange>
        {
            public void OnCompleted() { }
            public void OnError(Exception error)
            {
                owner.RaiseError(error);
            }
            public void OnNext(ResourceChange value)
            {
                owner.SafeRaiseChange(value.Kind, value.File, value.CurrentUri, value.PreviousUri);
            }
        }
    }
}
