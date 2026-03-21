using RepoQL.Contracts;

namespace RepoQL.FileSystem.Physical;

public static class FileUriPathResolver
{
    public readonly record struct ResolvedPath(string RelativePath, string AbsolutePath);

    /// <summary>
    /// Resolves a repository URI to both a relative path (for file providers) and an absolute on-disk path rooted at
    /// <paramref name="rootPath"/>. The optional <paramref name="expectedScheme"/> parameter allows callers that
    /// project alternative schemes (e.g., help://, github://) to reuse the resolver while still enforcing the scheme.
    /// </summary>
    public static ResolvedPath Resolve(string rootPath, RepoUri repoUri, string expectedScheme = "file")
    {
        if (rootPath is null) throw new ArgumentNullException(nameof(rootPath));
        if (repoUri is null) throw new ArgumentNullException(nameof(repoUri));

        if (!string.Equals(repoUri.Scheme, expectedScheme, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"URI scheme must be '{expectedScheme}'.");

        var normalizedRoot = Path.GetFullPath(rootPath);
        var relativeSegment = repoUri.GetComponents(UriComponents.Path, UriFormat.Unescaped)
            .TrimStart('/');

        var relativeForProvider = NormalizeRelativeForProvider(relativeSegment);
        var relativeForOs = NormalizeRelativeForOs(relativeSegment);

        var absolutePath = string.IsNullOrEmpty(relativeForOs)
            ? normalizedRoot
            : Path.GetFullPath(Path.Combine(normalizedRoot, relativeForOs));

        EnsureWithinRoot(normalizedRoot, absolutePath);

        return new ResolvedPath(relativeForProvider, absolutePath);
    }

    /// <summary>Convenience helper that returns only the absolute path component of <see cref="Resolve"/>.</summary>
    public static string ToAbsolutePath(string rootPath, RepoUri repoUri, string expectedScheme = "file")
        => Resolve(rootPath, repoUri, expectedScheme).AbsolutePath;

    private static string NormalizeRelativeForProvider(string relative)
    {
        if (string.IsNullOrEmpty(relative))
            return string.Empty;
        return relative.Replace('\\', '/');
    }

    private static string NormalizeRelativeForOs(string relative)
    {
        if (string.IsNullOrEmpty(relative))
            return string.Empty;
        return relative
            .Replace('\\', Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar);
    }

    private static void EnsureWithinRoot(string rootPath, string candidate)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        var rootWithSep = EnsureTrailingSeparator(rootPath);
        if (!candidate.StartsWith(rootWithSep, comparison) &&
            !string.Equals(candidate, rootPath, comparison))
        {
            throw new InvalidOperationException("URI path escapes repository root.");
        }
    }

    private static string EnsureTrailingSeparator(string path)
    {
        if (path.Length == 0)
            return path;
        return path.EndsWith(Path.DirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;
    }
}
