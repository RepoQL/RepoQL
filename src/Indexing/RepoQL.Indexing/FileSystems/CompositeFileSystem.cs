using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RepoQL.Contracts;
using RepoQL.FileSystem;
using RepoQL.FileSystem.Abstractions;

namespace RepoQL.Indexing.FileSystems;

/// <summary>
/// Indexing-focused composite over multiple <see cref="IVirtualFileSystem"/> instances. Supports per-mount URI
/// matching so that multiple stores may share the same scheme (e.g., multiple <c>github://</c> repos).
/// <para>
/// The type itself is intentionally stateful but dumb: it knows how to route URIs, enumerate files, and fan-out
/// watchers. Mount lifecycle is coordinated by <see cref="ICompositeFileSystemManager"/>, which owns a single
/// instance of this composite and calls into <see cref="AddOrUpdateMount"/> / <see cref="RemoveMount"/> when the
/// application adds default mounts (repo root, help://) or dynamic imports (e.g., github://&lt;repo&gt;).
/// </para>
/// </summary>
public sealed class CompositeFileSystem : IMultiFileSystem
{
    private readonly object _gate = new();
    private readonly Dictionary<string, MountRegistration> _mountsById = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<MountRegistration> _orderedMounts = new();
    private readonly ILogger<CompositeFileSystem> _logger;

    /// <summary>
    /// Initializes a new composite with the required primary mount and optional additional mounts.
    /// </summary>
    public CompositeFileSystem(
        CompositeFileSystemMount primaryMount,
        IEnumerable<CompositeFileSystemMount>? additionalMounts = null,
        ILogger<CompositeFileSystem>? logger = null)
    {
        _logger = logger ?? NullLogger<CompositeFileSystem>.Instance;
        ArgumentNullException.ThrowIfNull(primaryMount);
        if (!primaryMount.IsPrimary)
            throw new ArgumentException("Primary mount must have IsPrimary = true.", nameof(primaryMount));

        AddOrUpdateMountInternal(primaryMount);

        if (additionalMounts is null)
            return;

        foreach (var mount in additionalMounts)
        {
            AddOrUpdateMountInternal(mount);
        }
    }

    /// <summary>
    /// Adds or replaces a mount. Called by the manager whenever the application wires in a new virtual filesystem
    /// (docs, imports, embedded fixtures, etc.) so enumeration/watching immediately reflect the latest view.
    /// </summary>
    public void AddOrUpdateMount(CompositeFileSystemMount mount)
    {
        ArgumentNullException.ThrowIfNull(mount);
        AddOrUpdateMountInternal(mount);
    }

    /// <summary>
    /// Removes a mount by id. Primary mounts cannot be removed because the engine must always have a default
    /// repository file system to fall back to.
    /// </summary>
    public bool RemoveMount(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return false;

        lock (_gate)
        {
            if (!_mountsById.TryGetValue(id, out var registration))
                return false;

            if (registration.IsPrimary)
                throw new InvalidOperationException("The primary mount cannot be removed.");

            _mountsById.Remove(id);
            _orderedMounts.Remove(registration);
            return true;
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<EnumeratedResource> EnumerateAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var snapshot = GetMountSnapshot();
        foreach (var mount in snapshot)
        {
            if (!mount.IncludeInEnumeration)
                continue;

            _logger.LogTrace("Enumerating files for mount {MountId}", mount.Id);
            await foreach (var file in mount.FileSystem.EnumerateAsync(ct).ConfigureAwait(false))
            {
                if (ct.IsCancellationRequested)
                    yield break;

                var uri = mount.FileSystem.GetUri(file);
                var decorated = DecorateFile(file, mount);
                yield return new EnumeratedResource(decorated, uri);
            }
        }
    }

    /// <inheritdoc />
    public IFileInfo GetFile(RepoUri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        lock (_gate)
        {
            foreach (var mount in _orderedMounts)
            {
                if (!mount.Matches(uri))
                    continue;

                var file = mount.FileSystem.GetFile(uri);
                return DecorateFile(file, mount);
            }
        }

        throw new NotSupportedException($"No mounted file system can handle URI '{uri}'.");
    }

    /// <inheritdoc />
    public IFileSystemWatcher WatchAll() => new CompositeWatcher(this);

    private IFileInfo DecorateFile(IFileInfo file, MountRegistration mount)
    {
        if (mount.EnableAnalysis)
            return file;

        if (file is IFileAnalysisMetadata meta && meta.IsReadOnly)
            return file;

        return new MountAnnotatedFileInfo(file, isReadOnly: true);
    }

    private void AddOrUpdateMountInternal(CompositeFileSystemMount mount)
    {
        if (string.IsNullOrWhiteSpace(mount.Id))
            throw new ArgumentException("Mount id is required.", nameof(mount));
        ArgumentNullException.ThrowIfNull(mount.FileSystem);

        var predicate = mount.UriPredicate ?? (uri => string.Equals(uri.Scheme, mount.FileSystem.Scheme, StringComparison.OrdinalIgnoreCase));

        lock (_gate)
        {
            if (mount.IsPrimary)
            {
                if (_orderedMounts.Any(m => m.IsPrimary))
                {
                    _logger.LogInformation("Replacing primary mount '{MountId}'.", mount.Id);
                    var existingPrimary = _orderedMounts.First(m => m.IsPrimary);
                    _mountsById.Remove(existingPrimary.Id);
                    _orderedMounts.Remove(existingPrimary);
                }
            }

            if (_mountsById.TryGetValue(mount.Id, out var existing))
            {
                _orderedMounts.Remove(existing);
                _mountsById.Remove(mount.Id);
            }

            var registration = new MountRegistration(
                mount.Id,
                mount.FileSystem,
                predicate,
                mount.IncludeInEnumeration,
                mount.IsPrimary,
                mount.EnableWatching,
                mount.EnableAnalysis);
            _mountsById[mount.Id] = registration;

            if (registration.IsPrimary)
            {
                _orderedMounts.Add(registration);
            }
            else
            {
                _orderedMounts.Insert(0, registration);
            }

            _logger.LogDebug("Registered mount {MountId} (scheme={Scheme}, primary={IsPrimary}).",
                mount.Id, mount.FileSystem.Scheme, mount.IsPrimary);
        }
    }

    /// <summary>
    /// Attempts to map the provided <see cref="RepoUri"/> to a mounted <see cref="IVirtualFileSystem"/>.
    /// </summary>
    public bool TryResolve(RepoUri uri, [MaybeNullWhen(false)] out IVirtualFileSystem fileSystem)
    {
        ArgumentNullException.ThrowIfNull(uri);

        lock (_gate)
        {
            foreach (var mount in _orderedMounts)
            {
                if (!mount.Matches(uri))
                    continue;

                fileSystem = mount.FileSystem;
                return true;
            }
        }

        fileSystem = null;
        return false;
    }

    public IVirtualFileSystem Resolve(RepoUri uri)
        => TryResolve(uri, out var store)
            ? store
            : throw new NotSupportedException($"No mounted file system can handle URI '{uri}'.");

    private List<MountRegistration> GetMountSnapshot()
    {
        lock (_gate)
        {
            return _orderedMounts.ToList();
        }
    }

    /// <summary>
    /// Returns a snapshot of mount information for diagnostics/logging.
    /// </summary>
    public IReadOnlyList<(string Id, string Scheme, bool IncludeInEnumeration)> GetMounts()
    {
        lock (_gate)
        {
            return _orderedMounts
                .Select(m => (m.Id, m.FileSystem.Scheme, m.IncludeInEnumeration))
                .ToList();
        }
    }

    /// <summary>Immutable snapshot describing a registered mount.</summary>
    private sealed record MountRegistration(
        string Id,
        IVirtualFileSystem FileSystem,
        Func<RepoUri, bool> Matcher,
        bool IncludeInEnumeration,
        bool IsPrimary,
        bool EnableWatching,
        bool EnableAnalysis)
    {
        public bool Matches(RepoUri uri) => Matcher(uri);
    }

    /// <summary>
    /// Fan-out watcher that subscribes to each mounted file system and forwards change notifications up to the host.
    /// </summary>
    private sealed class CompositeWatcher : FileSystemWatcherBase
    {
        private readonly CompositeFileSystem _owner;
        private readonly List<(IFileSystemWatcher watcher, IDisposable subscription)> _subscriptions = new();

        public CompositeWatcher(CompositeFileSystem owner)
        {
            _owner = owner;
        }

        protected override async Task OnStartAsync(CancellationToken cancellationToken)
        {
            var mounts = _owner.GetMountSnapshot();
            foreach (var mount in mounts)
            {
                if (!mount.EnableWatching)
                    continue;

                var watcher = mount.FileSystem.Watch();
                var subscription = watcher.Subscribe(new Forwarder(this, mount));
                _subscriptions.Add((watcher, subscription));
                await watcher.StartAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        protected override async Task OnStopAsync(CancellationToken cancellationToken)
        {
            foreach (var (watcher, _) in _subscriptions)
            {
                try
                {
                    await watcher.StopAsync(cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    // swallowing to keep watcher teardown resilient
                }
            }
        }

        protected override async ValueTask OnDisposeAsync()
        {
            foreach (var (watcher, subscription) in _subscriptions)
            {
                try { subscription.Dispose(); } catch { /* ignore */ }
                try { await watcher.DisposeAsync().ConfigureAwait(false); } catch { /* ignore */ }
            }
            _subscriptions.Clear();
        }

        /// <summary>Simple observer bridge that relays mount-specific watcher events to the composite watcher.</summary>
        private sealed class Forwarder(CompositeWatcher owner, MountRegistration mount) : IObserver<ResourceChange>
        {
            public void OnCompleted() { }

            public void OnError(Exception error)
            {
                owner.RaiseError(error);
            }

            public void OnNext(ResourceChange value)
            {
                var decorated = owner._owner.DecorateFile(value.File, mount);
                owner.SafeRaiseChange(value.Kind, decorated, value.CurrentUri, value.PreviousUri);
            }
        }
    }

    private sealed class MountAnnotatedFileInfo : IFileInfo, IFileAnalysisMetadata
    {
        private readonly IFileInfo _inner;

        public MountAnnotatedFileInfo(IFileInfo inner, bool isReadOnly)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            IsReadOnly = isReadOnly;
        }

        public bool IsReadOnly { get; }

        public Stream CreateReadStream() => _inner.CreateReadStream();
        public bool Exists => _inner.Exists;
        public long Length => _inner.Length;
        public string? PhysicalPath => _inner.PhysicalPath;
        public string Name => _inner.Name;
        public DateTimeOffset LastModified => _inner.LastModified;
        public bool IsDirectory => _inner.IsDirectory;
    }
}
