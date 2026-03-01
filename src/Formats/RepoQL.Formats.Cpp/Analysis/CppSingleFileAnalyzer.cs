using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RepoQL.Contracts;
using RepoQL.Contracts.Models;
using RepoQL.Indexing.Indexing.Pipelines;
using RepoQL.Indexing.Indexing.Pipelines.Parsing;

namespace RepoQL.Formats.Cpp.Analysis;

/// <summary>
/// Single-file analysis processor for C/C++ parsed records.
///
/// Purpose: Add include reference edges and enrich nodes with comments, attributes, and test metadata.
///
/// Complexity: Multi-step analysis with per-step isolation and partial-result continuation.
/// </summary>
public sealed partial class CppSingleFileAnalyzer(
    UriRegistry? uriRegistry = null,
    ILogger<CppSingleFileAnalyzer>? logger = null)
    : IAsyncPipeline<IParsedArtifact, Annotation[]>
{
    private static readonly string[] StandardAttributes =
    [
        "nodiscard",
        "deprecated",
        "maybe_unused",
        "fallthrough",
        "likely",
        "unlikely"
    ];

    private readonly UriRegistry? _uriRegistry = uriRegistry;
    private readonly ILogger<CppSingleFileAnalyzer> _logger = logger ?? NullLogger<CppSingleFileAnalyzer>.Instance;

    public async Task<(Annotation[]? Result, PipelineResult PipelineStatus)> ProcessAsync(
        IParsedArtifact item,
        CallNextPipeline<IParsedArtifact, Annotation[]> next,
        CancellationToken token)
    {
        var (downstreamAnnotations, downstreamStatus) = await next(item).ConfigureAwait(false);
        if (downstreamStatus != PipelineResult.Success)
        {
            return (downstreamAnnotations, downstreamStatus);
        }

        if (!CppMediaTypes.IsSupportedKind(item.MediaType?.Kind) || item.Records is null)
        {
            return (downstreamAnnotations, PipelineResult.Success);
        }

        var records = item.Records;
        var documentNode = records.Nodes.FirstOrDefault(n => string.Equals(n.Kind, CppNodeKinds.Document, StringComparison.Ordinal));
        if (documentNode is null)
        {
            return (downstreamAnnotations, PipelineResult.Success);
        }

        var annotations = new List<Annotation>();
        var workingRecords = records;

        // Read source text once and normalize lines once — avoids re-allocating the
        // line array in every analysis step that needs it.
        string sourceText;
        using (var stream = item.CreateReadStream())
        using (var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: false))
        {
            sourceText = reader.ReadToEnd();
        }

        var sourceLines = NormalizeLines(sourceText);

        void RunStep(string stepName, Action action)
        {
            try
            {
                action();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "C++ single-file analysis step failed for {Uri}: {Step}", item.Uri, stepName);
                annotations.Add(CreateAnalysisFailure(stepName, ex.Message, documentNode.Id));
            }
        }

        RunStep("include_edges", () =>
        {
            workingRecords = AddIncludeEdges(item, workingRecords, documentNode);
        });

        RunStep("doc_comments", () =>
        {
            ApplyDocComments(workingRecords, sourceLines);
        });

        RunStep("attributes", () =>
        {
            ApplyAttributes(workingRecords, sourceLines);
        });

        RunStep("test_framework_detection", () =>
        {
            annotations.AddRange(DetectTestFramework(workingRecords, sourceText, documentNode.Id));
        });

        if (!ReferenceEquals(workingRecords, records))
        {
            TrySetRecords(item, workingRecords);
        }

        var merged = MergeAnnotations(downstreamAnnotations, annotations);
        return (merged, PipelineResult.Success);
    }

    private Records AddIncludeEdges(IParsedArtifact item, Records records, Node documentNode)
    {
        var includeNodes = records.Nodes
            .Where(n => string.Equals(n.Kind, CppNodeKinds.Include, StringComparison.Ordinal))
            .ToArray();
        if (includeNodes.Length == 0)
        {
            return records;
        }

        var edges = records.Edges.ToList();
        foreach (var include in includeNodes)
        {
            var target = include.Props[CppPropertyKeys.Target]?.ToString();
            if (string.IsNullOrWhiteSpace(target))
            {
                continue;
            }

            if (edges.Any(e =>
                    string.Equals(e.Type, CppEdgeTypes.RefersTo, StringComparison.Ordinal)
                    && e.SrcId == include.Id
                    && string.Equals(e.Props[CppPropertyKeys.Target]?.ToString(), target, StringComparison.Ordinal)))
            {
                continue;
            }

            var style = include.Props[CppPropertyKeys.Style]?.ToString();
            if (string.IsNullOrWhiteSpace(style))
            {
                style = target.StartsWith('<') ? "<>" : "\"\"";
            }

            RepoUri? targetUri = null;
            Guid? targetDocumentId = null;
            var isResolved = false;

            if (string.Equals(style, "\"\"", StringComparison.Ordinal))
            {
                targetUri = ResolveLocalInclude(item.Uri, target);
                if (targetUri is not null)
                {
                    targetDocumentId = records.Nodes
                        .FirstOrDefault(n =>
                            string.Equals(n.Kind, CppNodeKinds.Document, StringComparison.Ordinal)
                            && UriEqualsContainer(n.Uri, targetUri))?.Id;

                    if (targetDocumentId.HasValue)
                    {
                        isResolved = true;
                    }
                    else if (_uriRegistry is not null && _uriRegistry.ContainsKey(targetUri))
                    {
                        isResolved = true;
                    }
                }
            }

            edges.Add(new Edge
            {
                Id = Guid.NewGuid(),
                SrcId = include.Id,
                DstId = targetDocumentId,
                DstUri = targetUri,
                Type = CppEdgeTypes.RefersTo,
                IsComposition = false,
                ScopeDocumentId = documentNode.Id,
                CreatedAt = DateTimeOffset.UtcNow,
                Props = new JsonObject
                {
                    [CppPropertyKeys.Target] = target,
                    [CppPropertyKeys.Style] = style,
                    [CppPropertyKeys.IsResolved] = isResolved ? "true" : "false"
                }
            });
        }

        if (edges.Count == records.Edges.Length)
        {
            return records;
        }

        return new Records
        {
            Artifacts = records.Artifacts,
            Nodes = records.Nodes,
            Spans = records.Spans,
            Edges = [..edges],
            Annotations = records.Annotations,
            AnnotationSources = records.AnnotationSources
        };
    }

    private static void ApplyDocComments(Records records, string[] lines)
    {
        if (lines.Length == 0)
        {
            return;
        }

        var commentsByLine = ExtractDocCommentsByNextLine(lines);
        if (commentsByLine.Count == 0)
        {
            return;
        }

        var spanById = records.Spans.ToDictionary(s => s.Id);
        foreach (var node in records.Nodes.Where(IsDeclarationLikeNode))
        {
            if (!node.SpanId.HasValue || !spanById.TryGetValue(node.SpanId.Value, out var span))
            {
                continue;
            }

            if (!span.StartLine.HasValue || !commentsByLine.TryGetValue(span.StartLine.Value, out var comment))
            {
                continue;
            }

            node.Props[CppPropertyKeys.DocComment] = comment.Text;
            node.Props[CppPropertyKeys.DocTags] = comment.Tags;
        }
    }

    private static void ApplyAttributes(Records records, string[] lines)
    {
        if (lines.Length == 0)
        {
            return;
        }

        var spanById = records.Spans.ToDictionary(s => s.Id);
        foreach (var node in records.Nodes.Where(IsDeclarationLikeNode))
        {
            if (!node.SpanId.HasValue || !spanById.TryGetValue(node.SpanId.Value, out var span))
            {
                continue;
            }

            if (!span.StartLine.HasValue)
            {
                continue;
            }

            var prefix = GetPrefixText(lines, span.StartLine.Value, 5);
            if (string.IsNullOrWhiteSpace(prefix))
            {
                continue;
            }

            var standard = ExtractStandardAttributes(prefix);
            if (standard.Count > 0)
            {
                node.Props[CppPropertyKeys.Attributes] = standard;
            }

            var vendor = ExtractVendorAttributes(prefix);
            if (vendor.Count > 0)
            {
                node.Props[CppPropertyKeys.VendorAttributes] = vendor;
            }
        }
    }

    private static IReadOnlyList<Annotation> DetectTestFramework(Records records, string source, Guid documentId)
    {
        var annotations = new List<Annotation>();
        if (string.IsNullOrWhiteSpace(source))
        {
            return annotations;
        }

        var spanById = records.Spans.ToDictionary(s => s.Id);
        var hits = FindTestMacroHits(source);
        foreach (var hit in hits)
        {
            var target = records.Nodes.FirstOrDefault(node =>
                IsDeclarationLikeNode(node)
                && node.SpanId.HasValue
                && spanById.TryGetValue(node.SpanId.Value, out var span)
                && span.StartLine.HasValue
                && span.EndLine.HasValue
                && span.StartLine.Value <= hit.Line
                && span.EndLine.Value >= hit.Line)
                         ?? records.Nodes.FirstOrDefault(node => string.Equals(node.Kind, CppNodeKinds.Document, StringComparison.Ordinal));
            if (target is null)
            {
                continue;
            }

            target.Props[CppPropertyKeys.IsTest] = "true";
            if (!string.IsNullOrWhiteSpace(hit.Suite))
            {
                target.Props[CppPropertyKeys.TestSuite] = hit.Suite;
            }
            target.Props[CppPropertyKeys.TestName] = hit.Name;

            annotations.Add(new Annotation
            {
                Kind = "lint",
                Severity = "info",
                Source = CppValues.AnalyzerAnnotationSource,
                RuleId = CppAnnotationRuleIds.TestFramework,
                Message = $"Detected {hit.Framework} test macro '{hit.Name}'.",
                ScopeDocumentId = documentId,
                TargetNodeId = target.Id,
                CreatedAt = DateTimeOffset.UtcNow,
                Data = new JsonObject
                {
                    [CppPropertyKeys.TestSuite] = hit.Suite ?? string.Empty,
                    [CppPropertyKeys.TestName] = hit.Name,
                    [CppPropertyKeys.StartLine] = hit.Line,
                    [CppPropertyKeys.EndLine] = hit.Line
                }
            });
        }

        return annotations;
    }

    private static JsonArray ExtractStandardAttributes(string text)
    {
        var attributes = new JsonArray();
        for (var match = DoubleBracketAttributeRegex().Match(text); match.Success; match = match.NextMatch())
        {
            foreach (var token in SplitTopLevel(match.Groups["body"].Value, ','))
            {
                var normalized = token.Trim();
                if (string.IsNullOrWhiteSpace(normalized))
                {
                    continue;
                }

                var name = normalized.Split('(', 2)[0].Trim();
                if (!StandardAttributes.Contains(name, StringComparer.Ordinal))
                {
                    continue;
                }

                var deprecated = DeprecatedWithReasonRegex().Match(normalized);
                if (deprecated.Success)
                {
                    attributes.Add((JsonNode)new JsonObject
                    {
                        [CppPropertyKeys.Name] = "deprecated",
                        ["reason"] = deprecated.Groups["reason"].Value
                    });
                    continue;
                }

                attributes.Add(name);
            }
        }

        return attributes;
    }

    private static JsonArray ExtractVendorAttributes(string text)
    {
        var attributes = new JsonArray();
        for (var match = GnuAttributeRegex().Match(text); match.Success; match = match.NextMatch())
        {
            var value = match.Groups["value"].Value.Trim();
            if (!string.IsNullOrWhiteSpace(value))
            {
                attributes.Add($"__attribute__(({value}))");
            }
        }

        for (var match = DeclspecAttributeRegex().Match(text); match.Success; match = match.NextMatch())
        {
            var value = match.Groups["value"].Value.Trim();
            if (!string.IsNullOrWhiteSpace(value))
            {
                attributes.Add($"__declspec({value})");
            }
        }

        return attributes;
    }

    private static Dictionary<int, DocCommentInfo> ExtractDocCommentsByNextLine(string[] lines)
    {
        var comments = new Dictionary<int, DocCommentInfo>();

        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith("///", StringComparison.Ordinal))
            {
                var collected = new List<string>();
                var j = i;
                while (j < lines.Length && lines[j].TrimStart().StartsWith("///", StringComparison.Ordinal))
                {
                    var lineText = lines[j].TrimStart()[3..].TrimStart();
                    collected.Add(lineText);
                    j++;
                }

                var targetLine = FindNextDeclarationLine(lines, j);
                if (targetLine > 0 && !comments.ContainsKey(targetLine))
                {
                    var text = string.Join('\n', collected).Trim();
                    comments[targetLine] = new DocCommentInfo(text, ParseDocTags(text));
                }

                i = j - 1;
                continue;
            }

            if (trimmed.Contains("/**", StringComparison.Ordinal))
            {
                var block = new List<string>();
                var j = i;
                while (j < lines.Length)
                {
                    block.Add(lines[j]);
                    if (lines[j].Contains("*/", StringComparison.Ordinal))
                    {
                        break;
                    }
                    j++;
                }

                var normalized = NormalizeBlockComment(block);
                var targetLine = FindNextDeclarationLine(lines, j + 1);
                if (targetLine > 0 && !comments.ContainsKey(targetLine))
                {
                    comments[targetLine] = new DocCommentInfo(normalized, ParseDocTags(normalized));
                }

                i = j;
            }
        }

        return comments;
    }

    private static JsonArray ParseDocTags(string docComment)
    {
        var tags = new JsonArray();
        var lines = NormalizeLines(docComment);
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            var match = DocTagRegex().Match(line);
            if (!match.Success)
            {
                continue;
            }

            var tag = match.Groups["tag"].Value;
            var rest = match.Groups["rest"].Value.Trim();
            if (string.Equals(tag, "param", StringComparison.Ordinal))
            {
                var parts = rest.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                tags.Add((JsonNode)new JsonObject
                {
                    ["tag"] = tag,
                    [CppPropertyKeys.Name] = parts.Length > 0 ? parts[0] : string.Empty,
                    ["text"] = parts.Length > 1 ? parts[1] : string.Empty
                });
                continue;
            }

            tags.Add((JsonNode)new JsonObject
            {
                ["tag"] = tag,
                ["text"] = rest
            });
        }

        return tags;
    }

    private static List<TestMacroHit> FindTestMacroHits(string source)
    {
        var hits = new List<TestMacroHit>();

        for (var match = GoogleTestMacroRegex().Match(source); match.Success; match = match.NextMatch())
        {
            hits.Add(new TestMacroHit(
                Framework: match.Groups["macro"].Value,
                Suite: match.Groups["suite"].Value,
                Name: match.Groups["name"].Value,
                Line: GetLineNumber(source, match.Index)));
        }

        for (var match = Catch2TestCaseRegex().Match(source); match.Success; match = match.NextMatch())
        {
            hits.Add(new TestMacroHit(
                Framework: "TEST_CASE",
                Suite: null,
                Name: match.Groups["name"].Value,
                Line: GetLineNumber(source, match.Index)));
        }

        for (var match = Catch2SectionRegex().Match(source); match.Success; match = match.NextMatch())
        {
            hits.Add(new TestMacroHit(
                Framework: "SECTION",
                Suite: null,
                Name: match.Groups["name"].Value,
                Line: GetLineNumber(source, match.Index)));
        }

        return hits;
    }

    private static Annotation CreateAnalysisFailure(string stepName, string message, Guid documentId)
    {
        return new Annotation
        {
            Kind = "lint",
            Severity = "warning",
            Source = CppValues.AnalyzerAnnotationSource,
            RuleId = CppAnnotationRuleIds.AnalysisFailure,
            Message = $"C++ analysis step '{stepName}' failed: {message}",
            ScopeDocumentId = documentId,
            CreatedAt = DateTimeOffset.UtcNow,
            Data = new JsonObject
            {
                [CppPropertyKeys.Step] = stepName,
                [CppPropertyKeys.StartLine] = 1,
                [CppPropertyKeys.EndLine] = 1
            }
        };
    }

    private static bool TrySetRecords(IParsedArtifact item, Records records)
    {
        if (item is IndexItem indexItem)
        {
            indexItem.Records = records;
            return true;
        }

        var prop = item.GetType().GetProperty("Records", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (prop?.CanWrite == true)
        {
            prop.SetValue(item, records);
            return true;
        }

        return false;
    }

    private static Annotation[]? MergeAnnotations(Annotation[]? downstream, List<Annotation> additional)
    {
        if ((downstream is null || downstream.Length == 0) && additional.Count == 0)
        {
            return downstream;
        }

        if (downstream is null || downstream.Length == 0)
        {
            return [..additional];
        }

        if (additional.Count == 0)
        {
            return downstream;
        }

        var merged = new Annotation[downstream.Length + additional.Count];
        downstream.CopyTo(merged, 0);
        for (var i = 0; i < additional.Count; i++)
        {
            merged[downstream.Length + i] = additional[i];
        }

        return merged;
    }

    private static string[] NormalizeLines(string text)
        => text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

    private static string NormalizeBlockComment(IReadOnlyList<string> rawLines)
    {
        var lines = new List<string>();
        foreach (var raw in rawLines)
        {
            var line = raw.Trim();
            line = line.Replace("/**", string.Empty, StringComparison.Ordinal);
            line = line.Replace("*/", string.Empty, StringComparison.Ordinal);
            if (line.StartsWith('*'))
            {
                line = line[1..].TrimStart();
            }

            if (!string.IsNullOrWhiteSpace(line))
            {
                lines.Add(line);
            }
        }

        return string.Join('\n', lines).Trim();
    }

    private static int FindNextDeclarationLine(string[] lines, int startIndex)
    {
        for (var i = Math.Max(0, startIndex); i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                continue;
            }

            if (trimmed.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            return i + 1;
        }

        return -1;
    }

    private static string GetPrefixText(string[] lines, int startLine, int lookback)
    {
        var start = Math.Max(1, startLine - Math.Max(1, lookback));
        var end = Math.Min(lines.Length, startLine);
        var selected = lines[(start - 1)..end];
        return string.Join('\n', selected);
    }

    private static int GetLineNumber(string text, int charIndex)
    {
        var line = 1;
        var limit = Math.Min(text.Length, Math.Max(0, charIndex));
        for (var i = 0; i < limit; i++)
        {
            if (text[i] == '\n')
            {
                line++;
            }
        }

        return line;
    }

    private static RepoUri? ResolveLocalInclude(RepoUri sourceUri, string target)
    {
        var includeTarget = target.Trim().Trim('"', '<', '>');
        if (string.IsNullOrWhiteSpace(includeTarget))
        {
            return null;
        }

        includeTarget = includeTarget.Replace('\\', '/');
        try
        {
            var resolved = new Uri(sourceUri.Container, includeTarget);
            return RepoUri.TryParse(resolved.AbsoluteUri, out var repoUri) ? repoUri : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool UriEqualsContainer(RepoUri? nodeUri, RepoUri targetUri)
    {
        if (nodeUri is null)
        {
            return false;
        }

        return string.Equals(
            nodeUri.Container.AbsoluteUri,
            targetUri.Container.AbsoluteUri,
            StringComparison.OrdinalIgnoreCase);
    }

    private static List<string> SplitTopLevel(string text, char separator)
    {
        var values = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return values;
        }

        var depthParen = 0;
        var depthAngle = 0;
        var quote = '\0';
        var escaped = false;
        var builder = new StringBuilder();
        foreach (var ch in text)
        {
            if (quote != '\0')
            {
                builder.Append(ch);
                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                if (ch == '\\')
                {
                    escaped = true;
                    continue;
                }

                if (ch == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            if (ch is '"' or '\'')
            {
                quote = ch;
                builder.Append(ch);
                continue;
            }

            switch (ch)
            {
                case '(':
                    depthParen++;
                    builder.Append(ch);
                    continue;
                case ')':
                    depthParen = Math.Max(0, depthParen - 1);
                    builder.Append(ch);
                    continue;
                case '<':
                    depthAngle++;
                    builder.Append(ch);
                    continue;
                case '>':
                    depthAngle = Math.Max(0, depthAngle - 1);
                    builder.Append(ch);
                    continue;
            }

            if (ch == separator && depthParen == 0 && depthAngle == 0)
            {
                var value = builder.ToString().Trim();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    values.Add(value);
                }

                builder.Clear();
                continue;
            }

            builder.Append(ch);
        }

        var trailing = builder.ToString().Trim();
        if (!string.IsNullOrWhiteSpace(trailing))
        {
            values.Add(trailing);
        }

        return values;
    }

    private static bool IsDeclarationLikeNode(Node node)
    {
        return IsCallableNode(node)
               || string.Equals(node.Kind, CppNodeKinds.Type, StringComparison.Ordinal)
               || string.Equals(node.Kind, CppNodeKinds.Member, StringComparison.Ordinal)
               || string.Equals(node.Kind, CppNodeKinds.Module, StringComparison.Ordinal);
    }

    private static bool IsCallableNode(Node node)
    {
        if (!string.Equals(node.Kind, CppNodeKinds.Function, StringComparison.Ordinal)
            && !string.Equals(node.Kind, CppNodeKinds.Member, StringComparison.Ordinal))
        {
            return false;
        }

        var kind = node.Props[CppPropertyKeys.Kind]?.ToString();
        return string.Equals(kind, "function", StringComparison.Ordinal)
               || string.Equals(kind, "method", StringComparison.Ordinal)
               || string.Equals(kind, "constructor", StringComparison.Ordinal)
               || string.Equals(node.Kind, CppNodeKinds.Function, StringComparison.Ordinal);
    }

    private readonly record struct DocCommentInfo(string Text, JsonArray Tags);

    private readonly record struct TestMacroHit(string Framework, string? Suite, string Name, int Line);

    // ── Source-generated regex declarations ──────────────────────────────

    [GeneratedRegex(@"\[\[(?<body>.*?)\]\]", RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex DoubleBracketAttributeRegex();

    [GeneratedRegex(@"^deprecated\s*\(\s*""(?<reason>[^""]*)""\s*\)$", RegexOptions.CultureInvariant)]
    private static partial Regex DeprecatedWithReasonRegex();

    [GeneratedRegex(@"__attribute__\s*\(\((?<value>[^)]*)\)\)", RegexOptions.CultureInvariant)]
    private static partial Regex GnuAttributeRegex();

    [GeneratedRegex(@"__declspec\s*\((?<value>[^)]*)\)", RegexOptions.CultureInvariant)]
    private static partial Regex DeclspecAttributeRegex();

    [GeneratedRegex(@"^@(?<tag>\w+)\s*(?<rest>.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex DocTagRegex();

    [GeneratedRegex(@"^\s*(?<macro>TEST|TEST_F|TEST_P)\s*\(\s*(?<suite>[A-Za-z_][A-Za-z0-9_]*)\s*,\s*(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\)", RegexOptions.CultureInvariant | RegexOptions.Multiline)]
    private static partial Regex GoogleTestMacroRegex();

    [GeneratedRegex(@"^\s*TEST_CASE\s*\(\s*""(?<name>[^""]+)""", RegexOptions.CultureInvariant | RegexOptions.Multiline)]
    private static partial Regex Catch2TestCaseRegex();

    [GeneratedRegex(@"^\s*SECTION\s*\(\s*""(?<name>[^""]+)""", RegexOptions.CultureInvariant | RegexOptions.Multiline)]
    private static partial Regex Catch2SectionRegex();

}
