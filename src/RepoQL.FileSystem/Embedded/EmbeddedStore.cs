using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.FileProviders;
using RepoQL.Contracts;
using RepoQL.FileSystem.Abstractions;

namespace RepoQL.FileSystem.Embedded;

/// <summary>
///     Embedded resource store. Canonical URI scheme: <c>embed:///logical/path.ext</c>.
/// </summary>
public sealed class EmbeddedStore : IVirtualFileSystem
{
    private readonly Assembly _asm;
    private readonly EmbeddedFileProvider _fileProvider;
    private readonly Dictionary<string, string> _uriToRes;

    /// <summary>Create an embedded store for the specified assembly.</summary>
    public EmbeddedStore(Assembly asm)
    {
        _asm = asm ?? throw new ArgumentNullException(nameof(asm));
        _fileProvider = new EmbeddedFileProvider(asm);
        var asmName = _asm.GetName().Name ?? "asm";
        _uriToRes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var full in _asm.GetManifestResourceNames())
        {
            var logical = ToLogicalPath(full, asmName);
            _uriToRes[$"{Scheme}:///{logical}"] = logical;
        }
    }

    /// <inheritdoc />
    public string Scheme => "embed";

    /// <inheritdoc />
    public async IAsyncEnumerable<IFileInfo> EnumerateAsync([EnumeratorCancellation] CancellationToken ct)
    {
        // EmbeddedFileProvider doesn't support directory enumeration well,
        // so we enumerate all manifest resources
        foreach (var full in _asm.GetManifestResourceNames())
        {
            if (ct.IsCancellationRequested) yield break;

            var asmName = _asm.GetName().Name ?? "asm";
            var logical = ToLogicalPath(full, asmName);
            var fileInfo = _fileProvider.GetFileInfo(logical);
            if (fileInfo.Exists)
            {
                // PhysicalPath set to absolute logical path so hub emits embed:///logical
                yield return new EmbeddedLogicalFileInfo(fileInfo, $"/{logical}");
            }

            await Task.Yield();
        }
    }

    /// <inheritdoc />
    public IFileInfo GetFile(RepoUri uri)
    {
        if (!_uriToRes.TryGetValue(uri.AbsoluteUri, out var resourceName))
            return new NotFoundFileInfo(uri.AbsoluteUri);

        var fi = _fileProvider.GetFileInfo(resourceName);
        return new EmbeddedLogicalFileInfo(fi, $"/{resourceName}");
    }

    /// <inheritdoc />
    public IFileSystemWatcher Watch() => new ManualWatcher();

    private static string ToLogicalPath(string manifestName, string asmName)
    {
        // Strip the assembly prefix if present
        var prefix = asmName + ".";
        var rest = manifestName.StartsWith(prefix, StringComparison.Ordinal) ? manifestName[prefix.Length..] : manifestName;

        // Split at last '.' to keep extension intact
        var lastDot = rest.LastIndexOf('.');
        if (lastDot < 0) return rest.Replace('.', '/');
        var withoutExt = rest[..lastDot];
        var ext = rest[(lastDot + 1)..];
        return withoutExt.Replace('.', '/') + "." + ext;
    }
}
