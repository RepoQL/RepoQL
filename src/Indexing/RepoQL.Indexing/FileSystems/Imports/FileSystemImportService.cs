using Microsoft.Extensions.Logging;
using RepoQL.Contracts;
using RepoQL.FileSystem.Abstractions;

namespace RepoQL.Indexing.FileSystems.Imports;

/// <summary>
/// Facade used by CLI/agents to import additional filesystems (e.g., cloning a GitHub repo) and register them with
/// the live <see cref="ICompositeFileSystemManager"/>.
/// </summary>
public interface IFileSystemImportService
{
    /// <summary>
    /// Imports the specified source and returns the mount descriptor that was registered with the manager,
    /// plus any associated indexing operation.
    /// </summary>
    Task<FileSystemImportResult> ImportAsync(RepoUri source, CancellationToken cancellationToken = default);
}

/// <summary>
/// Result of an import request.
/// <para><b>Purpose:</b> Return both the registered mount and the optional indexing operation.</para>
/// <para><b>Complexity:</b> Simple data carrier for import outcomes.</para>
/// </summary>
public sealed record FileSystemImportResult(CompositeFileSystemMount Mount, IOperation? Operation);

/// <summary>Default implementation that locates an <see cref="IVirtualFileSystemImporter"/> and registers the mount.</summary>
public sealed class FileSystemImportService : IFileSystemImportService
{
    private readonly IEnumerable<IVirtualFileSystemImporter> _importers;
    private readonly ICompositeFileSystemManager _mountManager;
    private readonly UriRegistry? _uriRegistry;
    private readonly IOperationManager? _operationManager;
    private readonly IUriFilter? _filter;
    private readonly ILogger<FileSystemImportService>? _logger;

    public FileSystemImportService(
        IEnumerable<IVirtualFileSystemImporter> importers,
        ICompositeFileSystemManager mountManager,
        ILogger<FileSystemImportService>? logger = null,
        UriRegistry? uriRegistry = null,
        IOperationManager? operationManager = null,
        IUriFilter? filter = null)
    {
        _importers = importers ?? throw new ArgumentNullException(nameof(importers));
        _mountManager = mountManager ?? throw new ArgumentNullException(nameof(mountManager));
        _logger = logger;
        _uriRegistry = uriRegistry;
        _operationManager = operationManager;
        _filter = filter;
    }

    public async Task<FileSystemImportResult> ImportAsync(RepoUri source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        var importer = _importers.FirstOrDefault(i => i.CanHandle(source));
        if (importer is null)
            throw new InvalidOperationException($"No importer registered for URI '{source}'.");

        var mount = await importer.ImportAsync(source, cancellationToken).ConfigureAwait(false);
        var operation = await CreateImportOperationAsync(source, mount, cancellationToken).ConfigureAwait(false);
        _mountManager.AddOrUpdateMount(mount);
        return new FileSystemImportResult(mount, operation);
    }

    private async Task<IOperation?> CreateImportOperationAsync(
        RepoUri source,
        CompositeFileSystemMount mount,
        CancellationToken cancellationToken)
    {
        if (_uriRegistry is null || _operationManager is null)
            return null;

        var scope = new List<RepoUri>();

        try
        {
            await foreach (var file in mount.FileSystem.EnumerateAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!file.Exists)
                    continue;

                var uri = mount.FileSystem.GetUri(file);
                if (_filter is not null && !_filter.IncludeFile(uri))
                    continue;

                _uriRegistry.TryRegisterDiscovered(uri);
                scope.Add(uri);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to create import operation for {Uri}", source);
            return null;
        }

        return _operationManager.CreateOperation($"import: {source}", scope);
    }
}
