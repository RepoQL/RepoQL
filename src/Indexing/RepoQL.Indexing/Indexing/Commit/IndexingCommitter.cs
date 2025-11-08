using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RepoQL.Contracts.Data;
using RepoQL.Contracts.Models;
using RepoQL.Indexing.Indexing.Pipelines;
using RepoQL.Indexing.Indexing.State;

namespace RepoQL.Indexing.Indexing.Commit;

public sealed class IndexingCommitter(
    IDatabaseWriter writer,
    IDocumentCatalog catalog,
    ILogger<IndexingCommitter>? logger = null)
    : IIndexingCommitter
{
    private readonly IDatabaseWriter _writer = writer ?? throw new ArgumentNullException(nameof(writer));
    private readonly IDocumentCatalog _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    private readonly ILogger<IndexingCommitter> _logger = logger ?? NullLogger<IndexingCommitter>.Instance;

    public async Task CommitAsync(IndexItem item, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (item.Records is null)
        {
            _logger.LogWarning("Skipping commit for {Uri} because no records were produced.", item.Uri);
            return;
        }

        if (string.IsNullOrEmpty(item.DigestHex))
        {
            _logger.LogWarning("Skipping commit for {Uri} because digest is unavailable.", item.Uri);
            return;
        }

        var mediaType = item.MediaType ?? item.RawArtifact.ProvisionalMediaType.Value;
        if (mediaType is null)
        {
            _logger.LogWarning("Skipping commit for {Uri} because media type could not be resolved.", item.Uri);
            return;
        }

        var documentNode = item.Records.Nodes.FirstOrDefault(n =>
            string.Equals(n.Kind, "document", StringComparison.OrdinalIgnoreCase));
        if (documentNode is null)
        {
            _logger.LogWarning("Skipping commit for {Uri} because records do not contain a document node.", item.Uri);
            return;
        }

        var commitRecords = CreateCommitRecords(item);
        var digestHex = item.DigestHex;

        var operation = new WriteOperation
        {
            Id = Guid.NewGuid(),
            Type = WriteOperationType.ReplaceDocument,
            Uri = item.Uri,
            ParsedData = commitRecords,
            ParentContext = Activity.Current?.Context,
            OnCommitted = (_, result) =>
            {
                if (!result.Success)
                    return Task.CompletedTask;

                var entry = new DocumentCatalogEntry(
                    item.Uri,
                    digestHex!,
                    mediaType,
                    item.RawArtifact.PhysicalPath,
                    item.LastModified);
                _catalog.ApplyUpsert(entry);
                return Task.CompletedTask;
            }
        };

        var commitResult = await _writer.EnqueueAndWaitAsync(operation, cancellationToken).ConfigureAwait(false);

        if (!commitResult.Success)
            throw commitResult.Error is not null
                ? new InvalidOperationException($"Database commit failed for {item.Uri}.", commitResult.Error)
                : new InvalidOperationException($"Database commit failed for {item.Uri}.");
    }

    private static Records CreateCommitRecords(IndexItem item)
    {
        var existingAnnotations = item.Records!.Annotations ?? Array.Empty<Annotation>();
        var analyzerAnnotations = item.AnnotationsList.Count > 0
            ? item.AnnotationsList.ToArray()
            : Array.Empty<Annotation>();

        var combinedAnnotations = existingAnnotations.Length == 0
            ? analyzerAnnotations
            : analyzerAnnotations.Length == 0
                ? existingAnnotations
                : [.. existingAnnotations, .. analyzerAnnotations];

        return new Records
        {
            Artifacts = item.Records.Artifacts,
            Nodes = item.Records.Nodes,
            Spans = item.Records.Spans,
            Edges = item.Records.Edges,
            Annotations = combinedAnnotations,
            AnnotationSources = item.Records.AnnotationSources
        };
    }
}
