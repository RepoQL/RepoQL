using System.Text.Json.Nodes;
using RepoQL.Contracts;
using RepoQL.Contracts.Analysis;
using RepoQL.Contracts.Data;
using RepoQL.Contracts.Models;

namespace RepoQL.Core.Analysis;

public sealed class AnnotationResultWriter(IGraphStore store) : IAnalysisResultWriter
{
    public Task WriteAsync(string containerUri, IReadOnlyList<AnalysisResult> results, CancellationToken cancellationToken = default)
    {
        if (results.Count == 0)
            return Task.CompletedTask;

        RepoUri repoUri;
        try
        {
            repoUri = RepoUri.Parse(containerUri);
        }
        catch
        {
            return Task.CompletedTask;
        }

        var document = store.GetDocumentByUri(repoUri);
        if (document is null)
            return Task.CompletedTask;

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

            store.UpsertAnnotation(annotation);
        }

        return Task.CompletedTask;
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
