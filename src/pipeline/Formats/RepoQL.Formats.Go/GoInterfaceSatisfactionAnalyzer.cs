using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RepoQL.Contracts.Data;
using RepoQL.Contracts.Models;
using RepoQL.Indexing.Indexing.Pipelines;
using RepoQL.Indexing.Indexing.Pipelines.Analysis;

namespace RepoQL.Formats.Go;

/// <summary>
/// Purpose: Compute Go IMPLEMENTS relationships from package-wide graph state.
///
/// Complexity: Reads package data via IGraphReader, computes interface satisfaction
/// for structs in the current document, and returns IMPLEMENTS edges + diagnostics as output.
/// The pipeline commit stage handles persistence — this analyzer never writes directly.
/// </summary>
public sealed class GoInterfaceSatisfactionAnalyzer(
    IGraphReader graphReader,
    ILogger<GoInterfaceSatisfactionAnalyzer>? logger = null)
    : IAsyncPipeline<IAnnotatedArtifact, Annotation[]>
{
    private static readonly TimeSpan SlowAnalysisThreshold = TimeSpan.FromSeconds(5);
    private readonly IGraphReader _graphReader = graphReader ?? throw new ArgumentNullException(nameof(graphReader));
    private readonly ILogger<GoInterfaceSatisfactionAnalyzer> _logger = logger ?? NullLogger<GoInterfaceSatisfactionAnalyzer>.Instance;

    public async Task<(Annotation[]? Result, PipelineResult PipelineStatus)> ProcessAsync(
        IAnnotatedArtifact item,
        CallNextPipeline<IAnnotatedArtifact, Annotation[]> next,
        CancellationToken token)
    {
        var (downstreamAnnotations, downstreamStatus) = await next(item).ConfigureAwait(false);
        if (downstreamStatus != PipelineResult.Success)
        {
            return (downstreamAnnotations, downstreamStatus);
        }

        if (!GoMediaTypes.IsGoSourceKind(item.MediaType?.Kind) || item.Records is null)
        {
            return (downstreamAnnotations, PipelineResult.Success);
        }

        var documentNode = item.Records.Nodes.FirstOrDefault(n => string.Equals(n.Kind, GoNodeKinds.Document, StringComparison.Ordinal));
        if (documentNode is null)
        {
            return (downstreamAnnotations, PipelineResult.Success);
        }

        var packageName = documentNode.Props[GoPropertyKeys.PackageName]?.ToString();
        if (string.IsNullOrWhiteSpace(packageName))
        {
            return (downstreamAnnotations, PipelineResult.Success);
        }

        var candidateTypeIds = item.Records.Nodes
            .Where(IsStructTypeNode)
            .Select(n => n.Id)
            .Distinct()
            .ToArray();

        if (candidateTypeIds.Length == 0)
        {
            return (downstreamAnnotations, PipelineResult.Success);
        }

        var diagnostics = new List<Annotation>();
        var timer = Stopwatch.StartNew();

        try
        {
            var packageTypes = LoadPackageTypes(packageName, token);
            var packageMethods = LoadPackageMethods(packageName, token);
            var packageEmbeddings = LoadPackageEmbeddings(packageName, token);

            var result = GoInterfaceSatisfactionEngine.Compute(
                packageName,
                packageTypes,
                packageMethods,
                packageEmbeddings,
                candidateTypeIds);

            // Return edges as output — the pipeline commit stage merges them.
            // EdgesList on IndexItem follows the same pattern as AnnotationsList.
            if (item is IndexItem indexItem)
            {
                foreach (var implementation in result.Implementations)
                {
                    indexItem.AnalyzerEdges.Add(new Edge
                    {
                        SrcId = implementation.TypeNodeId,
                        DstId = implementation.InterfaceNodeId,
                        Type = GoEdgeTypes.Implements,
                        ScopeDocumentId = documentNode.Id,
                        EdgeKey = BuildSemanticKey(implementation),
                        Props = new JsonObject
                        {
                            [GoPropertyKeys.Target] = implementation.InterfaceQualifiedName,
                            [GoPropertyKeys.ReceiverKind] = implementation.ReceiverKind,
                            [GoPropertyKeys.IsStdlib] = implementation.IsStdlib
                        }
                    });
                }
            }

            foreach (var diagnostic in result.Diagnostics)
            {
                diagnostics.Add(new Annotation
                {
                    Kind = GoAnnotationKinds.InterfaceSatisfaction,
                    Severity = diagnostic.Severity,
                    Source = GoValues.AnnotationSource,
                    RuleId = diagnostic.RuleId,
                    Message = diagnostic.Message,
                    ScopeDocumentId = documentNode.Id,
                    Data = new JsonObject
                    {
                        [GoPropertyKeys.PackageName] = packageName
                    }
                });
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Go interface satisfaction failed for {Uri}", item.Uri);
            diagnostics.Add(new Annotation
            {
                Kind = GoAnnotationKinds.InterfaceSatisfaction,
                Severity = "warning",
                Source = GoValues.AnnotationSource,
                RuleId = "go.interface_satisfaction.failure",
                Message = $"Interface satisfaction failed: {ex.Message}",
                ScopeDocumentId = documentNode.Id,
                Data = new JsonObject
                {
                    [GoPropertyKeys.PackageName] = packageName
                }
            });
        }
        finally
        {
            timer.Stop();
        }

        if (timer.Elapsed > SlowAnalysisThreshold)
        {
            _logger.LogWarning(
                "Go interface satisfaction for {Uri} in package {Package} took {ElapsedSeconds:F2}s",
                item.Uri,
                packageName,
                timer.Elapsed.TotalSeconds);

            diagnostics.Add(new Annotation
            {
                Kind = GoAnnotationKinds.InterfaceSatisfaction,
                Severity = "warning",
                Source = GoValues.AnnotationSource,
                RuleId = "go.interface_satisfaction.slow",
                Message = $"Interface satisfaction exceeded {SlowAnalysisThreshold.TotalSeconds:F0}s ({timer.Elapsed.TotalSeconds:F2}s).",
                ScopeDocumentId = documentNode.Id,
                Data = new JsonObject
                {
                    [GoPropertyKeys.PackageName] = packageName
                }
            });
        }

        var merged = MergeAnnotations(downstreamAnnotations, diagnostics);
        return (merged, PipelineResult.Success);
    }

    private IReadOnlyList<GoTypeSnapshot> LoadPackageTypes(string packageName, CancellationToken token)
    {
        var packageLiteral = EscapeSqlLiteral(packageName);
        var sql = $"""
            SELECT
                t.id,
                COALESCE(t.properties->>'name', ''),
                COALESCE(t.properties->>'qualified_name', ''),
                COALESCE(t.properties->>'kind', '')
            FROM node t
            JOIN edge te
              ON te.destination_node_id = t.id
             AND te.type = '{GoEdgeTypes.HasPart}'
             AND te.is_composition = TRUE
            JOIN node doc
              ON doc.id = te.source_node_id
             AND doc.kind = '{GoNodeKinds.Document}'
            WHERE t.kind = '{GoNodeKinds.Type}'
              AND COALESCE(doc.properties->>'package_name', '') = '{packageLiteral}'
              AND COALESCE(t.properties->>'kind', '') IN ('struct', 'interface')
            """;

        return _graphReader.Read(
            sql,
            record => new GoTypeSnapshot(
                Id: record.GetGuid(0),
                Name: ReadString(record, 1),
                QualifiedName: ReadString(record, 2),
                Kind: ReadString(record, 3)),
            token);
    }

    private IReadOnlyList<GoMethodSnapshot> LoadPackageMethods(string packageName, CancellationToken token)
    {
        var packageLiteral = EscapeSqlLiteral(packageName);
        var sql = $"""
            SELECT
                COALESCE(m.properties->>'name', ''),
                COALESCE(m.properties->>'declaring_type', ''),
                m.properties->>'parameters',
                COALESCE(m.properties->>'is_pointer_receiver', 'false') = 'true'
            FROM node m
            JOIN edge me
              ON me.destination_node_id = m.id
             AND me.type = '{GoEdgeTypes.HasPart}'
             AND me.is_composition = TRUE
            JOIN node doc
              ON doc.id = me.scope_document_id
             AND doc.kind = '{GoNodeKinds.Document}'
            WHERE m.kind = '{GoNodeKinds.Member}'
              AND COALESCE(m.properties->>'kind', '') = 'method'
              AND COALESCE(doc.properties->>'package_name', '') = '{packageLiteral}'
            """;

        return _graphReader.Read(
            sql,
            record => new GoMethodSnapshot(
                Name: ReadString(record, 0),
                DeclaringType: ReadString(record, 1),
                Parameters: record.IsDBNull(2) ? null : ReadString(record, 2),
                IsPointerReceiver: ReadBoolean(record, 3)),
            token);
    }

    private IReadOnlyList<GoEmbeddingSnapshot> LoadPackageEmbeddings(string packageName, CancellationToken token)
    {
        var packageLiteral = EscapeSqlLiteral(packageName);
        var sql = $"""
            SELECT
                e.source_node_id,
                COALESCE(e.properties->>'target', '')
            FROM edge e
            JOIN node src
              ON src.id = e.source_node_id
             AND src.kind = '{GoNodeKinds.Type}'
            JOIN edge se
              ON se.destination_node_id = src.id
             AND se.type = '{GoEdgeTypes.HasPart}'
             AND se.is_composition = TRUE
            JOIN node doc
              ON doc.id = se.source_node_id
             AND doc.kind = '{GoNodeKinds.Document}'
            WHERE e.type = '{GoEdgeTypes.Embeds}'
              AND COALESCE(doc.properties->>'package_name', '') = '{packageLiteral}'
            """;

        return _graphReader.Read(
            sql,
            record => new GoEmbeddingSnapshot(
                SourceTypeId: record.GetGuid(0),
                Target: ReadString(record, 1)),
            token);
    }

    private static string BuildSemanticKey(GoInterfaceImplementation implementation)
    {
        return FormattableString.Invariant(
            $"go.implements:{implementation.TypeNodeId:N}:{implementation.InterfaceQualifiedName}:{implementation.ReceiverKind}:{implementation.IsStdlib}");
    }

    private static Annotation[]? MergeAnnotations(Annotation[]? downstream, List<Annotation> additional)
    {
        if ((downstream is null || downstream.Length == 0) && additional.Count == 0)
            return downstream;
        if (downstream is null || downstream.Length == 0)
            return additional.ToArray();
        if (additional.Count == 0)
            return downstream;

        var merged = new Annotation[downstream.Length + additional.Count];
        downstream.CopyTo(merged, 0);
        for (var i = 0; i < additional.Count; i++)
            merged[downstream.Length + i] = additional[i];
        return merged;
    }

    private static bool IsStructTypeNode(Node node)
    {
        if (!string.Equals(node.Kind, GoNodeKinds.Type, StringComparison.Ordinal))
            return false;
        return string.Equals(node.Props[GoPropertyKeys.Kind]?.ToString(), "struct", StringComparison.Ordinal);
    }

    private static string EscapeSqlLiteral(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    private static string ReadString(IDataRecord record, int ordinal)
    {
        if (record.IsDBNull(ordinal))
            return string.Empty;
        return Convert.ToString(record.GetValue(ordinal), CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static bool ReadBoolean(IDataRecord record, int ordinal)
    {
        if (record.IsDBNull(ordinal))
            return false;
        var value = record.GetValue(ordinal);
        return value switch
        {
            bool boolValue => boolValue,
            string text => string.Equals(text, "true", StringComparison.OrdinalIgnoreCase),
            _ => bool.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out var parsed) && parsed
        };
    }
}
