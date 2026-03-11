using System.IO.Hashing;
using System.Text.Json.Nodes;
using RepoQL.Contracts;
using RepoQL.Contracts.Models;

namespace RepoQL.Core;

internal sealed class PlainTextLoader : IFormatLoader, IFormatMaterializer
{
    internal const long DefaultMaxTextReadBytes = 256 * 1024 * 1024;

    private const string DigestMetadataKey = "plaintext.digest";
    private const string SizeMetadataKey = "plaintext.size";
    private const string ContentOmittedMetadataKey = "plaintext.content_omitted";
    private const string ContentOmissionReasonMetadataKey = "plaintext.content_omission_reason";

    private static readonly SemanticMediaType PlainText = SemanticMediaType
        .Create("text", "plain")
        .WithKind("plain.document");

    private readonly long _maxTextReadBytes;

    internal static SemanticMediaType PlainTextMediaType => PlainText;

    public PlainTextLoader(long maxTextReadBytes = DefaultMaxTextReadBytes)
    {
        _maxTextReadBytes = maxTextReadBytes;
    }

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
        var size = artifact.File.Exists ? artifact.File.Length : 0L;
        var media = artifact.MediaType ?? PlainText;
        var contentOmitted = false;
        string? contentOmissionReason = null;

        try
        {
            if (ShouldReadAsText(media) && size > 0 && size > _maxTextReadBytes)
            {
                text = string.Empty;
                digest = await FileDigest.ComputeAsync(artifact.File, cancellationToken).ConfigureAwait(false);
                contentOmitted = true;
                contentOmissionReason = $"size>{_maxTextReadBytes}";
            }
            else if (ShouldReadAsText(media))
            {
                var loaded = await FileContentReader.ReadAllTextWithDigestAsync(
                    artifact.File,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                text = loaded.Text;
                digest = loaded.Digest;
                size = loaded.ByteLength;
            }
            else
            {
                text = string.Empty;
                digest = await FileDigest.ComputeAsync(artifact.File, cancellationToken).ConfigureAwait(false);
                contentOmitted = true;
                contentOmissionReason = "non-text";
            }
        }
        catch
        {
            text = string.Empty;
            digest = ContentDigest.FromBytes(ReadOnlySpan<byte>.Empty);
            size = artifact.File.Exists ? artifact.File.Length : 0;
            contentOmitted = true;
            contentOmissionReason ??= "load-failed";
        }

        var metadata = new Dictionary<string, object?>
        {
            [DigestMetadataKey] = digest,
            [SizeMetadataKey] = size,
            [ContentOmittedMetadataKey] = contentOmitted,
            [ContentOmissionReasonMetadataKey] = contentOmissionReason
        };

        return new DocumentModel(artifact.RepoUri, media, text, metadata: metadata);
    }

    public Records Materialize(DocumentModel document)
    {
        var digest = document.GetMetadataOrDefault<string>(DigestMetadataKey) ?? "unknown";
        var size = document.GetMetadataOrDefault<long>(SizeMetadataKey);
        var contentOmitted = document.GetMetadataOrDefault<bool>(ContentOmittedMetadataKey);
        var contentOmissionReason = document.GetMetadataOrDefault<string>(ContentOmissionReasonMetadataKey);

        var tokenCount = TokenEstimator.EstimateTokensSafe(document.Text);

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
            var tokensStr = !contentOmitted && tokenCount.HasValue ? $" | {FormatTokens(tokenCount.Value)}" : "";

            if (contentOmitted)
            {
                headline = $"{fileName} | {kindOrBase} | {sizeHuman} | content omitted";
                summary = string.Join('\n', new[]
                {
                    $"Type: {kindOrBase}",
                    $"Size: {sizeHuman}",
                    $"Content omitted: {DescribeContentOmission(contentOmissionReason)}"
                });
            }
            else
            {
                headline = lineCount > 0
                    ? $"{fileName} | {kindOrBase} | {sizeHuman} | {lineCount} lines{tokensStr}"
                    : $"{fileName} | {kindOrBase} | {sizeHuman}{tokensStr}";

                var summaryLines = new List<string>(2)
                {
                    $"Type: {kindOrBase}"
                };
                var shape = lineCount > 0 ? $"Size: {sizeHuman}, Lines: {lineCount}" : $"Size: {sizeHuman}";
                summaryLines.Add(shape);
                summary = string.Join('\n', summaryLines);
            }
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
            Structure = null,
            TokenCount = tokenCount
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

    private static bool ShouldReadAsText(SemanticMediaType mediaType)
        => string.Equals(mediaType.Type, "text", StringComparison.OrdinalIgnoreCase);

    private string DescribeContentOmission(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return "content was not loaded";

        if (reason.StartsWith("size>", StringComparison.Ordinal))
            return $"file exceeds the {_maxTextReadBytes} byte safety limit";

        return reason switch
        {
            "non-text" => "binary media is indexed as metadata only",
            "load-failed" => "content could not be loaded safely",
            _ => "content was not loaded"
        };
    }

    private static string GetFileName(RepoUri uri)
    {
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

    private static string FormatTokens(int tokens)
    {
        if (tokens >= 1_000_000)
            return $"~{tokens / 1_000_000d:0.#}M tok";
        if (tokens >= 1_000)
            return $"~{tokens / 1_000d:0.#}k tok";
        return $"~{tokens} tok";
    }
}
