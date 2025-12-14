using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RepoQL.Data.DuckDB;
using RepoQL.FileSystem.Physical;
using RepoQL.Indexing.FileSystems;

namespace RepoQL.ConsoleApp.Host;

/// <summary>
/// Restores persisted file system mounts from the database on startup.
/// This ensures imported repositories survive server restarts.
/// </summary>
internal sealed class MountRestorationService : IHostedService
{
    private readonly DuckDbDataStore _db;
    private readonly ICompositeFileSystemManager _mountManager;
    private readonly ILogger<MountRestorationService> _logger;

    public MountRestorationService(
        DuckDbDataStore db,
        ICompositeFileSystemManager mountManager,
        ILogger<MountRestorationService>? logger = null)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _mountManager = mountManager ?? throw new ArgumentNullException(nameof(mountManager));
        _logger = logger ?? NullLogger<MountRestorationService>.Instance;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        RestorePersistedMounts();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private void RestorePersistedMounts()
    {
        var mounts = _db.GetAllMounts();
        if (mounts.Count == 0)
        {
            _logger.LogDebug("No persisted mounts to restore");
            return;
        }

        _logger.LogInformation("Restoring {Count} persisted mount(s)", mounts.Count);

        foreach (var record in mounts)
        {
            try
            {
                if (!Directory.Exists(record.LocalPath))
                {
                    _logger.LogWarning("Mount {Id} local path missing at {Path}, removing from database",
                        record.Id, record.LocalPath);
                    _db.DeleteMount(record.Id);
                    continue;
                }

                var fs = new PhysicalFileSystem(
                    record.LocalPath,
                    scheme: record.Scheme,
                    uriPrefix: record.PathPrefix,
                    authority: record.Authority);

                var mount = CompositeFileSystemMount.ForScheme(
                    id: record.Id,
                    fileSystem: fs,
                    scheme: record.Scheme,
                    authority: record.Authority,
                    pathPrefix: record.PathPrefix,
                    includeInEnumeration: record.IncludeInEnumeration,
                    enableWatching: record.EnableWatching,
                    enableAnalysis: record.EnableAnalysis);

                _mountManager.AddOrUpdateMount(mount);
                _logger.LogInformation("Restored mount {Id} from {Path}", record.Id, record.LocalPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to restore mount {Id}", record.Id);
            }
        }
    }
}
