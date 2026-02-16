using System.Reflection;
using RepoQL.Contracts.Snapshots;

namespace RepoQL.ConsoleApp.Host;

/// <summary>
/// Purpose: Provide pre-computed help:// documentation from a build-time snapshot.
/// Complexity: Loads an embedded JSON resource (Release builds only), deserializes via
/// <see cref="SnapshotSerializer"/>, returns domain <see cref="SnapshotDocument"/> objects.
/// Returns empty in Debug builds where no snapshot is embedded.
/// </summary>
internal sealed class HelpDocsSnapshotSource : ISnapshotSource
{
    private const string ResourceName = "HelpDocsSnapshot.json";

    private readonly Lazy<IReadOnlyList<SnapshotDocument>> _documents = new(LoadDocuments);
    private static readonly string CachedVersion = ResolveVersion();

    public string Id => "help-docs";
    public string Version => CachedVersion;
    public string UriPrefix => "help://";

    public IReadOnlyList<SnapshotDocument> GetDocuments() => _documents.Value;

    private static IReadOnlyList<SnapshotDocument> LoadDocuments()
    {
        var assembly = typeof(HelpDocsSnapshotSource).Assembly;
        using var stream = assembly.GetManifestResourceStream(ResourceName);
        if (stream is null)
            return []; // Debug build — no embedded snapshot

        var manifest = SnapshotSerializer.Deserialize(stream);
        return manifest.Documents
            .Select(SnapshotSerializer.FromDto)
            .ToList();
    }

    private static string ResolveVersion()
    {
        return typeof(HelpDocsSnapshotSource).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(HelpDocsSnapshotSource).Assembly.GetName().Version?.ToString()
            ?? "unknown";
    }
}
