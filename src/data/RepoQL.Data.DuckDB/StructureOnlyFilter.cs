using System.Reflection;

namespace RepoQL.Data.DuckDB;

/// <summary>
/// Filters URIs to determine if they should receive structure-only embedding
/// (headline + structure) instead of full-text chunked embedding.
/// Uses gitignore-style patterns loaded from an embedded resource.
/// </summary>
internal static class StructureOnlyFilter
{
    private static readonly Lazy<Ignore.Ignore> Filter = new(LoadPatterns, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// Checks if a URI should receive structure-only embedding.
    /// </summary>
    /// <param name="uri">The file URI (e.g., file:///path/to/file.js)</param>
    /// <returns>True if the file should get structure-only embedding.</returns>
    public static bool IsStructureOnly(string? uri)
    {
        if (string.IsNullOrEmpty(uri))
            return false;

        // Extract relative path from URI for pattern matching
        var relativePath = ExtractRelativePath(uri);
        if (string.IsNullOrEmpty(relativePath))
            return false;

        return Filter.Value.IsIgnored(relativePath);
    }

    /// <summary>
    /// Extracts a relative path from a repo URI for pattern matching.
    /// Handles file:/// URIs and normalizes path separators.
    /// </summary>
    private static string? ExtractRelativePath(string uri)
    {
        // Handle file:/// URIs
        if (uri.StartsWith("file:///", StringComparison.OrdinalIgnoreCase))
        {
            var path = uri[8..]; // Remove "file:///"
            // On Windows, might have leading drive letter like C:/...
            // Normalize to forward slashes for gitignore matching
            return path.Replace('\\', '/').TrimStart('/');
        }

        // For repo:// or other schemes, extract the path part
        if (uri.Contains("://", StringComparison.Ordinal))
        {
            var schemeEnd = uri.IndexOf("://", StringComparison.Ordinal) + 3;
            var path = uri[schemeEnd..];
            return path.Replace('\\', '/').TrimStart('/');
        }

        // Already a path
        return uri.Replace('\\', '/').TrimStart('/');
    }

    private static Ignore.Ignore LoadPatterns()
    {
        var ignore = new Ignore.Ignore();

        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("full-embedding.ignore", StringComparison.OrdinalIgnoreCase));

        if (resourceName is null)
            return ignore; // Empty filter if resource not found

        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
            return ignore;

        using var reader = new StreamReader(stream);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line)
        {
            // Skip empty lines and comments
            var trimmed = line.Trim();
            if (trimmed.Length > 0 && !trimmed.StartsWith('#'))
            {
                lines.Add(trimmed);
            }
        }

        if (lines.Count > 0)
        {
            ignore.Add(lines);
        }

        return ignore;
    }
}
