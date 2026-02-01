using RepoQL.FileSystem.Abstractions;

namespace RepoQL.Indexing.FileSystems;

/// <summary>
/// High-level orchestrator for the composite filesystem. It owns the single <see cref="CompositeFileSystem"/> instance
/// used by the host/indexing engine, exposes the current mount set, and provides change notifications so other
/// services (RepoqlHost, telemetry) can respond when mounts are added or removed.
/// </summary>
public interface ICompositeFileSystemManager
{
    /// <summary>The live <see cref="CompositeFileSystem"/> instance shared by the application.</summary>
    CompositeFileSystem FileSystem { get; }

    /// <summary>Returns a snapshot of all registered mounts (primary, help://, imports, etc.).</summary>
    IReadOnlyCollection<CompositeFileSystemMount> GetMounts();

    /// <summary>Attempts to retrieve a mount descriptor by id.</summary>
    bool TryGetMount(string id, out CompositeFileSystemMount mount);

    /// <summary>Adds or replaces a mount, updating the underlying composite immediately.</summary>
    void AddOrUpdateMount(CompositeFileSystemMount mount);

    /// <summary>Removes a non-primary mount. Returns <c>false</c> when the mount id was unknown.</summary>
    bool RemoveMount(string id);

    /// <summary>Raised whenever a mount is added, updated, or removed.</summary>
    event EventHandler<CompositeFileSystemMountChangedEventArgs>? MountsChanged;
}

/// <summary>Event payload describing a mount change.</summary>
public sealed class CompositeFileSystemMountChangedEventArgs : EventArgs
{
    public CompositeFileSystemMountChangedEventArgs(MountChangeKind kind, CompositeFileSystemMount mount)
    {
        Kind = kind;
        Mount = mount;
    }

    public MountChangeKind Kind { get; }

    public CompositeFileSystemMount Mount { get; }
}

/// <summary>Enumerates the kinds of mount transitions the manager can emit.</summary>
public enum MountChangeKind
{
    Added,
    Updated,
    Removed
}
