using System.Text;
using RepoQL.Commands;
using RepoQL.ConsoleApp.Helpers;
using RepoQL.Contracts;

namespace RepoQL.ConsoleApp.CommandImplementations;

/// <summary>
/// Purpose: On-demand parsing of any file through the hot-path pipeline without persisting results.
/// Complexity: Reads file bytes, sends to PreviewDocumentAsync, formats at three detail levels
/// (overview → graph → records) for iterative format development.
/// </summary>
[CommandClass]
internal sealed class ParseCommand(RepoQlClientProvider clientProvider)
{
    [Command("parse", Description = "Parse a file and show its representations")]
    public async Task<CommandResult> Execute(
        [CommandParam("Absolute file path")] string path,
        [CommandParam("View: overview (default), graph, records")] string? view,
        CancellationToken cancel)
    {
        if (string.IsNullOrWhiteSpace(path))
            return CommandResult.Error("File path is required. Usage: ::parse[C:\\path\\to\\file.cs]");

        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
            return CommandResult.Error($"File not found: {fullPath}");

        try
        {
            var content = await File.ReadAllBytesAsync(fullPath, cancel).ConfigureAwait(false);
            var fileName = Path.GetFileName(fullPath);
            var uri = $"file:///{fullPath.Replace('\\', '/')}";

            var client = await clientProvider.GetClientAsync(cancel).ConfigureAwait(false);
            var result = await client.PreviewDocumentAsync(uri, content, fileName, cancellationToken: cancel).ConfigureAwait(false);

            if (!result.Success)
            {
                var sb = new StringBuilder();
                sb.AppendLine($"Parse failed: {result.Error}");
                AppendStages(sb, result);
                return CommandResult.Error(sb.ToString().TrimEnd());
            }

            return (view?.ToLowerInvariant()) switch
            {
                "graph" => FormatGraph(result, fileName, content.Length),
                "records" => FormatRecords(result, fileName, content.Length),
                _ => FormatOverview(result, fileName, content.Length),
            };
        }
        catch (Exception ex)
        {
            return CommandResult.Error($"Parse failed: {ex.Message}");
        }
    }

    // --- Overview: shape + summaries + counts ---

    private static CommandResult FormatOverview(PreviewDocumentResponse result, string fileName, int fileSize)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Parsed: {fileName} ({result.MediaType}, {fileSize:N0} bytes)");

        if (result.Records?.Artifacts.Count > 0)
        {
            foreach (var artifact in result.Records.Artifacts)
            {
                if (!string.IsNullOrWhiteSpace(artifact.Headline))
                {
                    sb.AppendLine();
                    sb.AppendLine("Headline:");
                    sb.AppendLine(artifact.Headline);
                }

                if (!string.IsNullOrWhiteSpace(artifact.Structure))
                {
                    sb.AppendLine();
                    sb.AppendLine("Structure:");
                    sb.AppendLine(artifact.Structure);
                }

                if (!string.IsNullOrWhiteSpace(artifact.Summary))
                {
                    sb.AppendLine();
                    sb.AppendLine("Summary:");
                    sb.AppendLine(artifact.Summary);
                }
            }
        }

        var records = result.Records;
        if (records is not null)
        {
            sb.AppendLine();
            sb.AppendLine("Graph:");
            sb.AppendLine($"  {records.Nodes.Count} nodes, {records.Edges.Count} edges, {records.Spans.Count} spans, {records.Annotations.Count} annotations");

            var kinds = records.Nodes
                .GroupBy(n => n.Kind)
                .OrderByDescending(g => g.Count())
                .Select(g => $"{g.Key}({g.Count()})");
            sb.AppendLine($"  Nodes: {string.Join(", ", kinds)}");

            if (records.Edges.Count > 0)
            {
                var edgeTypes = records.Edges
                    .GroupBy(e => e.Type)
                    .OrderByDescending(g => g.Count())
                    .Select(g => $"{g.Key}({g.Count()})");
                sb.AppendLine($"  Edges: {string.Join(", ", edgeTypes)}");
            }

            if (records.Annotations.Count > 0)
            {
                var annoKinds = records.Annotations
                    .GroupBy(a => a.Kind)
                    .OrderByDescending(g => g.Count())
                    .Select(g => $"{g.Key}({g.Count()})");
                sb.AppendLine($"  Annotations: {string.Join(", ", annoKinds)}");
            }
        }

        AppendStages(sb, result);
        return CommandResult.Success(sb.ToString().TrimEnd());
    }

    // --- Graph: composition tree + references + annotations ---

    private static CommandResult FormatGraph(PreviewDocumentResponse result, string fileName, int fileSize)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Parsed: {fileName} ({result.MediaType}, {fileSize:N0} bytes)");

        var records = result.Records;
        if (records is null || records.Nodes.Count == 0)
        {
            sb.AppendLine("  (no graph nodes)");
            AppendStages(sb, result);
            return CommandResult.Success(sb.ToString().TrimEnd());
        }

        var nodeById = records.Nodes.ToDictionary(n => n.Id);
        var spanById = records.Spans.ToDictionary(s => s.Id);

        // Build composition tree
        var compositionEdges = records.Edges.Where(e => e.IsComposition).OrderBy(e => e.Ordinal).ToList();
        var referenceEdges = records.Edges.Where(e => !e.IsComposition).ToList();

        var childrenOf = new Dictionary<string, List<PreviewNode>>();
        var hasParent = new HashSet<string>();

        foreach (var edge in compositionEdges)
        {
            if (!nodeById.TryGetValue(edge.DstId, out var child)) continue;
            if (!childrenOf.ContainsKey(edge.SrcId))
                childrenOf[edge.SrcId] = [];
            childrenOf[edge.SrcId].Add(child);
            hasParent.Add(edge.DstId);
        }

        var roots = records.Nodes.Where(n => !hasParent.Contains(n.Id)).ToList();

        sb.AppendLine();
        sb.AppendLine("Composition tree:");
        foreach (var root in roots)
            RenderNode(sb, root, 1, childrenOf, spanById);

        // Reference edges
        if (referenceEdges.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("References:");
            foreach (var edge in referenceEdges)
            {
                var srcLabel = nodeById.TryGetValue(edge.SrcId, out var src) ? NodeLabel(src) : "?";
                var dstLabel = nodeById.TryGetValue(edge.DstId, out var dst) ? NodeLabel(dst) : "?";
                var spanInfo = "";
                if (!string.IsNullOrEmpty(edge.SrcSpanId) && spanById.TryGetValue(edge.SrcSpanId, out var srcSpan) && srcSpan.StartLine > 0)
                    spanInfo = $"  L{srcSpan.StartLine}";
                sb.AppendLine($"  {edge.Type,-16} {srcLabel} → {dstLabel}{spanInfo}");
            }
        }

        // Annotations
        if (records.Annotations.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Annotations:");
            foreach (var a in records.Annotations)
            {
                var msg = string.IsNullOrWhiteSpace(a.Message) ? "" : $"  {Truncate(a.Message, 80)}";
                sb.AppendLine($"  [{a.Kind}] {a.Severity}{msg}");
            }
        }

        AppendStages(sb, result);
        return CommandResult.Success(sb.ToString().TrimEnd());
    }

    private static void RenderNode(StringBuilder sb, PreviewNode node, int depth,
        Dictionary<string, List<PreviewNode>> childrenOf,
        Dictionary<string, PreviewSpan> spanById)
    {
        var indent = new string(' ', depth * 2);
        var label = NodeLabel(node);
        var spanInfo = "";
        if (!string.IsNullOrEmpty(node.SpanId) && spanById.TryGetValue(node.SpanId, out var span) && span.StartLine > 0)
            spanInfo = $"  L{span.StartLine}-{span.EndLine}";

        sb.AppendLine($"{indent}{node.Kind,-16} {label}{spanInfo}");

        if (childrenOf.TryGetValue(node.Id, out var children))
        {
            foreach (var child in children)
                RenderNode(sb, child, depth + 1, childrenOf, spanById);
        }
    }

    // --- Records: database-level detail with cross-referencing ---

    private static CommandResult FormatRecords(PreviewDocumentResponse result, string fileName, int fileSize)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Parsed: {fileName} ({result.MediaType}, {fileSize:N0} bytes)");

        var records = result.Records;
        if (records is null)
        {
            sb.AppendLine("  (no records)");
            AppendStages(sb, result);
            return CommandResult.Success(sb.ToString().TrimEnd());
        }

        // Build index maps for cross-referencing
        var nodeIndex = new Dictionary<string, int>();
        for (var i = 0; i < records.Nodes.Count; i++)
            nodeIndex[records.Nodes[i].Id] = i;

        var spanIndex = new Dictionary<string, int>();
        for (var i = 0; i < records.Spans.Count; i++)
            spanIndex[records.Spans[i].Id] = i;

        // Artifacts
        if (records.Artifacts.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"Artifacts ({records.Artifacts.Count}):");
            foreach (var a in records.Artifacts)
            {
                sb.AppendLine($"  media_type={a.MediaType}  size={a.SizeBytes:N0}");
                if (!string.IsNullOrWhiteSpace(a.Headline))
                    sb.AppendLine($"    headline: {a.Headline}");
                if (!string.IsNullOrWhiteSpace(a.Structure))
                {
                    sb.AppendLine("    structure:");
                    foreach (var line in a.Structure.Split('\n'))
                        sb.AppendLine($"      {line.TrimEnd()}");
                }
                if (!string.IsNullOrWhiteSpace(a.Summary))
                    sb.AppendLine($"    summary: {Truncate(a.Summary, 120)}");
            }
        }

        // Nodes
        if (records.Nodes.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"Nodes ({records.Nodes.Count}):");
            for (var i = 0; i < records.Nodes.Count; i++)
            {
                var n = records.Nodes[i];
                var spanRef = !string.IsNullOrEmpty(n.SpanId) && spanIndex.TryGetValue(n.SpanId, out var si) ? $"  span=S{si}" : "";
                var headline = !string.IsNullOrWhiteSpace(n.Headline) ? $"  {Truncate(n.Headline, 60)}" : "";
                sb.AppendLine($"  N{i,-4} {n.Kind,-16}{headline}{spanRef}");
                if (!string.IsNullOrWhiteSpace(n.PropsJson) && n.PropsJson != "{}")
                    sb.AppendLine($"        props: {n.PropsJson}");
            }
        }

        // Edges
        if (records.Edges.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"Edges ({records.Edges.Count}):");
            for (var i = 0; i < records.Edges.Count; i++)
            {
                var e = records.Edges[i];
                var srcRef = nodeIndex.TryGetValue(e.SrcId, out var srcIdx) ? $"N{srcIdx}" : "?";
                var dstRef = nodeIndex.TryGetValue(e.DstId, out var dstIdx) ? $"N{dstIdx}" : "?";
                var comp = e.IsComposition ? " composition" : "";
                var ord = e.Ordinal > 0 ? $" ord={e.Ordinal}" : "";
                var srcSpanRef = !string.IsNullOrEmpty(e.SrcSpanId) && spanIndex.TryGetValue(e.SrcSpanId, out var ssi) ? $" src_span=S{ssi}" : "";
                var dstSpanRef = !string.IsNullOrEmpty(e.DstSpanId) && spanIndex.TryGetValue(e.DstSpanId, out var dsi) ? $" dst_span=S{dsi}" : "";
                sb.AppendLine($"  E{i,-4} {e.Type,-16} {srcRef} → {dstRef}{comp}{ord}{srcSpanRef}{dstSpanRef}");
                if (!string.IsNullOrWhiteSpace(e.PropsJson) && e.PropsJson != "{}")
                    sb.AppendLine($"        props: {e.PropsJson}");
            }
        }

        // Spans
        if (records.Spans.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"Spans ({records.Spans.Count}):");
            for (var i = 0; i < records.Spans.Count; i++)
            {
                var s = records.Spans[i];
                var lines = s.StartLine > 0 ? $"L{s.StartLine}-{s.EndLine}" : "";
                var cols = s.StartColumn > 0 ? $"  col {s.StartColumn}-{s.EndColumn}" : "";
                sb.AppendLine($"  S{i,-4} {lines,-12} bytes {s.StartByte}-{s.EndByte}{cols}");
            }
        }

        // Annotations
        if (records.Annotations.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"Annotations ({records.Annotations.Count}):");
            foreach (var a in records.Annotations)
            {
                var source = !string.IsNullOrWhiteSpace(a.Source) ? $"  source={a.Source}" : "";
                var rule = !string.IsNullOrWhiteSpace(a.RuleId) ? $"  rule={a.RuleId}" : "";
                sb.AppendLine($"  [{a.Kind}] {a.Severity}{source}{rule}");
                if (!string.IsNullOrWhiteSpace(a.Message))
                {
                    foreach (var line in a.Message.Split('\n').Take(10))
                        sb.AppendLine($"    {line.TrimEnd()}");
                }
            }
        }

        AppendStages(sb, result);
        return CommandResult.Success(sb.ToString().TrimEnd());
    }

    // --- Helpers ---

    private static string NodeLabel(PreviewNode node)
    {
        if (!string.IsNullOrWhiteSpace(node.Headline))
            return Truncate(node.Headline, 60);
        return node.Kind;
    }

    private static string Truncate(string text, int maxLen)
    {
        var firstLine = text.Split('\n')[0].Trim();
        return firstLine.Length <= maxLen ? firstLine : firstLine[..(maxLen - 1)] + "…";
    }

    private static void AppendStages(StringBuilder sb, PreviewDocumentResponse result)
    {
        if (result.Stages.Count == 0) return;

        sb.AppendLine();
        sb.AppendLine("Stages:");
        var maxName = result.Stages.Max(s => s.Stage.Length);
        foreach (var stage in result.Stages)
        {
            var status = stage.Status == "Completed" ? "" : $" [{stage.Status}]";
            var error = string.IsNullOrWhiteSpace(stage.Error) ? "" : $" — {stage.Error}";
            sb.AppendLine($"  {stage.Stage.PadRight(maxName)}  {stage.DurationMs,5}ms{status}{error}");
        }
    }
}
