using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RepoQL.Contracts;
using RepoQL.Contracts.Models;

namespace RepoQL.Formats.Cpp.Analysis;

/// <summary>
/// Multi-file analysis for C/C++ records.
///
/// Purpose: Resolve cross-file relationships (declarations/definitions, inheritance, includes, forward declarations).
///
/// Complexity: Graph correlation over flattened record batches with defensive matching and isolated failure behavior.
/// </summary>
public sealed class CppMultiFileAnalyzer(ILogger<CppMultiFileAnalyzer>? logger = null)
{
    private const string DefinesRelationship = "defines";
    private const string ForwardDeclaresRelationship = "forward_declares";
    private const string TransitiveIncludeRelationship = "transitive_include";

    private static readonly string[] HeaderExtensions = [".h", ".hpp", ".hh", ".hxx"];
    private static readonly string[] SourceExtensions = [".cpp", ".cc", ".cxx"];

    private static readonly Regex CallableNameRegex = new(
        @"(?<name>(?:~?[A-Za-z_][A-Za-z0-9_]*|operator\s*[^\s(]+)(?:::(?:~?[A-Za-z_][A-Za-z0-9_]*|operator\s*[^\s(]+))*)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly ILogger<CppMultiFileAnalyzer> _logger = logger ?? NullLogger<CppMultiFileAnalyzer>.Instance;

    public CppMultiFileAnalysisResult Analyze(IReadOnlyCollection<Records> recordsBatch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recordsBatch);
        if (recordsBatch.Count == 0)
        {
            return CppMultiFileAnalysisResult.Empty;
        }

        var allNodes = recordsBatch.SelectMany(r => r.Nodes).ToArray();
        var allEdges = recordsBatch.SelectMany(r => r.Edges).ToArray();
        var spansById = recordsBatch.SelectMany(r => r.Spans).GroupBy(s => s.Id).ToDictionary(g => g.Key, g => g.First());
        var artifactsById = recordsBatch.SelectMany(r => r.Artifacts).GroupBy(a => a.Id).ToDictionary(g => g.Key, g => g.First());

        var documentsById = new Dictionary<Guid, DocumentContext>();
        var documentsByContainerUri = new Dictionary<string, DocumentContext>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in allNodes.Where(n => string.Equals(n.Kind, CppNodeKinds.Document, StringComparison.Ordinal) && n.Uri is not null))
        {
            var text = node.ArtifactId.HasValue && artifactsById.TryGetValue(node.ArtifactId.Value, out var artifact)
                ? artifact.Text ?? string.Empty
                : string.Empty;
            var context = new DocumentContext(
                node,
                node.Uri!,
                Path.GetExtension(node.Uri!.Container.AbsolutePath),
                NormalizeLines(text));
            documentsById[node.Id] = context;
            documentsByContainerUri[node.Uri!.Container.AbsoluteUri] = context;
        }

        var compositionParents = allEdges
            .Where(e => e.IsComposition && e.DstId.HasValue)
            .GroupBy(e => e.DstId!.Value)
            .ToDictionary(g => g.Key, g => g.Select(e => e.SrcId).Distinct().ToArray());

        var nodeDocumentCache = new Dictionary<Guid, Guid?>();
        Guid? ResolveDocumentId(Node node)
        {
            if (nodeDocumentCache.TryGetValue(node.Id, out var cached))
            {
                return cached;
            }

            if (string.Equals(node.Kind, CppNodeKinds.Document, StringComparison.Ordinal))
            {
                nodeDocumentCache[node.Id] = node.Id;
                return node.Id;
            }

            if (node.SpanId.HasValue && spansById.TryGetValue(node.SpanId.Value, out var span))
            {
                nodeDocumentCache[node.Id] = span.DocumentId;
                return span.DocumentId;
            }

            var visited = new HashSet<Guid>();
            var queue = new Queue<Guid>();
            queue.Enqueue(node.Id);
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                if (!visited.Add(current))
                {
                    continue;
                }

                if (!compositionParents.TryGetValue(current, out var parents))
                {
                    continue;
                }

                foreach (var parent in parents)
                {
                    if (documentsById.ContainsKey(parent))
                    {
                        nodeDocumentCache[node.Id] = parent;
                        return parent;
                    }

                    queue.Enqueue(parent);
                }
            }

            nodeDocumentCache[node.Id] = null;
            return null;
        }

        var nodeContexts = new Dictionary<Guid, NodeContext>();
        foreach (var node in allNodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var documentId = ResolveDocumentId(node);
            if (!documentId.HasValue || !documentsById.TryGetValue(documentId.Value, out var document))
            {
                continue;
            }

            nodeContexts[node.Id] = new NodeContext(node, document);
        }

        var addedEdges = new List<Edge>();
        var addedAnnotations = new List<Annotation>();
        var existingKeys = BuildExistingEdgeKeys(allEdges);

        LinkDeclarationsToDefinitions(nodeContexts.Values, addedEdges, existingKeys, cancellationToken);
        CompleteInheritanceGraph(nodeContexts.Values, spansById, addedEdges, existingKeys, cancellationToken);
        ComputeTransitiveIncludes(
            nodeContexts.Values,
            allEdges,
            documentsById,
            documentsByContainerUri,
            addedEdges,
            addedAnnotations,
            existingKeys,
            cancellationToken);
        ResolveForwardDeclarations(nodeContexts.Values, addedEdges, existingKeys, cancellationToken);

        return new CppMultiFileAnalysisResult([.. addedEdges], [.. addedAnnotations]);
    }

    private void LinkDeclarationsToDefinitions(
        IEnumerable<NodeContext> contexts,
        List<Edge> addedEdges,
        HashSet<string> existingKeys,
        CancellationToken cancellationToken)
    {
        var callables = contexts
            .Select(c => TryCreateCallable(c, out var callable) ? callable : (CallableSignature?)null)
            .Where(c => c is not null)
            .Select(c => c!.Value)
            .ToArray();

        var declarations = callables
            .Where(c => HeaderExtensions.Contains(c.Context.Document.Extension, StringComparer.OrdinalIgnoreCase))
            .GroupBy(c => (c.QualifiedName, c.Arity))
            .ToDictionary(g => g.Key, g => g.ToArray());

        var definitions = callables
            .Where(c => SourceExtensions.Contains(c.Context.Document.Extension, StringComparer.OrdinalIgnoreCase))
            .ToArray();

        foreach (var definition in definitions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!declarations.TryGetValue((definition.QualifiedName, definition.Arity), out var matches) || matches.Length == 0)
            {
                continue;
            }

            if (matches.Length > 1)
            {
                _logger.LogWarning(
                    "C++ defines match is ambiguous for {QualifiedName}/{Arity}. Linking all {Count} declarations.",
                    definition.QualifiedName,
                    definition.Arity,
                    matches.Length);
            }

            foreach (var declaration in matches)
            {
                var key = BuildRefersToKey(declaration.Context.Node.Id, definition.Context.Node.Id, DefinesRelationship, depth: null);
                if (!existingKeys.Add(key))
                {
                    continue;
                }

                addedEdges.Add(new Edge
                {
                    Id = Guid.NewGuid(),
                    SrcId = declaration.Context.Node.Id,
                    DstId = definition.Context.Node.Id,
                    Type = CppEdgeTypes.RefersTo,
                    IsComposition = false,
                    ScopeDocumentId = declaration.Context.Document.Node.Id,
                    CreatedAt = DateTimeOffset.UtcNow,
                    Props = new JsonObject
                    {
                        [CppPropertyKeys.Relationship] = DefinesRelationship,
                        [CppPropertyKeys.Target] = definition.QualifiedName,
                        [CppPropertyKeys.IsResolved] = "true"
                    }
                });
            }
        }
    }

    private void CompleteInheritanceGraph(
        IEnumerable<NodeContext> contexts,
        IReadOnlyDictionary<Guid, Span> spansById,
        List<Edge> addedEdges,
        HashSet<string> existingKeys,
        CancellationToken cancellationToken)
    {
        var allTypes = contexts
            .Where(c => string.Equals(c.Node.Kind, CppNodeKinds.Type, StringComparison.Ordinal))
            .ToArray();
        var typeDefinitions = allTypes
            .Where(c => !string.Equals(c.Node.Props[CppPropertyKeys.IsForwardDeclaration]?.ToString(), "true", StringComparison.Ordinal))
            .ToArray();

        var byName = typeDefinitions
            .Where(c => TryGetProp(c.Node, CppPropertyKeys.Name, out _))
            .GroupBy(c => c.Node.Props[CppPropertyKeys.Name]!.ToString(), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        var byQualifiedName = typeDefinitions
            .Where(c => TryGetProp(c.Node, CppPropertyKeys.QualifiedName, out _))
            .GroupBy(c => c.Node.Props[CppPropertyKeys.QualifiedName]!.ToString(), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);

        foreach (var derived in allTypes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryGetProp(derived.Node, CppPropertyKeys.Extends, out var extendsRaw))
            {
                continue;
            }

            var baseSpecs = ExtractBaseSpecs(derived, spansById, extendsRaw);
            foreach (var baseSpec in baseSpecs)
            {
                var resolution = ResolveBase(baseSpec.Name, derived, byName, byQualifiedName);
                if (resolution.Status == BaseResolutionStatus.NotFound)
                {
                    continue;
                }

                if (resolution.Status == BaseResolutionStatus.Ambiguous || resolution.Target is null)
                {
                    _logger.LogWarning(
                        "C++ extends match is ambiguous for base '{Base}' in derived type '{Derived}'. Skipping edge.",
                        baseSpec.Name,
                        GetQualifiedName(derived.Node));
                    continue;
                }

                var key = BuildExtendsKey(derived.Node.Id, resolution.Target.Node.Id, baseSpec.Access, baseSpec.IsVirtual);
                if (!existingKeys.Add(key))
                {
                    continue;
                }

                var props = new JsonObject
                {
                    [CppPropertyKeys.Access] = baseSpec.Access
                };
                if (baseSpec.IsVirtual)
                {
                    props[CppPropertyKeys.IsVirtual] = "true";
                }

                addedEdges.Add(new Edge
                {
                    Id = Guid.NewGuid(),
                    SrcId = derived.Node.Id,
                    DstId = resolution.Target.Node.Id,
                    Type = CppEdgeTypes.Extends,
                    IsComposition = false,
                    ScopeDocumentId = derived.Document.Node.Id,
                    CreatedAt = DateTimeOffset.UtcNow,
                    Props = props
                });
            }
        }
    }

    private void ComputeTransitiveIncludes(
        IEnumerable<NodeContext> contexts,
        IReadOnlyList<Edge> allEdges,
        IReadOnlyDictionary<Guid, DocumentContext> documentsById,
        IReadOnlyDictionary<string, DocumentContext> documentsByContainerUri,
        List<Edge> addedEdges,
        List<Annotation> addedAnnotations,
        HashSet<string> existingKeys,
        CancellationToken cancellationToken)
    {
        var includeNodes = contexts
            .Where(c => string.Equals(c.Node.Kind, CppNodeKinds.Include, StringComparison.Ordinal))
            .ToDictionary(c => c.Node.Id);

        var adjacency = new Dictionary<Guid, HashSet<Guid>>();
        foreach (var edge in allEdges.Where(e => string.Equals(e.Type, CppEdgeTypes.RefersTo, StringComparison.Ordinal)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!includeNodes.TryGetValue(edge.SrcId, out var includeNode))
            {
                continue;
            }

            Guid? destination = null;
            if (edge.DstId.HasValue && documentsById.ContainsKey(edge.DstId.Value))
            {
                destination = edge.DstId.Value;
            }
            else if (edge.DstUri is not null
                     && documentsByContainerUri.TryGetValue(edge.DstUri.Container.AbsoluteUri, out var resolvedDocument))
            {
                destination = resolvedDocument.Node.Id;
            }

            if (!destination.HasValue)
            {
                continue;
            }

            var source = includeNode.Document.Node.Id;
            if (!adjacency.TryGetValue(source, out var targets))
            {
                targets = [];
                adjacency[source] = targets;
            }

            targets.Add(destination.Value);
        }

        var emittedCycles = new HashSet<string>(StringComparer.Ordinal);
        foreach (var sourceDocument in documentsById.Values.Where(d => SourceExtensions.Contains(d.Extension, StringComparer.OrdinalIgnoreCase)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!adjacency.ContainsKey(sourceDocument.Node.Id))
            {
                continue;
            }

            var minDepth = new Dictionary<Guid, int>();
            var path = new List<Guid> { sourceDocument.Node.Id };
            Traverse(sourceDocument.Node.Id, 0);

            void Traverse(Guid current, int depth)
            {
                if (!adjacency.TryGetValue(current, out var targets))
                {
                    return;
                }

                foreach (var target in targets)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var cycleIndex = path.IndexOf(target);
                    if (cycleIndex >= 0)
                    {
                        var cycle = path.Skip(cycleIndex).Append(target).ToArray();
                        var cycleKey = string.Join("->", cycle.Select(c => c.ToString("N")));
                        if (emittedCycles.Add(cycleKey))
                        {
                            addedAnnotations.Add(CreateIncludeCycleAnnotation(sourceDocument.Node.Id, cycle, documentsById));
                        }

                        continue;
                    }

                    var nextDepth = depth + 1;
                    if (nextDepth >= 2)
                    {
                        var key = BuildRefersToKey(sourceDocument.Node.Id, target, TransitiveIncludeRelationship, nextDepth);
                        if (existingKeys.Add(key))
                        {
                            addedEdges.Add(new Edge
                            {
                                Id = Guid.NewGuid(),
                                SrcId = sourceDocument.Node.Id,
                                DstId = target,
                                Type = CppEdgeTypes.RefersTo,
                                IsComposition = false,
                                ScopeDocumentId = sourceDocument.Node.Id,
                                CreatedAt = DateTimeOffset.UtcNow,
                                Props = new JsonObject
                                {
                                    [CppPropertyKeys.Relationship] = TransitiveIncludeRelationship,
                                    [CppPropertyKeys.Depth] = nextDepth
                                }
                            });
                        }
                    }

                    if (minDepth.TryGetValue(target, out var knownDepth) && knownDepth <= nextDepth)
                    {
                        continue;
                    }

                    minDepth[target] = nextDepth;
                    path.Add(target);
                    Traverse(target, nextDepth);
                    path.RemoveAt(path.Count - 1);
                }
            }
        }
    }

    private void ResolveForwardDeclarations(
        IEnumerable<NodeContext> contexts,
        List<Edge> addedEdges,
        HashSet<string> existingKeys,
        CancellationToken cancellationToken)
    {
        var forwardDeclarations = contexts
            .Where(c => string.Equals(c.Node.Kind, CppNodeKinds.Type, StringComparison.Ordinal))
            .Where(c => string.Equals(c.Node.Props[CppPropertyKeys.IsForwardDeclaration]?.ToString(), "true", StringComparison.Ordinal))
            .Where(c => TryGetProp(c.Node, CppPropertyKeys.QualifiedName, out _))
            .ToArray();

        var definitionsByQualifiedName = contexts
            .Where(c => string.Equals(c.Node.Kind, CppNodeKinds.Type, StringComparison.Ordinal))
            .Where(c => !string.Equals(c.Node.Props[CppPropertyKeys.IsForwardDeclaration]?.ToString(), "true", StringComparison.Ordinal))
            .Where(c => TryGetProp(c.Node, CppPropertyKeys.QualifiedName, out _))
            .GroupBy(c => c.Node.Props[CppPropertyKeys.QualifiedName]!.ToString(), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);

        foreach (var forward in forwardDeclarations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var qualifiedName = forward.Node.Props[CppPropertyKeys.QualifiedName]!.ToString();
            if (!definitionsByQualifiedName.TryGetValue(qualifiedName, out var definitions))
            {
                continue;
            }

            foreach (var definition in definitions.Where(d => d.Node.Id != forward.Node.Id))
            {
                var key = BuildRefersToKey(forward.Node.Id, definition.Node.Id, ForwardDeclaresRelationship, depth: null);
                if (!existingKeys.Add(key))
                {
                    continue;
                }

                addedEdges.Add(new Edge
                {
                    Id = Guid.NewGuid(),
                    SrcId = forward.Node.Id,
                    DstId = definition.Node.Id,
                    Type = CppEdgeTypes.RefersTo,
                    IsComposition = false,
                    ScopeDocumentId = forward.Document.Node.Id,
                    CreatedAt = DateTimeOffset.UtcNow,
                    Props = new JsonObject
                    {
                        [CppPropertyKeys.Relationship] = ForwardDeclaresRelationship,
                        [CppPropertyKeys.Target] = qualifiedName,
                        [CppPropertyKeys.IsResolved] = "true"
                    }
                });
            }
        }
    }

    private static HashSet<string> BuildExistingEdgeKeys(IEnumerable<Edge> edges)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var edge in edges)
        {
            if (string.Equals(edge.Type, CppEdgeTypes.RefersTo, StringComparison.Ordinal))
            {
                var relationship = edge.Props[CppPropertyKeys.Relationship]?.ToString();
                if ((string.Equals(relationship, DefinesRelationship, StringComparison.Ordinal)
                     || string.Equals(relationship, ForwardDeclaresRelationship, StringComparison.Ordinal))
                    && edge.DstId.HasValue)
                {
                    keys.Add(BuildRefersToKey(edge.SrcId, edge.DstId.Value, relationship!, null));
                }
                else if (string.Equals(relationship, TransitiveIncludeRelationship, StringComparison.Ordinal) && edge.DstId.HasValue)
                {
                    keys.Add(BuildRefersToKey(edge.SrcId, edge.DstId.Value, relationship!, TryParseInt(edge.Props[CppPropertyKeys.Depth])));
                }
            }
            else if (string.Equals(edge.Type, CppEdgeTypes.Extends, StringComparison.Ordinal) && edge.DstId.HasValue)
            {
                var access = edge.Props[CppPropertyKeys.Access]?.ToString() ?? string.Empty;
                var isVirtual = string.Equals(edge.Props[CppPropertyKeys.IsVirtual]?.ToString(), "true", StringComparison.Ordinal);
                keys.Add(BuildExtendsKey(edge.SrcId, edge.DstId.Value, access, isVirtual));
            }
        }

        return keys;
    }

    private static bool TryCreateCallable(NodeContext context, out CallableSignature signature)
    {
        signature = default;
        if (!IsCallableNode(context.Node))
        {
            return false;
        }

        var arity = ResolveArity(context.Node);
        var qualifiedName = ResolveCallableQualifiedName(context.Node);
        if (string.IsNullOrWhiteSpace(qualifiedName))
        {
            return false;
        }

        signature = new CallableSignature(context, qualifiedName, arity);
        return true;
    }

    private static bool IsCallableNode(Node node)
    {
        if (!string.Equals(node.Kind, CppNodeKinds.Member, StringComparison.Ordinal)
            && !string.Equals(node.Kind, CppNodeKinds.Function, StringComparison.Ordinal))
        {
            return false;
        }

        var kind = node.Props[CppPropertyKeys.Kind]?.ToString();
        return string.Equals(kind, "method", StringComparison.Ordinal)
               || string.Equals(kind, "constructor", StringComparison.Ordinal)
               || string.Equals(kind, "function", StringComparison.Ordinal);
    }

    private static string ResolveCallableQualifiedName(Node node)
    {
        var explicitQualifiedName = node.Props[CppPropertyKeys.QualifiedName]?.ToString();
        var signature = node.Props[CppPropertyKeys.Signature]?.ToString() ?? string.Empty;
        var namespaceName = node.Props[CppPropertyKeys.Namespace]?.ToString() ?? string.Empty;

        if (TryExtractCallableNameAndArity(signature, out var extractedName, out _))
        {
            if (!extractedName.Contains("::", StringComparison.Ordinal))
            {
                if (!string.IsNullOrWhiteSpace(explicitQualifiedName))
                {
                    return explicitQualifiedName;
                }

                return string.IsNullOrWhiteSpace(namespaceName)
                    ? extractedName
                    : $"{namespaceName}::{extractedName}";
            }

            var segmentCount = extractedName.Split("::", StringSplitOptions.RemoveEmptyEntries).Length;
            if (!string.IsNullOrWhiteSpace(namespaceName)
                && segmentCount == 2
                && !extractedName.StartsWith(namespaceName + "::", StringComparison.Ordinal))
            {
                return $"{namespaceName}::{extractedName}";
            }

            return extractedName;
        }

        return explicitQualifiedName ?? string.Empty;
    }

    private static int ResolveArity(Node node)
    {
        if (node.Props[CppPropertyKeys.Parameters] is JsonArray parameters)
        {
            return parameters.Count;
        }

        return TryExtractCallableNameAndArity(node.Props[CppPropertyKeys.Signature]?.ToString() ?? string.Empty, out _, out var arity)
            ? arity
            : 0;
    }

    private static bool TryExtractCallableNameAndArity(string signature, out string callableName, out int arity)
    {
        callableName = string.Empty;
        arity = 0;

        var normalized = NormalizeWhitespace(signature);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        var openIndex = normalized.IndexOf('(');
        if (openIndex <= 0)
        {
            return false;
        }

        var closeIndex = FindMatchingParen(normalized, openIndex);
        if (closeIndex <= openIndex)
        {
            return false;
        }

        var before = normalized[..openIndex].Trim();
        var match = CallableNameRegex.Match(before);
        if (!match.Success)
        {
            return false;
        }

        callableName = NormalizeWhitespace(match.Groups["name"].Value);
        arity = CountParameters(normalized[(openIndex + 1)..closeIndex]);
        return !string.IsNullOrWhiteSpace(callableName);
    }

    private static int CountParameters(string parameterList)
    {
        var parts = SplitTopLevel(parameterList, ',');
        if (parts.Count == 0)
        {
            return 0;
        }

        if (parts.Count == 1 && string.Equals(NormalizeWhitespace(parts[0]), "void", StringComparison.Ordinal))
        {
            return 0;
        }

        return parts.Count(p => !string.IsNullOrWhiteSpace(StripDefaultValue(p)));
    }

    private static IReadOnlyList<BaseSpec> ExtractBaseSpecs(NodeContext derived, IReadOnlyDictionary<Guid, Span> spansById, string extendsRaw)
    {
        var defaultAccess = string.Equals(derived.Node.Props[CppPropertyKeys.Kind]?.ToString(), "class", StringComparison.Ordinal)
            ? CppValues.Private
            : CppValues.Public;

        if (TryParseBaseSpecsFromSource(derived, spansById, defaultAccess, out var parsedSpecs) && parsedSpecs.Count > 0)
        {
            return parsedSpecs;
        }

        return SplitTopLevel(extendsRaw, ',')
            .Select(s => NormalizeWhitespace(s))
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => new BaseSpec(
                NormalizeWhitespace(Regex.Replace(s, @"\b(class|struct|typename)\b", string.Empty, RegexOptions.CultureInvariant)),
                defaultAccess,
                false))
            .Where(s => !string.IsNullOrWhiteSpace(s.Name))
            .ToArray();
    }

    private static bool TryParseBaseSpecsFromSource(
        NodeContext derived,
        IReadOnlyDictionary<Guid, Span> spansById,
        string defaultAccess,
        out List<BaseSpec> specs)
    {
        specs = [];
        if (!derived.Node.SpanId.HasValue
            || !spansById.TryGetValue(derived.Node.SpanId.Value, out var span)
            || !span.StartLine.HasValue)
        {
            return false;
        }

        var lines = derived.Document.Lines;
        if (lines.Length == 0)
        {
            return false;
        }

        var startLine = Math.Max(1, span.StartLine.Value);
        var endLine = span.EndLine.GetValueOrDefault(startLine);
        var maxLine = Math.Min(lines.Length, Math.Max(startLine, Math.Min(endLine, startLine + 32)));
        var snippet = string.Join('\n', lines[(startLine - 1)..maxLine]);
        snippet = Regex.Replace(snippet, @"//.*$", string.Empty, RegexOptions.Multiline | RegexOptions.CultureInvariant);

        var prefix = ExtractDeclarationPrefix(snippet);
        var colon = prefix.IndexOf(':');
        if (colon < 0 || colon + 1 >= prefix.Length)
        {
            return false;
        }

        foreach (var segment in SplitTopLevel(prefix[(colon + 1)..], ','))
        {
            var normalized = NormalizeWhitespace(segment);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                continue;
            }

            var access = ExtractAccess(normalized, defaultAccess);
            var isVirtual = ContainsWord(normalized, "virtual");
            var baseName = NormalizeWhitespace(Regex.Replace(
                normalized,
                @"\b(public|private|protected|virtual|class|struct|typename)\b",
                string.Empty,
                RegexOptions.CultureInvariant));
            if (!string.IsNullOrWhiteSpace(baseName))
            {
                specs.Add(new BaseSpec(baseName, access, isVirtual));
            }
        }

        return specs.Count > 0;
    }

    private static BaseResolution ResolveBase(
        string baseName,
        NodeContext derived,
        IReadOnlyDictionary<string, NodeContext[]> byName,
        IReadOnlyDictionary<string, NodeContext[]> byQualifiedName)
    {
        var shortName = baseName.Split("::", StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        if (string.IsNullOrWhiteSpace(shortName))
        {
            return new BaseResolution(BaseResolutionStatus.NotFound, null);
        }

        var unqualifiedMatches = byName.TryGetValue(shortName, out var byUnqualified)
            ? byUnqualified.Where(m => m.Node.Id != derived.Node.Id).ToArray()
            : [];
        if (unqualifiedMatches.Length == 1)
        {
            return new BaseResolution(BaseResolutionStatus.Found, unqualifiedMatches[0]);
        }

        if (unqualifiedMatches.Length > 1)
        {
            if (baseName.Contains("::", StringComparison.Ordinal))
            {
                var exact = byQualifiedName.TryGetValue(baseName, out var byExact)
                    ? byExact.Where(m => m.Node.Id != derived.Node.Id).ToArray()
                    : [];
                if (exact.Length == 1)
                {
                    return new BaseResolution(BaseResolutionStatus.Found, exact[0]);
                }
            }

            return new BaseResolution(BaseResolutionStatus.Ambiguous, null);
        }

        var fallback = byQualifiedName.TryGetValue(baseName, out var byQualified)
            ? byQualified.Where(m => m.Node.Id != derived.Node.Id).ToArray()
            : [];
        return fallback.Length switch
        {
            1 => new BaseResolution(BaseResolutionStatus.Found, fallback[0]),
            > 1 => new BaseResolution(BaseResolutionStatus.Ambiguous, null),
            _ => new BaseResolution(BaseResolutionStatus.NotFound, null)
        };
    }

    private static Annotation CreateIncludeCycleAnnotation(
        Guid sourceDocumentId,
        IReadOnlyList<Guid> cycle,
        IReadOnlyDictionary<Guid, DocumentContext> documentsById)
    {
        var message = "Include cycle detected: " + string.Join(
            " -> ",
            cycle.Select(id => documentsById.TryGetValue(id, out var document)
                ? Path.GetFileName(document.Uri.Container.LocalPath)
                : id.ToString("N")));

        return new Annotation
        {
            Kind = "lint",
            Severity = "warning",
            Source = CppValues.AnalyzerAnnotationSource,
            RuleId = CppAnnotationRuleIds.IncludeCycle,
            Message = message,
            ScopeDocumentId = sourceDocumentId,
            TargetNodeId = sourceDocumentId,
            CreatedAt = DateTimeOffset.UtcNow,
            Data = new JsonObject
            {
                [CppPropertyKeys.StartLine] = 1,
                [CppPropertyKeys.EndLine] = 1
            }
        };
    }

    private static bool TryGetProp(Node node, string key, out string value)
    {
        value = node.Props[key]?.ToString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(value);
    }

    private static string GetQualifiedName(Node node)
    {
        if (TryGetProp(node, CppPropertyKeys.QualifiedName, out var qualified))
        {
            return qualified;
        }

        return node.Props[CppPropertyKeys.Name]?.ToString() ?? node.Id.ToString("N");
    }

    private static string BuildRefersToKey(Guid srcId, Guid dstId, string relationship, int? depth)
        => depth.HasValue
            ? $"REFERS_TO|{srcId:N}|{dstId:N}|{relationship}|{depth.Value}"
            : $"REFERS_TO|{srcId:N}|{dstId:N}|{relationship}";

    private static string BuildExtendsKey(Guid srcId, Guid dstId, string access, bool isVirtual)
        => $"EXTENDS|{srcId:N}|{dstId:N}|{access}|{(isVirtual ? "true" : "false")}";

    private static int? TryParseInt(JsonNode? node)
        => node is not null && int.TryParse(node.ToString(), out var parsed) ? parsed : null;

    private static string[] NormalizeLines(string text)
        => (text ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

    private static string NormalizeWhitespace(string? value)
        => Regex.Replace(value?.Trim() ?? string.Empty, @"\s+", " ");

    private static string StripDefaultValue(string value)
    {
        var depthParen = 0;
        var depthAngle = 0;
        var depthBracket = 0;
        var depthBrace = 0;
        for (var i = 0; i < value.Length; i++)
        {
            switch (value[i])
            {
                case '(':
                    depthParen++;
                    break;
                case ')':
                    depthParen = Math.Max(0, depthParen - 1);
                    break;
                case '<':
                    depthAngle++;
                    break;
                case '>':
                    depthAngle = Math.Max(0, depthAngle - 1);
                    break;
                case '[':
                    depthBracket++;
                    break;
                case ']':
                    depthBracket = Math.Max(0, depthBracket - 1);
                    break;
                case '{':
                    depthBrace++;
                    break;
                case '}':
                    depthBrace = Math.Max(0, depthBrace - 1);
                    break;
                case '=' when depthParen == 0 && depthAngle == 0 && depthBracket == 0 && depthBrace == 0:
                    return value[..i].Trim();
            }
        }

        return value.Trim();
    }

    private static string ExtractDeclarationPrefix(string declaration)
    {
        var brace = declaration.IndexOf('{');
        var semicolon = declaration.IndexOf(';');
        var cut = -1;
        if (brace >= 0 && semicolon >= 0)
        {
            cut = Math.Min(brace, semicolon);
        }
        else if (brace >= 0)
        {
            cut = brace;
        }
        else if (semicolon >= 0)
        {
            cut = semicolon;
        }

        return cut > 0 ? declaration[..cut] : declaration;
    }

    private static string ExtractAccess(string segment, string defaultAccess)
    {
        if (ContainsWord(segment, CppValues.Public))
        {
            return CppValues.Public;
        }

        if (ContainsWord(segment, CppValues.Private))
        {
            return CppValues.Private;
        }

        if (ContainsWord(segment, CppValues.Protected))
        {
            return CppValues.Protected;
        }

        return defaultAccess;
    }

    private static bool ContainsWord(string value, string word)
        => Regex.IsMatch(value, $@"\b{Regex.Escape(word)}\b", RegexOptions.CultureInvariant);

    private static int FindMatchingParen(string text, int openIndex)
    {
        var depth = 0;
        for (var i = openIndex; i < text.Length; i++)
        {
            if (text[i] == '(')
            {
                depth++;
            }
            else if (text[i] == ')')
            {
                depth--;
                if (depth == 0)
                {
                    return i;
                }
            }
        }

        return -1;
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
        var depthBracket = 0;
        var depthBrace = 0;
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
                case '[':
                    depthBracket++;
                    builder.Append(ch);
                    continue;
                case ']':
                    depthBracket = Math.Max(0, depthBracket - 1);
                    builder.Append(ch);
                    continue;
                case '{':
                    depthBrace++;
                    builder.Append(ch);
                    continue;
                case '}':
                    depthBrace = Math.Max(0, depthBrace - 1);
                    builder.Append(ch);
                    continue;
            }

            if (ch == separator && depthParen == 0 && depthAngle == 0 && depthBracket == 0 && depthBrace == 0)
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

        var finalValue = builder.ToString().Trim();
        if (!string.IsNullOrWhiteSpace(finalValue))
        {
            values.Add(finalValue);
        }

        return values;
    }

    private sealed record DocumentContext(Node Node, RepoUri Uri, string Extension, string[] Lines);
    private sealed record NodeContext(Node Node, DocumentContext Document);
    private readonly record struct CallableSignature(NodeContext Context, string QualifiedName, int Arity);
    private readonly record struct BaseSpec(string Name, string Access, bool IsVirtual);
    private readonly record struct BaseResolution(BaseResolutionStatus Status, NodeContext? Target);

    private enum BaseResolutionStatus
    {
        NotFound,
        Found,
        Ambiguous
    }
}

public readonly record struct CppMultiFileAnalysisResult(Edge[] Edges, Annotation[] Annotations)
{
    public static CppMultiFileAnalysisResult Empty => new([], []);
}
