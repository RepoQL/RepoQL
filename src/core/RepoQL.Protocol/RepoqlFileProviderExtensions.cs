using Microsoft.Extensions.FileProviders;

namespace RepoQL.Protocol;

/// <summary>
/// Purpose: Extend IFileProvider with helpers for repo-local ".repoql" files.
/// Complexity: Normalizes relative paths and hides stream handling.
/// </summary>
public static class RepoqlFileProviderExtensions
{
    public static IFileInfo GetRepoqlFileInfo(this IFileProvider provider, string relativePath)
    {
        ArgumentNullException.ThrowIfNull(provider);

        var normalized = NormalizeRelativePath(relativePath);
        var repoqlRelative = string.IsNullOrEmpty(normalized)
            ? RepoqlPaths.RepoqlDirectoryName
            : $"{RepoqlPaths.RepoqlDirectoryName}/{normalized}";
        return provider.GetFileInfo(repoqlRelative);
    }

    public static string? TryReadRepoqlFileText(this IFileProvider provider, string relativePath)
    {
        ArgumentNullException.ThrowIfNull(provider);

        try
        {
            var file = provider.GetRepoqlFileInfo(relativePath);
            if (!file.Exists)
                return null;

            using var stream = file.CreateReadStream();
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch
        {
            return null;
        }
    }

    public static string? TryReadRepoqlSocketMapping(this IFileProvider provider)
    {
        var contents = provider.TryReadRepoqlFileText(RepoqlPaths.SocketMapFileName);
        var trimmed = contents?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    private static string NormalizeRelativePath(string relativePath)
    {
        var trimmed = (relativePath ?? string.Empty)
            .Trim()
            .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return trimmed.Replace('\\', '/');
    }
}
