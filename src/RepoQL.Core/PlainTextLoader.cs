using System.Text.Json.Nodes;
using RepoQL.Contracts;
using RepoQL.Contracts.Models;

namespace RepoQL.Core;

internal sealed class PlainTextLoader : IFormatLoader, IFormatMaterializer
{
    private static readonly SemanticMediaType PlainText = SemanticMediaType
        .Create("text", "plain")
        .WithKind("plain.document");

    public bool Supports(SemanticMediaType mediaType)
    {
        ArgumentNullException.ThrowIfNull(mediaType);

        if (string.Equals(mediaType.Kind, PlainText.Kind, StringComparison.OrdinalIgnoreCase))
            return true;

        return string.Equals(mediaType.Type, PlainText.Type, StringComparison.OrdinalIgnoreCase)
               && string.Equals(mediaType.Subtype, PlainText.Subtype, StringComparison.OrdinalIgnoreCase);
    }

    public Task<bool> CanLoadAsync(DiscoveredArtifact artifact, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        artifact.MediaType ??= PlainText;
        return Task.FromResult(true);
    }

    public async Task<DocumentModel> LoadAsync(DiscoveredArtifact artifact, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        if (artifact.RepoUri is null)
            throw new InvalidOperationException("RepoUri required for plain text loader.");

        string text;
        string digest;
        long size;
        try
        {
            var loaded = await FileContentReader.ReadAllTextWithDigestAsync(
                artifact.File,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            text = loaded.Text;
            digest = loaded.Digest;
            size = loaded.ByteLength;
        }
        catch
        {
            text = string.Empty;
            digest = ContentDigest.FromBytes(ReadOnlySpan<byte>.Empty);
            size = artifact.File.Exists ? artifact.File.Length : 0;
        }

        var media = artifact.MediaType ?? PlainText;
        var metadata = new Dictionary<string, object?>
        {
            ["plaintext.digest"] = digest,
            ["plaintext.size"] = size
        };

        return new DocumentModel(artifact.RepoUri, media, text, metadata: metadata);
    }

    public Records Materialize(DocumentModel document)
    {
        var digest = document.GetMetadataOrDefault<string>("plaintext.digest") ?? "unknown";
        var size = document.GetMetadataOrDefault<long>("plaintext.size");

        // Prepare x-ray fields for initializer; best-effort and terse
        string? headline = null;
        string? summary = null;
        try
        {
            var fileName = GetFileName(document.Uri);
            var kindOrBase = !string.IsNullOrWhiteSpace(document.MediaType.Kind)
                ? document.MediaType.Kind!
                : $"{document.MediaType.Type}/{document.MediaType.Subtype}";
            var sizeHuman = FormatBytes(size);
            var lineCount = document.LineMap?.LineCount ?? 0;

            headline = lineCount > 0
                ? $"{fileName} | {kindOrBase} | {sizeHuman} | {lineCount} lines"
                : $"{fileName} | {kindOrBase} | {sizeHuman}";

            var summaryLines = new List<string>(2)
            {
                $"Type: {kindOrBase}"
            };
            var shape = lineCount > 0 ? $"Size: {sizeHuman}, Lines: {lineCount}" : $"Size: {sizeHuman}";
            summaryLines.Add(shape);
            summary = string.Join('\n', summaryLines);
        }
        catch
        {
            // ignore
        }

        var artifact = new Artifact
        {
            Id = Guid.NewGuid(),
            Digest = digest,
            Size = size,
            MediaType = document.MediaType,
            Text = document.Text,
            StoreUri = document.Uri.ToString(),
            Headline = headline,
            Summary = summary,
            Structure = null
        };

        var node = new Node
        {
            Id = Guid.NewGuid(),
            Kind = "document",
            Uri = document.Uri,
            ArtifactId = artifact.Id,
            Props = new JsonObject
            {
                ["media_type"] = artifact.MediaType?.ToString(),
                ["byte_size"] = artifact.Size
            },
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        return new Records
        {
            Artifacts = [artifact],
            Nodes = [node],
            Spans = [],
            Edges = []
        };
    }

    private static string GetFileName(RepoUri uri)
    {
        // Try LocalPath for file://, otherwise use last segment of AbsolutePath.
        try
        {
            if (uri.IsFile)
            {
                var lp = uri.LocalPath;
                if (!string.IsNullOrEmpty(lp)) return Path.GetFileName(lp);
            }
        }
        catch
        {
            // fall through
        }
        var ap = Uri.UnescapeDataString(uri.AbsolutePath);
        var slash = ap.LastIndexOf('/')
                    >= 0 ? ap[(ap.LastIndexOf('/') + 1)..] : ap;
        return string.IsNullOrEmpty(slash) ? uri.AbsoluteUri : slash;
    }

    private static string FormatBytes(long bytes)
    {
        const long KB = 1024;
        const long MB = KB * 1024;
        const long GB = MB * 1024;
        if (bytes >= GB) return ($"{bytes / (double)GB:0.##} GB");
        if (bytes >= MB) return ($"{bytes / (double)MB:0.##} MB");
        if (bytes >= KB) return ($"{bytes / (double)KB:0.##} KB");
        return ($"{bytes} B");
    }
}
