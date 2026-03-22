using Microsoft.Extensions.Logging;
using RepoQL.Contracts;
using RepoQL.Data.DuckDB;
using RepoQL.FileSystem.Physical;

namespace RepoQL.Indexing.FileSystems.Imports;

/// <summary>
/// Imports local directories and exposes a read-only mount whose URIs take the form
/// <c>local:///absolute/path/file</c>. Much simpler than GitHub importer - no cloning needed.
///
/// <para><b>Purpose:</b> Allows agents to query additional local directories alongside
/// the primary repository, useful for cross-project analysis or reference code.</para>
///
/// <para><b>Complexity:</b> Minimal - validates path exists, creates a PhysicalFileSystem
/// wrapper, and registers the mount. No external tools or network required.</para>
/// </summary>
public sealed class LocalDirectoryImporter : IVirtualFileSystemImporter
{
    private readonly PhysicalFileSystem _primary;
    private readonly DuckDbDataStore _db;
    private readonly ILogger<LocalDirectoryImporter>? _logger;

    public LocalDirectoryImporter(PhysicalFileSystem primaryFileSystem, DuckDbDataStore db, ILogger<LocalDirectoryImporter>? logger)
    {
        _primary = primaryFileSystem ?? throw new ArgumentNullException(nameof(primaryFileSystem));
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _logger = logger;
    }

    /// <inheritdoc />
    public bool CanHandle(RepoUri source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return string.Equals(source.Scheme, "local", StringComparison.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public Task<CompositeFileSystemMount> ImportAsync(RepoUri source, bool analyze = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        _logger?.LogInformation("[Local] Starting import for {Uri}", source.AbsoluteUri);

        // Extract and normalize path from URI
        var path = source.AbsolutePath;
        var absolutePath = Path.GetFullPath(path);

        _logger?.LogDebug("[Local] Resolved path: {Path}", absolutePath);

        // Validate directory exists
        if (!Directory.Exists(absolutePath))
        {
            _logger?.LogWarning("[Local] Directory not found: {Path}", absolutePath);
            throw new DirectoryNotFoundException($"Directory not found: {absolutePath}");
        }

        // Prevent importing paths that overlap with the primary repository
        var primaryRoot = Path.GetFullPath(_primary.RootPath);
        var normalizedImport = NormalizePath(absolutePath);
        var normalizedPrimary = NormalizePath(primaryRoot);

        if (normalizedImport.Equals(normalizedPrimary, StringComparison.OrdinalIgnoreCase))
        {
            _logger?.LogWarning("[Local] Cannot import the primary repository: {Path}", absolutePath);
            throw new InvalidOperationException($"Cannot import the primary repository. Use file:// URIs to query the current repository.");
        }

        if (normalizedImport.StartsWith(normalizedPrimary + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            _logger?.LogWarning("[Local] Cannot import subdirectory of primary repository: {Path}", absolutePath);
            throw new InvalidOperationException($"Cannot import a subdirectory of the primary repository. The path '{absolutePath}' is inside '{primaryRoot}'.");
        }

        if (normalizedPrimary.StartsWith(normalizedImport + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            _logger?.LogWarning("[Local] Cannot import parent directory of primary repository: {Path}", absolutePath);
            throw new InvalidOperationException($"Cannot import a parent directory of the primary repository. The path '{absolutePath}' contains '{primaryRoot}'.");
        }

        // Create predictable mount ID from path (makes un-import easy)
        // Use forward slashes in PathPrefix to match URI format (URIs always use forward slashes)
        var uriPath = absolutePath.Replace('\\', '/');
        var mountId = $"local:{uriPath}";

        _logger?.LogDebug("[Local] Creating mount {MountId}", mountId);

        // Create PhysicalFileSystem for the directory
        var fs = new PhysicalFileSystem(
            absolutePath,
            scheme: "local",
            uriPrefix: uriPath,
            authority: null);

        // Create mount
        var mount = CompositeFileSystemMount.ForScheme(
            mountId,
            fs,
            scheme: "local",
            authority: null,
            pathPrefix: uriPath,
            includeInEnumeration: true,
            enableWatching: false,
            enableAnalysis: analyze);

        // Persist mount so it survives restarts
        _logger?.LogDebug("[Local] Persisting mount record...");
        _db.SaveMount(new FileSystemMountRecord
        {
            Id = mount.Id,
            Scheme = "local",
            Authority = null,
            PathPrefix = uriPath,
            SourceUri = source.AbsoluteUri,
            LocalPath = absolutePath,
            IncludeInEnumeration = true,
            EnableWatching = false,
            EnableAnalysis = analyze
        });

        _logger?.LogInformation("[Local] Import completed for {Path}", absolutePath);

        return Task.FromResult(mount);
    }

    /// <summary>Normalizes path separators for consistent comparison.</summary>
    private static string NormalizePath(string path)
        => path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
               .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
}
