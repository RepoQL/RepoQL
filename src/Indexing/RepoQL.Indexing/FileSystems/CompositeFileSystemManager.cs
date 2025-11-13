using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RepoQL.FileSystem.Physical;

namespace RepoQL.Indexing.FileSystems;

/// <summary>
/// Default implementation of <see cref="ICompositeFileSystemManager"/>. It bootstrap-mounts the physical repository
/// plus any statically registered mounts (docs://, embedded fixtures) and exposes runtime APIs so services can import
/// additional read-only file systems. Every host resolves this manager once and uses its <see cref="FileSystem"/>
/// facade everywhere.
/// </summary>
public sealed class CompositeFileSystemManager : ICompositeFileSystemManager
{
    private readonly CompositeFileSystem _composite;
    private readonly Dictionary<string, CompositeFileSystemMount> _mounts;
    private readonly ILogger<CompositeFileSystemManager> _logger;

    /// <summary>
    /// Creates a new manager rooted at the repository's physical file system. The primary mount is always installed
    /// and additional mounts provided by DI (docs, tests) are layered on top in registration order.
    /// </summary>
    public CompositeFileSystemManager(
        PhysicalFileSystem primaryFileSystem,
        IEnumerable<CompositeFileSystemMount>? initialMounts = null,
        ILogger<CompositeFileSystemManager>? logger = null,
        ILogger<CompositeFileSystem>? compositeLogger = null)
    {
        ArgumentNullException.ThrowIfNull(primaryFileSystem);
        _logger = logger ?? NullLogger<CompositeFileSystemManager>.Instance;

        var primaryMount = CompositeFileSystemMount.CreatePrimary(primaryFileSystem, "primary");
        var additional = (initialMounts ?? Enumerable.Empty<CompositeFileSystemMount>())
            .Where(m => !m.IsPrimary)
            .GroupBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Last())
            .ToList();

        _composite = new CompositeFileSystem(primaryMount, additional, compositeLogger);
        _mounts = new Dictionary<string, CompositeFileSystemMount>(StringComparer.OrdinalIgnoreCase)
        {
            [primaryMount.Id] = primaryMount
        };

        foreach (var mount in additional)
        {
            _mounts[mount.Id] = mount;
        }
    }

    public CompositeFileSystem FileSystem => _composite;

    public event EventHandler<CompositeFileSystemMountChangedEventArgs>? MountsChanged;

    public IReadOnlyCollection<CompositeFileSystemMount> GetMounts() => _mounts.Values.ToArray();

    public bool TryGetMount(string id, out CompositeFileSystemMount mount)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            mount = null!;
            return false;
        }

        return _mounts.TryGetValue(id, out mount!);
    }

    /// <summary>
    /// Adds or replaces a mount. This immediately updates the underlying <see cref="CompositeFileSystem"/> so future
    /// enumerations, watchers, and URI resolutions see the new store.
    /// </summary>
    public void AddOrUpdateMount(CompositeFileSystemMount mount)
    {
        ArgumentNullException.ThrowIfNull(mount);
        if (string.IsNullOrWhiteSpace(mount.Id))
            throw new ArgumentException("Mount id is required.", nameof(mount));

        if (mount.IsPrimary)
            throw new InvalidOperationException("Only one primary mount is supported.");

        _composite.AddOrUpdateMount(mount);
        var changeKind = _mounts.ContainsKey(mount.Id) ? MountChangeKind.Updated : MountChangeKind.Added;
        _mounts[mount.Id] = mount;
        _logger.LogInformation("Registered mount {MountId} (scheme={Scheme})", mount.Id, mount.FileSystem.Scheme);
        MountsChanged?.Invoke(this, new CompositeFileSystemMountChangedEventArgs(changeKind, mount));
    }

    /// <summary>
    /// Removes a non-primary mount. Returns <c>false</c> if the specified id does not exist.
    /// </summary>
    public bool RemoveMount(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return false;

        if (!_mounts.TryGetValue(id, out var existing))
            return false;

        if (existing.IsPrimary)
            throw new InvalidOperationException("Cannot remove the primary mount.");

        var removed = _composite.RemoveMount(id);
        if (removed)
        {
            _mounts.Remove(id);
            MountsChanged?.Invoke(this, new CompositeFileSystemMountChangedEventArgs(MountChangeKind.Removed, existing));
            _logger.LogInformation("Removed mount {MountId}", id);
        }

        return removed;
    }
}
