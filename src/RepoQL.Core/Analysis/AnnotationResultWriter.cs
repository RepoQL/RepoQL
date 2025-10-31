using System.Text.Json.Nodes;
using RepoQL.Contracts;
using RepoQL.Contracts.Analysis;
using RepoQL.Contracts.Data;
using RepoQL.Contracts.Models;

namespace RepoQL.Core.Analysis;

public sealed class AnnotationResultWriter(IGraphStore store, IDatabaseWriter? writer = null) : IAnalysisResultWriter
{
    public async Task WriteAsync(
        string containerUri,
        IReadOnlyList<AnalysisResult> results,
        IReadOnlyCollection<string>? analyzerSources = null,
        CancellationToken cancellationToken = default)
    {
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

        var annotations = new List<Annotation>(results.Count);
        var sourcesToClear = new HashSet<string>(StringComparer.Ordinal);
        if (analyzerSources is not null)
        {
            foreach (var src in analyzerSources)
            {
                if (!string.IsNullOrWhiteSpace(src))
                    sourcesToClear.Add(src);
            }
        }

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
            if (!string.IsNullOrWhiteSpace(annotation.Source))
                sourcesToClear.Add(annotation.Source);
        }

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
                    Annotations = [.. annotations],
                    AnnotationSources = sourcesToClear.ToArray()
                }
            };

            await writer.EnqueueAsync(operation, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            ClearStaleAnnotations(store, document.Id, sourcesToClear, annotations);
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

    private static void ClearStaleAnnotations(
        IGraphStore store,
        Guid documentId,
        HashSet<string> sourcesToClear,
        IReadOnlyList<Annotation> newAnnotations)
    {
        if (sourcesToClear.Count == 0)
            return;

        var newKeys = new HashSet<string>(StringComparer.Ordinal);
        var includeNullKey = false;
        foreach (var annotation in newAnnotations)
        {
            if (!string.IsNullOrEmpty(annotation.SemanticKey))
            {
                newKeys.Add(annotation.SemanticKey);
            }
            else
            {
                includeNullKey = true;
            }
        }

        var existing = store.GetAnnotationsForDocument(documentId).ToList();
        foreach (var stale in existing)
        {
            if (string.IsNullOrEmpty(stale.Source))
                continue;
            if (!sourcesToClear.Contains(stale.Source))
                continue;

            var key = stale.SemanticKey;
            if (!string.IsNullOrEmpty(key) && newKeys.Contains(key))
                continue;

            if (string.IsNullOrEmpty(key) && includeNullKey)
                continue;

            store.DeleteAnnotation(stale.Id);
        }
    }

    private static string MapSeverity(AnalysisSeverity severity) => severity switch
    {
        AnalysisSeverity.Error => "error",
        AnalysisSeverity.Warning => "warning",
        AnalysisSeverity.Suggestion => "info",
        _ => "hint"
    };
}
