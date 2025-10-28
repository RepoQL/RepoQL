using System.Text.Json.Nodes;
using RepoQL.Contracts;
using RepoQL.Contracts.Analysis;
using RepoQL.Contracts.Data;
using RepoQL.Contracts.Models;

namespace RepoQL.Core.Analysis;

public sealed class AnnotationResultWriter(IGraphStore store, IDatabaseWriter? writer = null) : IAnalysisResultWriter
{
    public async Task WriteAsync(string containerUri, IReadOnlyList<AnalysisResult> results, CancellationToken cancellationToken = default)
    {
        if (results.Count == 0)
            return;

        RepoUri repoUri;
        try
        {
            repoUri = RepoUri.Parse(containerUri);
        }
        catch
        {
            return;
        }

        var document = store.GetDocumentByUri(repoUri);
        if (document is null)
            return;

        var annotations = new List<Annotation>();
        foreach (var result in results)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            if (result.Severity == AnalysisSeverity.None)
                continue;

            var annotation = new Annotation
            {
                SemanticKey = result.SemanticKey,
                Kind = result.Kind,
                Severity = MapSeverity(result.Severity),
                Source = result.Source,
                RuleId = result.RuleId,
                Message = result.Message,
                Data = CloneData(result.Data) ?? new JsonObject(),
                ScopeDocumentId = document.Id,
                TargetNodeId = result.Target?.NodeId,
                TargetEdgeId = result.Target?.EdgeId,
                TargetSpanId = result.Target?.SpanId,
                TargetUri = result.Target?.TargetUri
            };

            annotations.Add(annotation);
        }

        if (annotations.Count == 0)
            return;

        // Route through single-threaded writer to avoid concurrency conflicts
        if (writer is not null)
        {
            var operation = new WriteOperation
            {
                Id = Guid.NewGuid(),
                Type = WriteOperationType.UpsertAnnotations,
                Uri = repoUri,
                ParsedData = new Records
                {
                    Artifacts = [],
                    Annotations = [.. annotations]
                }
            };

            await writer.EnqueueAsync(operation, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            // Fallback for tests and scenarios without database writer
            foreach (var annotation in annotations)
            {
                store.UpsertAnnotation(annotation);
            }
        }
    }

    private static JsonObject? CloneData(JsonObject? data)
    {
        return data?.DeepClone() as JsonObject;
    }

    private static string MapSeverity(AnalysisSeverity severity) => severity switch
    {
        AnalysisSeverity.Error => "error",
        AnalysisSeverity.Warning => "warning",
        AnalysisSeverity.Suggestion => "info",
        _ => "hint"
    };
}
