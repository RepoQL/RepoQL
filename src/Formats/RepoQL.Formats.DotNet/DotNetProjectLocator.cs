using System.Collections.Concurrent;

namespace RepoQL.Formats.DotNet;

internal static class DotNetProjectLocator
{
    private static readonly ConcurrentDictionary<string, string?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static string? FindProject(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return null;

        var directory = Path.GetDirectoryName(Path.GetFullPath(filePath));
        if (string.IsNullOrEmpty(directory))
            return null;

        return Cache.GetOrAdd(directory, SearchUpward);
    }

    private static string? SearchUpward(string startDirectory)
    {
        var current = startDirectory;
        while (!string.IsNullOrEmpty(current))
        {
            var csproj = Directory.EnumerateFiles(current, "*.csproj").FirstOrDefault();
            if (csproj is not null)
                return csproj;

            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                break;
            current = parent;
        }
        return null;
    }
}
