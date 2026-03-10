using System.Reflection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;

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
            ?? TryCreateEmbeddedPrefixedProvider(assembly, "wwwroot")
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

    private static IFileProvider? TryCreateEmbeddedPrefixedProvider(Assembly assembly, string prefix)
    {
        var provider = TryCreateEmbeddedProvider(assembly, subpath: null);
        if (provider is null)
            return null;

        var prefixedProvider = new PrefixedFileProvider(provider, prefix);
        return prefixedProvider.GetFileInfo("index.html").Exists ? prefixedProvider : null;
    }

    private sealed class PrefixedFileProvider(IFileProvider inner, string prefix) : IFileProvider
    {
        private readonly string _prefix = prefix.Trim().Trim('/', '\\');

        public IDirectoryContents GetDirectoryContents(string subpath)
            => GetCandidates(subpath)
                .Select(inner.GetDirectoryContents)
                .FirstOrDefault(contents => contents.Exists)
                ?? NotFoundDirectoryContents.Singleton;

        public IFileInfo GetFileInfo(string subpath)
            => GetCandidates(subpath)
                .Select(inner.GetFileInfo)
                .FirstOrDefault(file => file.Exists)
                ?? new NotFoundFileInfo(subpath);

        public IChangeToken Watch(string filter)
            => inner.Watch(GetCandidates(filter).First());

        private IEnumerable<string> GetCandidates(string subpath)
        {
            var normalized = subpath.TrimStart('/', '\\');
            yield return $"{_prefix}/{normalized}";
            yield return $"{_prefix}\\{normalized}";
        }
    }
}
