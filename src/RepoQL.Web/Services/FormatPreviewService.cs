using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RepoQL.Contracts;

namespace RepoQL.Web.Services;

public sealed record FormatPreviewRequest(
    string Uri,
    byte[]? Content,
    string? FileName,
    string? MediaTypeHint);

public sealed record FormatPreviewStage(string Stage, string Status, double DurationMs, string? Error);

public sealed record FormatPreviewArtifact(string Id, string Digest, long SizeBytes, string MediaType, string Headline, string Summary, string Structure);

public sealed record FormatPreviewNode(string Id, string Kind, string Uri, string Headline, string Structure);

public sealed record FormatPreviewAnnotation(string Kind, string Severity, string Source, string Message);

public sealed record FormatPreviewRecords(
    IReadOnlyList<FormatPreviewArtifact> Artifacts,
    IReadOnlyList<FormatPreviewNode> Nodes,
    IReadOnlyList<FormatPreviewAnnotation> Annotations);

public sealed record FormatPreviewResult(
    bool Success,
    string? Error,
    string MediaType,
    string DigestHex,
    IReadOnlyList<FormatPreviewStage> Stages,
    FormatPreviewRecords Records)
{
    public static FormatPreviewResult FromResponse(PreviewDocumentResponse response)
    {
        var stages = response.Stages
            .Select(s => new FormatPreviewStage(
                s.Stage,
                s.Status,
                s.DurationMs,
                string.IsNullOrWhiteSpace(s.Error) ? null : s.Error))
            .ToArray();

        var artifacts = response.Records?.Artifacts?
            .Select(a => new FormatPreviewArtifact(
                a.Id,
                a.Digest,
                a.SizeBytes,
                a.MediaType,
                a.Headline,
                a.Summary,
                a.Structure))
            .ToArray() ?? Array.Empty<FormatPreviewArtifact>();

        var nodes = response.Records?.Nodes?
            .Select(n => new FormatPreviewNode(
                n.Id,
                n.Kind,
                n.Uri,
                n.Headline,
                n.Structure))
            .ToArray() ?? Array.Empty<FormatPreviewNode>();

        var annotations = response.Records?.Annotations?
            .Select(a => new FormatPreviewAnnotation(
                a.Kind,
                a.Severity,
                a.Source,
                a.Message))
            .ToArray() ?? Array.Empty<FormatPreviewAnnotation>();

        var records = new FormatPreviewRecords(artifacts, nodes, annotations);
        return new FormatPreviewResult(
            response.Success,
            string.IsNullOrWhiteSpace(response.Error) ? null : response.Error,
            response.MediaType,
            response.DigestHex,
            stages,
            records);
    }
}

public sealed class FormatPreviewService
{
    private readonly RepoQlConnectionManager _connectionManager;
    private readonly ILogger<FormatPreviewService> _logger;

    public FormatPreviewService(RepoQlConnectionManager connectionManager, ILogger<FormatPreviewService> logger)
    {
        _connectionManager = connectionManager;
        _logger = logger;
    }

    public async Task<FormatPreviewResult> RunPreviewAsync(FormatPreviewRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Uri))
            throw new ArgumentException("Repository URI is required.", nameof(request));

        var client = await _connectionManager.GetClientAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogDebug("Previewing {Uri} (uploaded={Uploaded})", request.Uri, request.Content is { Length: > 0 });
        var response = await client.PreviewDocumentAsync(
            uri: request.Uri,
            content: request.Content,
            fileName: request.FileName,
            mediaTypeHint: request.MediaTypeHint,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return FormatPreviewResult.FromResponse(response);
    }
}
