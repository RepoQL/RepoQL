using RepoQL.Contracts;

namespace RepoQL.Indexing.FileSystems.Imports;

/// <summary>
/// Facade used by CLI/agents to import additional filesystems (e.g., cloning a GitHub repo) and register them with
/// the live <see cref="ICompositeFileSystemManager"/>.
/// </summary>
public interface IFileSystemImportService
{
    /// <summary>
    /// Imports the specified source and returns the mount descriptor that was registered with the manager.
    /// </summary>
    Task<CompositeFileSystemMount> ImportAsync(RepoUri source, CancellationToken cancellationToken = default);
}

/// <summary>Default implementation that locates an <see cref="IVirtualFileSystemImporter"/> and registers the mount.</summary>
public sealed class FileSystemImportService : IFileSystemImportService
{
    private readonly IEnumerable<IVirtualFileSystemImporter> _importers;
    private readonly ICompositeFileSystemManager _mountManager;

    public FileSystemImportService(
        IEnumerable<IVirtualFileSystemImporter> importers,
        ICompositeFileSystemManager mountManager)
    {
        _importers = importers ?? throw new ArgumentNullException(nameof(importers));
        _mountManager = mountManager ?? throw new ArgumentNullException(nameof(mountManager));
    }

    public async Task<CompositeFileSystemMount> ImportAsync(RepoUri source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        var importer = _importers.FirstOrDefault(i => i.CanHandle(source));
        if (importer is null)
            throw new InvalidOperationException($"No importer registered for URI '{source}'.");

        var mount = await importer.ImportAsync(source, cancellationToken).ConfigureAwait(false);
        _mountManager.AddOrUpdateMount(mount);
        return mount;
    }
}
