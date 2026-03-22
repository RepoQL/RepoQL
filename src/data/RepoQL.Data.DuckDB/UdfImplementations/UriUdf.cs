using RepoQL.Contracts;
using RepoQL.Data.DuckDB.UdfFramework;
using RepoQL.FileSystem.Physical;

namespace RepoQL.Data.DuckDB.UdfImplementations;

/// <summary>
/// UDF class for URI resolution operations.
/// Converts repository URIs to physical filesystem paths for use with external functions like read_xlsx().
/// </summary>
/// <remarks>
/// <para><b>Purpose:</b> Bridge between RepoQL's URI-based addressing and DuckDB's file path requirements.
/// External functions like read_xlsx() require physical paths, but RepoQL works with file:/// URIs.</para>
/// <para><b>Complexity:</b> Handles URL decoding, scheme validation, and path normalization. The file system
/// provides the repository root path, and this UDF combines it with the relative path from the URI.</para>
/// </remarks>
[UdfClass]
public class UriUdf(PhysicalFileSystem fileSystem)
{
    /// <summary>
    /// Converts a repository URI to a physical filesystem path.
    /// Used internally by table function wrappers and can be called directly.
    /// </summary>
    /// <example>
    /// <code>
    /// SELECT resolve_path('file:///Examples/data.xlsx');
    /// -- Returns: C:/Source/RepoQL/Examples/data.xlsx
    /// </code>
    /// </example>
    /// <param name="uri">Repository URI (e.g., file:///Examples/data.xlsx)</param>
    /// <returns>Physical filesystem path (e.g., C:/Source/RepoQL/Examples/data.xlsx)</returns>
    [ScalarUdf("_resolve_path_internal", MacroName = "resolve_path", Description = "Convert repo URI to physical filesystem path", IsPure = true)]
    public string? ToPhysicalPath(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri))
            return null;

        // Parse the URI and validate scheme
        if (!RepoUri.TryParse(uri, out var repoUri))
            return null;

        // Only handle file:// URIs
        if (!string.Equals(repoUri.Scheme, "file", StringComparison.OrdinalIgnoreCase))
            return null;

        // Get the path portion from the URI (URL-decoded via AbsolutePath)
        // For file:///Examples/test.xlsx, AbsolutePath returns "/Examples/test.xlsx"
        var absolutePath = repoUri.AbsolutePath;

        // Remove leading slash to get relative path
        var relativePath = absolutePath.TrimStart('/');

        // URL decode the path (handles %20 -> space, etc.)
        relativePath = Uri.UnescapeDataString(relativePath);

        if (string.IsNullOrEmpty(relativePath))
            return fileSystem.RootPath;

        // Combine with repository root
        var fullPath = Path.Combine(fileSystem.RootPath, relativePath);

        // Normalize path separators for the current platform
        return Path.GetFullPath(fullPath);
    }

}
