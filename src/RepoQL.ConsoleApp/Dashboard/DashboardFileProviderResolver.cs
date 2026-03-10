using System.Reflection;
using Microsoft.Extensions.FileProviders;

namespace RepoQL.ConsoleApp.Dashboard;

/// <summary>
/// Purpose: Resolve the dashboard static-file provider across debug builds and single-file publishes.
/// Complexity: Probes extracted bundle content first, then falls back to manifest-embedded resources.
/// </summary>
internal static class DashboardFileProviderResolver
{
    public static IFileProvider? Resolve(Assembly assembly, string baseDirectory)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);

        return TryCreatePhysicalProvider(Path.Combine(baseDirectory, "wwwroot"))
            ?? TryCreatePhysicalProvider(baseDirectory)
            ?? TryCreateEmbeddedProvider(assembly, "wwwroot")
            ?? TryCreateEmbeddedProvider(assembly, subpath: null);
    }

    private static IFileProvider? TryCreatePhysicalProvider(string rootPath)
    {
        if (!Directory.Exists(rootPath))
            return null;

        var provider = new PhysicalFileProvider(rootPath);
        return provider.GetFileInfo("index.html").Exists ? provider : null;
    }

    private static IFileProvider? TryCreateEmbeddedProvider(Assembly assembly, string? subpath)
    {
        try
        {
            IFileProvider provider = string.IsNullOrEmpty(subpath)
                ? new ManifestEmbeddedFileProvider(assembly)
                : new ManifestEmbeddedFileProvider(assembly, subpath);

            return provider.GetFileInfo("index.html").Exists ? provider : null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}