using System.Runtime.CompilerServices;
using System.Reflection;
using System.Xml.Linq;
using Microsoft.Extensions.FileProviders;
using RepoQL.Contracts;
using RepoQL.FileSystem.Abstractions;

namespace RepoQL.FileSystem.Embedded;

/// <summary>
///     Embedded resource store. Canonical URI scheme: <c>embed:///logical/path.ext</c>.
///     Canonical paths are sourced from the embedded manifest XML when available.
/// </summary>
public sealed class EmbeddedStore : IVirtualFileSystem
{
    private const string EmbeddedManifestResourceName = "Microsoft.Extensions.FileProviders.Embedded.Manifest.xml";

    private readonly record struct EmbeddedEntry(string CanonicalPath, string ProviderPath);

    private readonly IFileProvider _fileProvider;
    private readonly Dictionary<string, EmbeddedEntry> _uriToEntry;
    private readonly string _scheme;

    /// <summary>
    /// Create an embedded store for the specified assembly. Optionally override the exposed URI scheme when the store
    /// is mounted under a custom prefix (e.g., help:// instead of embed://).
    /// </summary>
    public EmbeddedStore(Assembly asm, string? scheme = null)
    {
        ArgumentNullException.ThrowIfNull(asm);
        _fileProvider = CreateFileProvider(asm);
        _scheme = string.IsNullOrWhiteSpace(scheme)
            ? "embed"
            : scheme.Trim().ToLowerInvariant();

        _uriToEntry = new Dictionary<string, EmbeddedEntry>(StringComparer.OrdinalIgnoreCase);

        if (!TryIndexFromManifest(asm) && !TryIndexFromResourceNames(asm))
            EnumerateDirectoryViaProvider("");
    }

    /// <inheritdoc />
    public string Scheme => _scheme;

    /// <inheritdoc />
    public async IAsyncEnumerable<IFileInfo> EnumerateAsync([EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var entry in _uriToEntry.Values)
        {
            if (ct.IsCancellationRequested) yield break;

            var fileInfo = _fileProvider.GetFileInfo(entry.ProviderPath);
            if (fileInfo.Exists)
                yield return new EmbeddedLogicalFileInfo(fileInfo, $"/{entry.CanonicalPath}");

            await Task.Yield();
        }
    }

    /// <inheritdoc />
    public IFileInfo GetFile(RepoUri uri)
    {
        if (!_uriToEntry.TryGetValue(uri.AbsoluteUri, out var entry))
            return new NotFoundFileInfo(uri.AbsoluteUri);

        var fi = _fileProvider.GetFileInfo(entry.ProviderPath);
        return new EmbeddedLogicalFileInfo(fi, $"/{entry.CanonicalPath}");
    }

    /// <inheritdoc />
    public RepoUri GetUri(IFileInfo file)
    {
        if (file.PhysicalPath == null)
            throw new ArgumentException("File must have a PhysicalPath", nameof(file));

        var logicalPath = file.PhysicalPath.TrimStart('/');
        return RepoUri.Parse($"{Scheme}:///{logicalPath}");
    }

    /// <inheritdoc />
    public IFileSystemWatcher Watch() => new ManualWatcher();

    private static IFileProvider CreateFileProvider(Assembly asm)
    {
        try
        {
            return new ManifestEmbeddedFileProvider(asm);
        }
        catch (InvalidOperationException)
        {
            return new EmbeddedFileProvider(asm);
        }
    }

    private bool TryIndexFromManifest(Assembly asm)
    {
        using var stream = OpenManifestStream(asm);
        if (stream == null)
            return false;

        XDocument manifest;
        try
        {
            manifest = XDocument.Load(stream, LoadOptions.None);
        }
        catch
        {
            return false;
        }

        var fileSystem = manifest.Root?.Element("FileSystem");
        if (fileSystem == null)
            return false;

        var countBefore = _uriToEntry.Count;
        EnumerateManifestDirectory(fileSystem, "");
        return _uriToEntry.Count > countBefore;
    }

    private static Stream? OpenManifestStream(Assembly asm)
    {
        var manifestStream = asm.GetManifestResourceStream(EmbeddedManifestResourceName);
        if (manifestStream != null)
            return manifestStream;

        var fallbackResourceName = asm.GetManifestResourceNames()
            .FirstOrDefault(static name => name.EndsWith(".Manifest.xml", StringComparison.Ordinal));
        if (fallbackResourceName == null)
            return null;

        return asm.GetManifestResourceStream(fallbackResourceName);
    }

    private bool TryIndexFromResourceNames(Assembly asm)
    {
        var assemblyName = asm.GetName().Name;
        if (string.IsNullOrWhiteSpace(assemblyName))
            return false;

        var prefix = $"{assemblyName}.";
        var countBefore = _uriToEntry.Count;

        foreach (var resourceName in asm.GetManifestResourceNames())
        {
            if (!resourceName.StartsWith(prefix, StringComparison.Ordinal))
                continue;

            var relativeResourceName = resourceName[prefix.Length..];
            var providerPath = TryResolveProviderPathFromResourceName(relativeResourceName);
            if (providerPath == null)
                continue;

            RegisterPath(providerPath, providerPath);
        }

        return _uriToEntry.Count > countBefore;
    }

    private string? TryResolveProviderPathFromResourceName(string relativeResourceName)
    {
        var parts = relativeResourceName.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return null;

        var initialFileStart = parts.Length > 1 ? parts.Length - 2 : 0;
        for (var fileStart = initialFileStart; fileStart >= 0; fileStart--)
        {
            var fileName = string.Join('.', parts.Skip(fileStart));
            var directoryPath = fileStart == 0
                ? string.Empty
                : string.Join('/', parts.Take(fileStart));
            var candidatePath = string.IsNullOrEmpty(directoryPath)
                ? fileName
                : $"{directoryPath}/{fileName}";
            if (_fileProvider.GetFileInfo(candidatePath).Exists)
                return candidatePath;
        }

        return null;
    }

    private void EnumerateManifestDirectory(XContainer directoryElement, string path)
    {
        foreach (var fileElement in directoryElement.Elements("File"))
        {
            var fileName = fileElement.Attribute("Name")?.Value;
            if (string.IsNullOrWhiteSpace(fileName))
                continue;

            var canonicalPath = string.IsNullOrEmpty(path) ? fileName : $"{path}/{fileName}";
            var providerPath = ResolveProviderPath(canonicalPath);
            if (!_fileProvider.GetFileInfo(providerPath).Exists)
                continue;
            RegisterPath(canonicalPath, providerPath);
        }

        foreach (var childDirectory in directoryElement.Elements("Directory"))
        {
            var directoryName = childDirectory.Attribute("Name")?.Value;
            if (string.IsNullOrWhiteSpace(directoryName))
                continue;

            var childPath = string.IsNullOrEmpty(path) ? directoryName : $"{path}/{directoryName}";
            EnumerateManifestDirectory(childDirectory, childPath);
        }
    }

    private string ResolveProviderPath(string canonicalPath)
    {
        if (_fileProvider.GetFileInfo(canonicalPath).Exists)
            return canonicalPath;

        var segments = canonicalPath.Split('/', StringSplitOptions.None);
        if (segments.Length < 2)
            return canonicalPath;

        var candidate = TryResolveMangledDirectoryPath(segments, 0);
        return candidate ?? canonicalPath;
    }

    private string? TryResolveMangledDirectoryPath(string[] segments, int startIndex)
    {
        for (var i = startIndex; i < segments.Length - 1; i++)
        {
            var original = segments[i];
            if (!original.Contains('-', StringComparison.Ordinal))
                continue;

            segments[i] = original.Replace('-', '_');
            var candidatePath = string.Join('/', segments);
            if (_fileProvider.GetFileInfo(candidatePath).Exists)
            {
                segments[i] = original;
                return candidatePath;
            }

            var deeperCandidate = TryResolveMangledDirectoryPath(segments, i + 1);
            segments[i] = original;
            if (deeperCandidate != null)
                return deeperCandidate;
        }

        return null;
    }

    private void EnumerateDirectoryViaProvider(string path)
    {
        foreach (var entry in _fileProvider.GetDirectoryContents(path))
        {
            var entryPath = string.IsNullOrEmpty(path) ? entry.Name : $"{path}/{entry.Name}";
            if (entry.IsDirectory)
            {
                EnumerateDirectoryViaProvider(entryPath);
                continue;
            }

            RegisterPath(entryPath, entryPath);
        }
    }

    private void RegisterPath(string canonicalPath, string providerPath)
    {
        var uri = $"{Scheme}:///{canonicalPath}";
        _uriToEntry[uri] = new EmbeddedEntry(canonicalPath, providerPath);
    }
}
