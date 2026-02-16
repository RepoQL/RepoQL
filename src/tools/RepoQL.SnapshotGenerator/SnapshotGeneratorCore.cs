using RepoQL.Contracts;
using RepoQL.Contracts.Snapshots;
using RepoQL.Formats.Markdown;

namespace RepoQL.SnapshotGenerator;

/// <summary>
/// Purpose: Generate a <see cref="SnapshotManifest"/> from a directory of markdown files.
/// Complexity: Enumerates files, runs each through <see cref="MarkdownLoader"/>,
/// collects <see cref="SnapshotDocumentDto"/> records, wraps in a manifest.
/// </summary>
public static class SnapshotGeneratorCore
{
    /// <summary>
    /// Generate a snapshot manifest from all markdown files in <paramref name="docsDirectory"/>.
    /// Files are converted to <c>help:///</c> URIs matching the <c>EmbeddedStore.ToLogicalPath</c> convention.
    /// </summary>
    public static async Task<SnapshotManifest> GenerateAsync(
        string docsDirectory,
        string version,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(docsDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);

        var rootDir = new DirectoryInfo(docsDirectory);
        if (!rootDir.Exists)
            throw new DirectoryNotFoundException($"Documentation directory not found: {docsDirectory}");

        var loader = new MarkdownLoader();
        var documents = new List<SnapshotDocumentDto>();

        var mdFiles = rootDir.EnumerateFiles("*.md", SearchOption.AllDirectories)
            .OrderBy(f => f.FullName, StringComparer.OrdinalIgnoreCase);

        foreach (var file in mdFiles)
        {
            ct.ThrowIfCancellationRequested();

            var relativePath = Path.GetRelativePath(rootDir.FullName, file.FullName)
                .Replace('\\', '/');
            var helpUri = RepoUri.Parse($"help:///{relativePath}");

            var artifact = new DiscoveredArtifact
            {
                File = new PhysicalFileInfoAdapter(file),
                RepoUri = helpUri,
                MediaType = SemanticMediaType.Create("text", "markdown").WithKind("markdown.doc")
            };

            var model = await loader.LoadAsync(artifact, ct).ConfigureAwait(false);
            var records = loader.Materialize(model);
            var dto = SnapshotSerializer.ToDto(helpUri, records);
            documents.Add(dto);
        }

        return new SnapshotManifest
        {
            FormatVersion = "1",
            SourceId = "help-docs",
            Version = version,
            Documents = documents
        };
    }
}
