using RepoQL.Contracts;

namespace RepoQL.FileSystem.Physical;

internal static class FileUriPathResolver
{
    internal readonly record struct ResolvedPath(string RelativePath, string AbsolutePath);

    public static ResolvedPath Resolve(string rootPath, RepoUri repoUri)
    {
        if (rootPath is null) throw new ArgumentNullException(nameof(rootPath));
        if (repoUri is null) throw new ArgumentNullException(nameof(repoUri));

        if (!string.Equals(repoUri.Scheme, "file", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("URI scheme must be 'file'.");

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

    public static string ToAbsolutePath(string rootPath, RepoUri repoUri)
        => Resolve(rootPath, repoUri).AbsolutePath;

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
