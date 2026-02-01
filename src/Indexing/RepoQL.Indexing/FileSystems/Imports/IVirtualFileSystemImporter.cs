using RepoQL.Contracts;

namespace RepoQL.Indexing.FileSystems.Imports;

/// <summary>
/// Strategy interface for translating an arbitrary URI (github://, help://, future providers) into a mounted
/// <see cref="CompositeFileSystemMount"/>. Importers resolve/download the backing content and hand the manager a mount
/// that the engine can consume immediately.
/// </summary>
public interface IVirtualFileSystemImporter
{
    /// <summary>Returns true when the importer understands the provided source URI.</summary>
    bool CanHandle(RepoUri source);

    /// <summary>
    /// Imports the specified source and returns a mount descriptor. Callers are responsible for registering the mount
    /// with <see cref="ICompositeFileSystemManager"/>.
    /// </summary>
    Task<CompositeFileSystemMount> ImportAsync(RepoUri source, CancellationToken cancellationToken);
}
