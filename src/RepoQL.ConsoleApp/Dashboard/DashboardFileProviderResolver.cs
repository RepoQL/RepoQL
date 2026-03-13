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
            ?? TryCreateManifestResourceNameProvider(assembly, "wwwroot")
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

    private static IFileProvider? TryCreateManifestResourceNameProvider(Assembly assembly, string prefix)
    {
        var provider = new ManifestResourceNameFileProvider(assembly, prefix);
        return provider.GetFileInfo("index.html").Exists ? provider : null;
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

    private sealed class ManifestResourceNameFileProvider(Assembly assembly, string prefix) : IFileProvider
    {
        private readonly string[] _resourceNames = assembly.GetManifestResourceNames();
        private readonly string _prefix = prefix.Trim().Trim('/', '\\').Replace('\\', '/');

        public IDirectoryContents GetDirectoryContents(string subpath) => NotFoundDirectoryContents.Singleton;

        public IFileInfo GetFileInfo(string subpath)
        {
            foreach (var suffix in GetCandidateSuffixes(subpath))
            {
                var resourceName = _resourceNames.FirstOrDefault(name => name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
                if (resourceName is not null)
                    return new ManifestResourceNameFileInfo(assembly, resourceName, Path.GetFileName(subpath));
            }

            return new NotFoundFileInfo(subpath);
        }

        public IChangeToken Watch(string filter) => NullChangeToken.Singleton;

        private IEnumerable<string> GetCandidateSuffixes(string subpath)
        {
            var normalized = subpath.TrimStart('/', '\\').Replace('\\', '/').Replace('/', '.');
            yield return $".{_prefix.Replace('/', '.')}.{normalized}";
            yield return $".{normalized}";
        }
    }

    private sealed class ManifestResourceNameFileInfo(Assembly assembly, string resourceName, string name) : IFileInfo
    {
        public bool Exists => true;
        public long Length
        {
            get
            {
                using var stream = CreateReadStream();
                return stream.Length;
            }
        }

        public string PhysicalPath => string.Empty;
        public string Name => name;
        public DateTimeOffset LastModified => DateTimeOffset.MinValue;
        public bool IsDirectory => false;

        public Stream CreateReadStream()
        {
            return assembly.GetManifestResourceStream(resourceName)
                   ?? throw new FileNotFoundException($"Embedded dashboard resource '{resourceName}' was not found.");
        }
    }
}
