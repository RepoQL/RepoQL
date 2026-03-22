using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RepoQL.Contracts;
using RepoQL.Contracts.Models;
using RepoQL.Formats.Cpp.Analysis;
using RepoQL.Formats.Cpp.TreeSitter;
using TsNode = TreeSitter.Node;

namespace RepoQL.Formats.Cpp;

/// <summary>
/// C/C++ format loader and materializer.
///
/// Purpose: Parse C/C++ files and emit graph records directly from Tree-sitter CST.
///
/// Complexity: Single-pass depth-first CST walk with state tracking and X-ray generation.
/// </summary>
public sealed partial class CppMaterializer : IFormatLoader, IFormatMaterializer, IDisposable
{
    internal const string StateMetadataKey = "cpp.state";

    // GeneratedRegex declarations are at the bottom of the class.

    private static readonly HashSet<string> ParameterNameKeywords = new(StringComparer.Ordinal)
    {
        "void",
        "char",
        "short",
        "int",
        "long",
        "float",
        "double",
        "bool",
        "signed",
        "unsigned",
        "const",
        "volatile",
        "auto",
        "typename",
        "class",
        "struct",
        "enum"
    };

    private readonly CppTreeSitterClient _client;
    private readonly CppXRayGenerator _xrayGenerator;
    private readonly MacroInterferenceDetector _macroInterferenceDetector;
    private readonly ILogger<CppMaterializer> _logger;
    private readonly TimeSpan _parseTimeout;
    private bool _disposed;

    public CppMaterializer(
        CppTreeSitterClient? client = null,
        CppXRayGenerator? xrayGenerator = null,
        MacroInterferenceDetector? macroInterferenceDetector = null,
        ILogger<CppMaterializer>? logger = null,
        TimeSpan? parseTimeout = null)
    {
        _client = client ?? new CppTreeSitterClient();
        _xrayGenerator = xrayGenerator ?? new CppXRayGenerator();
        _macroInterferenceDetector = macroInterferenceDetector ?? new MacroInterferenceDetector();
        _logger = logger ?? NullLogger<CppMaterializer>.Instance;
        _parseTimeout = parseTimeout ?? TimeSpan.FromSeconds(5);
    }

    public bool IsGrammarAvailable => _client.IsGrammarAvailable;

    public bool Supports(SemanticMediaType mediaType)
    {
        ArgumentNullException.ThrowIfNull(mediaType);
        return CppMediaTypes.IsSupportedKind(mediaType.Kind);
    }

    public Task<bool> CanLoadAsync(DiscoveredArtifact artifact, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);

        if (CppMediaTypes.TryResolve(artifact.File.Name, out var mediaType))
        {
            artifact.MediaType = mediaType;
            return Task.FromResult(true);
        }

        return Task.FromResult(false);
    }

    public async Task<DocumentModel> LoadAsync(DiscoveredArtifact artifact, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        if (artifact.RepoUri is null)
            throw new InvalidOperationException("RepoUri required to load C/C++ files.");

        var loaded = await FileContentReader.ReadAllTextWithDigestAsync(
            artifact.File,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var mediaType = artifact.MediaType
                        ?? (CppMediaTypes.TryResolve(artifact.File.Name, out var resolved)
                            ? resolved!
                            : throw new InvalidOperationException("Media type could not be resolved for C/C++ file."));

        var metadata = new Dictionary<string, object?>
        {
            [StateMetadataKey] = new CppDocumentState(
                Digest: loaded.Digest,
                Size: loaded.ByteLength,
                MediaType: mediaType,
                StoreUri: artifact.RepoUri.ToString())
        };

        return new DocumentModel(artifact.RepoUri, mediaType, loaded.Text, metadata: metadata);
    }

    public Records Materialize(DocumentModel document)
    {
        if (!Supports(document.MediaType))
        {
            return Records.Empty;
        }

        var state = document.GetMetadataOrDefault<CppDocumentState>(StateMetadataKey);
        if (state is null)
        {
            throw new InvalidOperationException("C/C++ document missing state metadata.");
        }

        var tokenCount = TokenEstimator.EstimateTokensSafe(document.Text);
        var now = DateTimeOffset.UtcNow;
        var artifactId = Guid.NewGuid();
        var artifactSize = state.Size;

        var documentId = Guid.NewGuid();
        var language = CppMediaTypes.IsCppFamilyKind(state.MediaType.Kind) ? CppValues.LanguageCpp : CppValues.LanguageC;
        var documentNode = new Node
        {
            Id = documentId,
            Kind = CppNodeKinds.Document,
            Uri = document.Uri,
            ArtifactId = artifactId,
            Props = new JsonObject
            {
                [CppPropertyKeys.Language] = language,
                [CppPropertyKeys.LineCount] = document.LineMap.LineCount,
                [CppPropertyKeys.ByteSize] = artifactSize
            },
            CreatedAt = now,
            UpdatedAt = now
        };

        var nodes = new List<Node> { documentNode };
        var spans = new List<Span>();
        var edges = new List<Edge>();
        var annotations = new List<Annotation>();
        var symbolLookup = new Dictionary<string, Guid>(StringComparer.Ordinal);

        var structureLines = new List<string>();
        var topLevelTypes = new List<string>();
        var topLevelFunctions = new List<string>();
        string? primaryNamespace = null;

        using var parse = _client.Parse(document.Text);
        if (!parse.GrammarAvailable || !parse.HasTree)
        {
            var ruleId = parse.GrammarAvailable
                ? CppAnnotationRuleIds.ParseFailure
                : CppAnnotationRuleIds.GrammarLoadFailure;
            var message = parse.Diagnostic ?? "C/C++ parser was unavailable.";

            annotations.Add(CreateAnnotation(
                ruleId,
                message,
                documentId,
                targetNodeId: documentNode.Id,
                createdAt: now));

            var fileName = SafeFileName(document.Uri);
            var failedArtifact = new Artifact
            {
                Id = artifactId,
                Digest = state.Digest,
                Size = state.Size,
                MediaType = state.MediaType,
                Text = document.Text,
                StoreUri = state.StoreUri,
                TokenCount = tokenCount,
                Headline = $"{fileName} | {document.MediaType.Kind} | parse failed",
                Summary = message,
                Structure = "parse failed"
            };

            return new Records
            {
                Artifacts = [failedArtifact],
                Nodes = [..nodes],
                Spans = [..spans],
                Edges = [..edges],
                Annotations = [..annotations],
                AnnotationSources = [CppValues.AnnotationSource]
            };
        }

        if (IsNullNode(parse.RootNode))
        {
            const string message = "C/C++ parse returned an empty root node.";
            annotations.Add(CreateAnnotation(
                CppAnnotationRuleIds.ParseFailure,
                message,
                documentId,
                targetNodeId: documentNode.Id,
                createdAt: now));

            var fileName = SafeFileName(document.Uri);
            var failedArtifact = new Artifact
            {
                Id = artifactId,
                Digest = state.Digest,
                Size = state.Size,
                MediaType = state.MediaType,
                Text = document.Text,
                StoreUri = state.StoreUri,
                TokenCount = tokenCount,
                Headline = $"{fileName} | {document.MediaType.Kind} | parse failed",
                Summary = message,
                Structure = "parse failed"
            };

            return new Records
            {
                Artifacts = [failedArtifact],
                Nodes = [..nodes],
                Spans = [..spans],
                Edges = [..edges],
                Annotations = [..annotations],
                AnnotationSources = [CppValues.AnnotationSource]
            };
        }

        var parentStack = new Stack<ParentFrame>();
        parentStack.Push(new ParentFrame(documentNode.Id));

        var namespaceStack = new Stack<string>();
        var typeStack = new Stack<TypeFrame>();
        var templateStack = new Stack<IReadOnlyList<string>>();

        var timedOut = false;
        var depthLimitWarningEmitted = false;
        string? crashMessage = null;
        var deadline = _parseTimeout <= TimeSpan.Zero
            ? DateTimeOffset.UtcNow
            : DateTimeOffset.UtcNow.Add(_parseTimeout);

        try
        {
            VisitNode(parse.RootNode!);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "C/C++ materialization crashed for {Uri}", document.Uri);
            crashMessage = $"C/C++ materialization failed: {ex.Message}";
            annotations.Add(CreateAnnotation(
                CppAnnotationRuleIds.ParseFailure,
                crashMessage,
                documentId,
                targetNodeId: documentNode.Id,
                createdAt: now));

            // Parser crashes must not leak partially materialized structure.
            if (nodes.Count > 1)
            {
                nodes.RemoveRange(1, nodes.Count - 1);
            }
            spans.Clear();
            edges.Clear();
            structureLines.Clear();
            topLevelTypes.Clear();
            topLevelFunctions.Clear();
            primaryNamespace = null;
        }

        if (timedOut && crashMessage is null)
        {
            annotations.Add(CreateAnnotation(
                CppAnnotationRuleIds.ParseTimeout,
                $"C/C++ parse/materialization timed out after {_parseTimeout.TotalSeconds:0.###}s. Partial results were emitted.",
                documentId,
                targetNodeId: documentNode.Id,
                createdAt: now));
        }

        if (crashMessage is null && !IsNullNode(parse.RootNode))
        {
            annotations.AddRange(_macroInterferenceDetector.Detect(parse.RootNode!, document, documentId, now));
            EmitUnsupportedModuleAnnotations(parse.RootNode!);
        }

        var macroWarning = ExtractMacroWarningName(annotations);

        string? headline = null;
        string? summary = null;
        string? structure = null;
        try
        {
            var xray = _xrayGenerator.Generate(new CppXRayModel(
                FileName: SafeFileName(document.Uri),
                MediaKind: document.MediaType.Kind ?? document.MediaType.ToString(),
                LineCount: document.LineMap.LineCount,
                TokenCount: tokenCount,
                PrimaryNamespace: primaryNamespace,
                TopLevelTypes: topLevelTypes,
                TopLevelFunctions: topLevelFunctions,
                StructureLines: structureLines,
                MacroWarning: macroWarning));

            headline = xray.Headline;
            summary = xray.Summary;
            structure = xray.Structure;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to build C/C++ X-ray summaries");
        }

        var artifact = new Artifact
        {
            Id = artifactId,
            Digest = state.Digest,
            Size = state.Size,
            MediaType = state.MediaType,
            Text = document.Text,
            StoreUri = state.StoreUri,
            TokenCount = tokenCount,
            Headline = headline,
            Summary = summary,
            Structure = structure
        };

        var annotationSources = annotations.Count == 0
            ? Array.Empty<string>()
            : annotations
                .Select(a => a.Source)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        return new Records
        {
            Artifacts = [artifact],
            Nodes = [..nodes],
            Spans = [..spans],
            Edges = [..edges],
            Annotations = [..annotations],
            AnnotationSources = annotationSources
        };

        void VisitNode(TsNode node, int depth = 0)
        {
            if (timedOut || IsNullNode(node))
            {
                return;
            }

            if (depth > 256)
            {
                EmitDepthLimitWarning(node, depth, "materializing syntax nodes");
                return;
            }

            if (DateTimeOffset.UtcNow > deadline)
            {
                timedOut = true;
                return;
            }

            switch (node.Type)
            {
                case "template_declaration":
                    HandleTemplateDeclaration(node, depth);
                    return;
                case "preproc_include":
                    HandleInclude(node);
                    return;
                case "preproc_def":
                case "preproc_function_def":
                    HandleMacro(node);
                    return;
                case "preproc_ifdef":
                case "preproc_if":
                    HandleConditionalCompilation(node, depth);
                    return;
                case "namespace_definition":
                    HandleNamespace(node, depth);
                    return;
                case "class_specifier":
                    HandleType(node, "class", depth);
                    return;
                case "struct_specifier":
                    HandleType(node, "struct", depth);
                    return;
                case "union_specifier":
                    HandleType(node, "union", depth);
                    return;
                case "concept_definition":
                    HandleConceptDefinition(node);
                    return;
                case "module_declaration":
                    HandleModuleDeclaration(node);
                    return;
                case "enum_specifier":
                    HandleEnum(node);
                    return;
                case "using_declaration":
                case "alias_declaration":
                    HandleUsingDeclaration(node);
                    return;
                case "type_definition":
                    HandleTypedef(node);
                    return;
                case "friend_declaration":
                    HandleFriendDeclaration(node);
                    return;
                case "declaration":
                    if (HandleGeneralDeclaration(node))
                    {
                        return;
                    }

                    break;
                case "function_definition":
                    HandleFunctionDefinition(node);
                    return;
                case "field_declaration" when typeStack.Count > 0:
                    HandleFieldDeclaration(node);
                    return;
                case "access_specifier" when typeStack.Count > 0:
                    ApplyAccessSpecifier(node);
                    return;
            }

            foreach (var child in node.NamedChildren)
            {
                VisitNode(child, depth + 1);
                if (timedOut)
                {
                    return;
                }
            }
        }

        void HandleInclude(TsNode node)
        {
            var match = IncludeRegex().Match(node.Text);
            if (!match.Success)
            {
                return;
            }

            var target = NormalizeWhitespace(match.Groups["target"].Value);
            if (string.IsNullOrWhiteSpace(target))
            {
                return;
            }

            var style = string.Equals(match.Groups["style"].Value, "<", StringComparison.Ordinal) ? "<>" : "\"\"";
            var props = new JsonObject
            {
                [CppPropertyKeys.Kind] = "include",
                [CppPropertyKeys.Name] = target,
                [CppPropertyKeys.Target] = target,
                [CppPropertyKeys.Style] = style
            };

            AddNode(
                CppNodeKinds.Include,
                props,
                node,
                headline: $"include {target}",
                structure: null);
        }

        void HandleMacro(TsNode node)
        {
            var match = MacroDefinitionRegex().Match(node.Text.Trim());
            if (!match.Success)
            {
                return;
            }

            var name = NormalizeName(match.Groups["name"].Value);
            if (string.IsNullOrWhiteSpace(name))
            {
                return;
            }

            var parameters = NormalizeWhitespace(match.Groups["parameters"].Value);
            var replacement = match.Groups["replacement"].Value.Trim();

            var props = new JsonObject
            {
                [CppPropertyKeys.Kind] = "macro",
                [CppPropertyKeys.Name] = name
            };

            if (!string.IsNullOrWhiteSpace(parameters))
            {
                props[CppPropertyKeys.Parameters] = parameters;
            }

            if (!string.IsNullOrWhiteSpace(replacement))
            {
                props[CppPropertyKeys.Replacement] = replacement;
            }

            AddNode(
                CppNodeKinds.Macro,
                props,
                node,
                headline: $"macro {name}",
                structure: null);
        }

        void HandleConditionalCompilation(TsNode node, int depth)
        {
            var span = CreateSpan(node.StartIndex, node.EndIndex, document, documentId);
            spans.Add(span);

            var predicate = ExtractConditionalPredicate(node.Text);
            AddLintAnnotation(
                CppAnnotationRuleIds.ConditionalCompilation,
                "info",
                "Conditional compilation boundary detected.",
                span,
                dataBuilder: data =>
                {
                    data[CppPropertyKeys.Predicate] = predicate;
                });

            foreach (var child in node.NamedChildren)
            {
                VisitNode(child, depth + 1);
                if (timedOut)
                {
                    return;
                }
            }
        }

        void HandleConceptDefinition(TsNode node)
        {
            var normalized = NormalizeWhitespace(node.Text.Trim().TrimEnd(';'));
            var match = ConceptRegex().Match(normalized);
            var conceptName = match.Success
                ? NormalizeName(match.Groups["name"].Value)
                : ResolveTypeName(node);
            if (string.IsNullOrWhiteSpace(conceptName))
            {
                return;
            }

            var constraint = match.Success
                ? NormalizeWhitespace(match.Groups["constraint"].Value)
                : string.Empty;

            var namespaceName = CurrentNamespace();
            var scope = BuildScopeQualifiedName();
            var qualifiedName = string.IsNullOrWhiteSpace(scope) ? conceptName : $"{scope}::{conceptName}";

            var props = new JsonObject
            {
                [CppPropertyKeys.Name] = conceptName,
                [CppPropertyKeys.QualifiedName] = qualifiedName,
                [CppPropertyKeys.Kind] = "concept",
                [CppPropertyKeys.Accessibility] = CppValues.Public,
                [CppPropertyKeys.IsForwardDeclaration] = "false"
            };

            if (!string.IsNullOrWhiteSpace(namespaceName))
            {
                props[CppPropertyKeys.Namespace] = namespaceName;
            }

            if (!string.IsNullOrWhiteSpace(constraint))
            {
                props[CppPropertyKeys.Constraint] = constraint;
            }

            ApplyTemplateProperties(props, normalized);

            AddNode(
                CppNodeKinds.Type,
                props,
                node,
                headline: $"concept {qualifiedName}",
                structure: null);

            topLevelTypes.Add(conceptName);
            AddStructureLine(IndentLevel(), $"+ concept {conceptName}");
        }

        void HandleModuleDeclaration(TsNode node)
        {
            var normalized = NormalizeWhitespace(node.Text.Trim().TrimEnd(';'));
            var match = ModuleRegex().Match(normalized);
            var moduleName = match.Success ? NormalizeName(match.Groups["name"].Value) : string.Empty;
            if (string.IsNullOrWhiteSpace(moduleName))
            {
                return;
            }

            var partition = match.Success ? NormalizeName(match.Groups["partition"].Value) : string.Empty;
            var isExport = match.Success && !string.IsNullOrWhiteSpace(match.Groups["export"].Value);
            var props = new JsonObject
            {
                [CppPropertyKeys.Name] = moduleName,
                [CppPropertyKeys.Kind] = "module",
                [CppPropertyKeys.IsExport] = isExport ? "true" : "false"
            };

            if (!string.IsNullOrWhiteSpace(partition))
            {
                props[CppPropertyKeys.Partition] = partition;
            }

            AddNode(
                CppNodeKinds.Module,
                props,
                node,
                headline: $"module {moduleName}",
                structure: null);

            AddStructureLine(IndentLevel(), $"+ module {moduleName}{(string.IsNullOrWhiteSpace(partition) ? string.Empty : $":{partition}")}");
        }

        void HandleUsingDeclaration(TsNode node)
        {
            var normalized = NormalizeWhitespace(node.Text.Trim().TrimEnd(';'));
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return;
            }

            if (TryParseUsingAlias(normalized, out var aliasName, out var aliasTarget))
            {
                AddTypeAliasMember(node, aliasName, aliasTarget, "type_alias", normalized);
                return;
            }

            var usingTarget = normalized.StartsWith("using ", StringComparison.Ordinal)
                ? NormalizeWhitespace(normalized["using ".Length..])
                : normalized;
            if (string.IsNullOrWhiteSpace(usingTarget))
            {
                return;
            }

            var scopedName = usingTarget.Split(new[] { "::" }, StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
            if (string.IsNullOrWhiteSpace(scopedName))
            {
                scopedName = usingTarget;
            }

            var scope = BuildScopeQualifiedName();
            var qualifiedName = string.IsNullOrWhiteSpace(scope)
                ? scopedName
                : $"{scope}::{scopedName}";

            var props = new JsonObject
            {
                [CppPropertyKeys.Name] = scopedName,
                [CppPropertyKeys.QualifiedName] = qualifiedName,
                [CppPropertyKeys.Kind] = "using",
                [CppPropertyKeys.Target] = usingTarget
            };

            if (!string.IsNullOrWhiteSpace(CurrentNamespace()))
            {
                props[CppPropertyKeys.Namespace] = CurrentNamespace();
            }

            var usingNode = AddNode(
                CppNodeKinds.Using,
                props,
                node,
                headline: $"using {usingTarget}",
                structure: null);

            var targetId = ResolveReferenceTargetNode(usingTarget);
            if (targetId.HasValue)
            {
                AddReferenceEdge(
                    sourceNodeId: usingNode.Id,
                    destinationNodeId: targetId,
                    relationship: "using",
                    targetName: usingTarget,
                    isResolved: true);
            }
        }

        void HandleTypedef(TsNode node)
        {
            var normalized = NormalizeWhitespace(node.Text.Trim().TrimEnd(';'));
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return;
            }

            if (TryParseTypedef(normalized, out var aliasName, out var aliasTarget))
            {
                AddTypeAliasMember(node, aliasName, aliasTarget, "typedef", normalized);
            }
        }

        void HandleFriendDeclaration(TsNode node)
        {
            if (typeStack.Count == 0)
            {
                return;
            }

            var text = NormalizeWhitespace(node.Text.Trim().TrimEnd(';'));
            var targetName = ExtractFriendTarget(text);
            if (string.IsNullOrWhiteSpace(targetName))
            {
                return;
            }

            var sourceType = typeStack.Peek();
            var destinationId = ResolveReferenceTargetNode(targetName);
            AddReferenceEdge(
                sourceNodeId: sourceType.NodeId,
                destinationNodeId: destinationId,
                relationship: "friend",
                targetName: targetName,
                isResolved: destinationId.HasValue);
        }

        bool HandleGeneralDeclaration(TsNode node)
        {
            var declaration = NormalizeWhitespace(node.Text.Trim().TrimEnd(';'));
            if (string.IsNullOrWhiteSpace(declaration))
            {
                return false;
            }

            if (TryParseUsingAlias(declaration, out var aliasName, out var aliasTarget))
            {
                AddTypeAliasMember(node, aliasName, aliasTarget, "type_alias", declaration);
                return true;
            }

            if (TryParseTypedef(declaration, out aliasName, out aliasTarget))
            {
                AddTypeAliasMember(node, aliasName, aliasTarget, "typedef", declaration);
                return true;
            }

            if (typeStack.Count == 0 && TryParseConstexprVariable(declaration, out var variableName, out var variableType))
            {
                var scope = BuildScopeQualifiedName();
                var qualifiedName = string.IsNullOrWhiteSpace(scope)
                    ? variableName
                    : $"{scope}::{variableName}";

                var props = new JsonObject
                {
                    [CppPropertyKeys.Name] = variableName,
                    [CppPropertyKeys.QualifiedName] = qualifiedName,
                    [CppPropertyKeys.Kind] = "variable",
                    [CppPropertyKeys.IsConstexpr] = "true",
                    [CppPropertyKeys.Signature] = declaration
                };

                if (!string.IsNullOrWhiteSpace(CurrentNamespace()))
                {
                    props[CppPropertyKeys.Namespace] = CurrentNamespace();
                }

                if (!string.IsNullOrWhiteSpace(variableType))
                {
                    props[CppPropertyKeys.ReturnType] = variableType;
                }

                AddNode(
                    CppNodeKinds.Member,
                    props,
                    node,
                    headline: $"constexpr {qualifiedName}",
                    structure: null);

                return true;
            }

            return false;
        }

        void HandleTemplateDeclaration(TsNode node, int depth)
        {
            var templateParams = ExtractTemplateParameters(node.Text);
            templateStack.Push(templateParams);
            try
            {
                foreach (var child in node.NamedChildren.Where(c => c.Type != "template_parameter_list"))
                {
                    VisitNode(child, depth + 1);
                    if (timedOut)
                    {
                        return;
                    }
                }
            }
            finally
            {
                templateStack.Pop();
            }
        }

        void HandleNamespace(TsNode node, int depth)
        {
            var namespaceText = ExtractDeclarationPrefix(node.Text, '{');
            var isInline = InlineNamespaceRegex().IsMatch(namespaceText);
            var segments = ResolveNamespaceSegments(node, namespaceText);
            if (segments.Count == 0)
            {
                segments = ["(anonymous)"];
            }

            var created = new List<string>(segments.Count);
            foreach (var segment in segments)
            {
                var parentNamespace = CurrentNamespace();
                var qualifiedName = string.IsNullOrWhiteSpace(parentNamespace)
                    ? segment
                    : $"{parentNamespace}::{segment}";

                var props = new JsonObject
                {
                    [CppPropertyKeys.Name] = segment,
                    [CppPropertyKeys.QualifiedName] = qualifiedName,
                    [CppPropertyKeys.Kind] = "namespace"
                };
                if (!string.IsNullOrWhiteSpace(parentNamespace))
                {
                    props[CppPropertyKeys.Namespace] = parentNamespace;
                }

                if (string.Equals(segment, "(anonymous)", StringComparison.Ordinal))
                {
                    props[CppPropertyKeys.IsAnonymous] = "true";
                }

                if (isInline)
                {
                    props[CppPropertyKeys.IsInline] = "true";
                }

                var namespaceNode = AddNode(
                    CppNodeKinds.Namespace,
                    props,
                    node,
                    headline: $"namespace {qualifiedName}",
                    structure: null);

                AddStructureLine(
                    IndentLevel(),
                    $"namespace {segment}");

                if (string.IsNullOrWhiteSpace(primaryNamespace)
                    && !string.Equals(segment, "(anonymous)", StringComparison.Ordinal))
                {
                    primaryNamespace = qualifiedName;
                }

                namespaceStack.Push(segment);
                parentStack.Push(new ParentFrame(namespaceNode.Id));
                created.Add(segment);
                isInline = false;
            }

            var body = TryGetField(node, "body") ?? node.NamedChildren.FirstOrDefault(c => c.Type == "declaration_list");
            if (!IsNullNode(body))
            {
                foreach (var child in body!.NamedChildren)
                {
                    VisitNode(child, depth + 1);
                    if (timedOut)
                    {
                        break;
                    }
                }
            }

            for (var i = created.Count - 1; i >= 0; i--)
            {
                parentStack.Pop();
                namespaceStack.Pop();
            }
        }

        void HandleType(TsNode node, string typeKind, int depth)
        {
            var name = ResolveTypeName(node);
            if (string.IsNullOrWhiteSpace(name))
            {
                return;
            }

            var defaultAccess = typeKind == "class" ? CppValues.Private : CppValues.Public;
            var parentNamespace = CurrentNamespace();
            var scope = BuildScopeQualifiedName();
            var qualifiedName = string.IsNullOrWhiteSpace(scope) ? name : $"{scope}::{name}";

            var body = TryGetField(node, "body") ?? node.NamedChildren.FirstOrDefault(c => c.Type == "field_declaration_list");
            var isForwardDeclaration = IsNullNode(body);
            var extends = ExtractBaseTypes(node);

            var props = new JsonObject
            {
                [CppPropertyKeys.Name] = name,
                [CppPropertyKeys.QualifiedName] = qualifiedName,
                [CppPropertyKeys.Kind] = typeKind,
                [CppPropertyKeys.Accessibility] = defaultAccess,
                [CppPropertyKeys.IsAbstract] = "false",
                [CppPropertyKeys.IsForwardDeclaration] = isForwardDeclaration ? "true" : "false"
            };
            if (!string.IsNullOrWhiteSpace(parentNamespace))
            {
                props[CppPropertyKeys.Namespace] = parentNamespace;
            }

            if (!string.IsNullOrWhiteSpace(extends))
            {
                props[CppPropertyKeys.Extends] = extends;
            }

            ApplyTemplateProperties(props, ExtractDeclarationPrefix(node.Text, '{'));

            var typeHeadline = string.IsNullOrWhiteSpace(extends)
                ? $"{typeKind} {qualifiedName}"
                : $"{typeKind} {qualifiedName} : {extends}";
            var typeNode = AddNode(CppNodeKinds.Type, props, node, typeHeadline, null);

            if (typeStack.Count == 0)
            {
                topLevelTypes.Add(name);
            }

            AddStructureLine(
                IndentLevel(),
                $"{AccessPrefix(defaultAccess)} {typeKind} {name}{(string.IsNullOrWhiteSpace(extends) ? string.Empty : $" : {extends}")}");

            if (isForwardDeclaration)
            {
                return;
            }

            var typeFrame = new TypeFrame(name, qualifiedName, typeNode.Id, defaultAccess, props);
            typeStack.Push(typeFrame);
            parentStack.Push(new ParentFrame(typeNode.Id));
            try
            {
                if (!IsNullNode(body))
                {
                    foreach (var child in body!.NamedChildren)
                    {
                        VisitNode(child, depth + 1);
                        if (timedOut)
                        {
                            return;
                        }
                    }
                }
            }
            finally
            {
                parentStack.Pop();
                typeStack.Pop();
            }

            if (typeFrame.HasPureVirtualMethod)
            {
                typeFrame.TypeProps[CppPropertyKeys.IsAbstract] = "true";
            }
        }

        void HandleEnum(TsNode node)
        {
            var name = ResolveTypeName(node);
            if (string.IsNullOrWhiteSpace(name))
            {
                return;
            }

            var parentNamespace = CurrentNamespace();
            var scope = BuildScopeQualifiedName();
            var qualifiedName = string.IsNullOrWhiteSpace(scope) ? name : $"{scope}::{name}";
            var header = ExtractDeclarationPrefix(node.Text, '{');
            var isScoped = ScopedEnumRegex().IsMatch(header);
            var underlyingType = ExtractEnumUnderlyingType(header);

            var props = new JsonObject
            {
                [CppPropertyKeys.Name] = name,
                [CppPropertyKeys.QualifiedName] = qualifiedName,
                [CppPropertyKeys.Kind] = "enum",
                [CppPropertyKeys.Accessibility] = CppValues.Public,
                [CppPropertyKeys.IsAbstract] = "false",
                [CppPropertyKeys.IsForwardDeclaration] = "false",
                [CppPropertyKeys.IsScoped] = isScoped ? "true" : "false"
            };
            if (!string.IsNullOrWhiteSpace(parentNamespace))
            {
                props[CppPropertyKeys.Namespace] = parentNamespace;
            }

            if (!string.IsNullOrWhiteSpace(underlyingType))
            {
                props[CppPropertyKeys.UnderlyingType] = underlyingType;
            }

            ApplyTemplateProperties(props, header);

            var enumNode = AddNode(
                CppNodeKinds.Type,
                props,
                node,
                headline: $"enum {qualifiedName}",
                structure: null);

            if (typeStack.Count == 0)
            {
                topLevelTypes.Add(name);
            }

            AddStructureLine(IndentLevel(), $"{AccessPrefix(CppValues.Public)} enum {name}");

            var enumBody = TryGetField(node, "body") ?? node.NamedChildren.FirstOrDefault(c => c.Type == "enumerator_list");
            if (IsNullNode(enumBody))
            {
                return;
            }

            var typeFrame = new TypeFrame(name, qualifiedName, enumNode.Id, CppValues.Public, props);
            typeStack.Push(typeFrame);
            parentStack.Push(new ParentFrame(enumNode.Id));
            try
            {
                foreach (var enumerator in enumBody!.NamedChildren.Where(c => c.Type == "enumerator"))
                {
                    HandleEnumerator(enumerator);
                }
            }
            finally
            {
                parentStack.Pop();
                typeStack.Pop();
            }
        }

        void HandleEnumerator(TsNode enumeratorNode)
        {
            if (typeStack.Count == 0)
            {
                return;
            }

            var typeFrame = typeStack.Peek();
            var nameNode = TryGetField(enumeratorNode, "name");
            var valueNode = TryGetField(enumeratorNode, "value");

            var name = !IsNullNode(nameNode)
                ? NormalizeName(nameNode!.Text)
                : NormalizeName(ExtractBefore(enumeratorNode.Text, '='));
            if (string.IsNullOrWhiteSpace(name))
            {
                return;
            }

            var value = !IsNullNode(valueNode)
                ? NormalizeWhitespace(valueNode!.Text)
                : ExtractAfter(enumeratorNode.Text, '=');

            var qualifiedName = $"{typeFrame.QualifiedName}::{name}";
            var props = new JsonObject
            {
                [CppPropertyKeys.Name] = name,
                [CppPropertyKeys.QualifiedName] = qualifiedName,
                [CppPropertyKeys.Kind] = "enumerator",
                [CppPropertyKeys.Accessibility] = CppValues.Public,
                [CppPropertyKeys.DeclaringType] = typeFrame.Name
            };

            var parentNamespace = CurrentNamespace();
            if (!string.IsNullOrWhiteSpace(parentNamespace))
            {
                props[CppPropertyKeys.Namespace] = parentNamespace;
            }

            if (!string.IsNullOrWhiteSpace(value))
            {
                props[CppPropertyKeys.Value] = value;
            }

            AddNode(
                CppNodeKinds.Member,
                props,
                enumeratorNode,
                headline: $"enumerator {qualifiedName}",
                structure: null);

            AddStructureLine(IndentLevel(), $"{AccessPrefix(CppValues.Public)} {name}{(string.IsNullOrWhiteSpace(value) ? string.Empty : $" = {value}")}");
        }

        void HandleFieldDeclaration(TsNode node)
        {
            if (typeStack.Count == 0)
            {
                return;
            }

            var declaration = NormalizeWhitespace(node.Text.Trim().TrimEnd(';'));
            if (string.IsNullOrWhiteSpace(declaration))
            {
                return;
            }

            if (TryParseFunctionPointerDeclaration(declaration, out var functionPointer))
            {
                AddFieldMember(functionPointer, node, declaration);
                return;
            }

            if (LooksLikeCallable(declaration) && TryParseCallable(declaration, out var callable))
            {
                AddMemberFromCallable(callable, node, declaration, node.Text);
                return;
            }

            foreach (var field in ParseFieldDeclarators(declaration))
            {
                AddFieldMember(field, node, declaration);
            }
        }

        void HandleFunctionDefinition(TsNode node)
        {
            var signature = ExtractDeclarationPrefix(node.Text, '{').Trim().TrimEnd(';');
            if (string.IsNullOrWhiteSpace(signature) || !TryParseCallable(signature, out var callable))
            {
                return;
            }

            EmitExceptionAnnotations(node);

            if (typeStack.Count > 0)
            {
                AddMemberFromCallable(callable, node, signature, node.Text);
                return;
            }

            AddFreeFunction(callable, node, signature, node.Text);
        }

        void AddFieldMember(FieldDeclarator field, TsNode sourceNode, string declaration)
        {
            if (typeStack.Count == 0)
            {
                return;
            }

            var typeFrame = typeStack.Peek();
            var accessibility = typeFrame.CurrentAccess;
            var namespaceName = CurrentNamespace();
            var qualifiedName = $"{typeFrame.QualifiedName}::{field.Name}";

            var props = new JsonObject
            {
                [CppPropertyKeys.Name] = field.Name,
                [CppPropertyKeys.QualifiedName] = qualifiedName,
                [CppPropertyKeys.Kind] = "field",
                [CppPropertyKeys.Accessibility] = accessibility,
                [CppPropertyKeys.DeclaringType] = typeFrame.Name,
                [CppPropertyKeys.Signature] = declaration
            };

            if (!string.IsNullOrWhiteSpace(namespaceName))
            {
                props[CppPropertyKeys.Namespace] = namespaceName;
            }

            if (!string.IsNullOrWhiteSpace(field.Type))
            {
                props[CppPropertyKeys.ReturnType] = field.Type;
            }

            if (!string.IsNullOrWhiteSpace(field.BitfieldWidth))
            {
                props[CppPropertyKeys.BitfieldWidth] = field.BitfieldWidth;
            }

            if (field.IsFunctionPointer)
            {
                props[CppPropertyKeys.IsFunctionPointer] = "true";
                if (!string.IsNullOrWhiteSpace(field.PointedSignature))
                {
                    props[CppPropertyKeys.PointedSignature] = field.PointedSignature;
                }
            }

            AddNode(
                CppNodeKinds.Member,
                props,
                sourceNode,
                headline: $"field {qualifiedName}",
                structure: null);

            AddStructureLine(IndentLevel(), $"{AccessPrefix(accessibility)} {field.Type} {field.Name}");
        }

        void AddMemberFromCallable(CallableInfo callable, TsNode sourceNode, string declaration, string sourceText)
        {
            if (typeStack.Count == 0)
            {
                return;
            }

            var typeFrame = typeStack.Peek();
            var accessibility = typeFrame.CurrentAccess;
            var isConstructor = string.Equals(callable.Name, typeFrame.Name, StringComparison.Ordinal);
            var kind = isConstructor ? "constructor" : "method";
            var qualifiedName = $"{typeFrame.QualifiedName}::{callable.Name}";
            var namespaceName = CurrentNamespace();

            var props = new JsonObject
            {
                [CppPropertyKeys.Name] = callable.Name,
                [CppPropertyKeys.QualifiedName] = qualifiedName,
                [CppPropertyKeys.Kind] = kind,
                [CppPropertyKeys.Accessibility] = accessibility,
                [CppPropertyKeys.DeclaringType] = typeFrame.Name,
                [CppPropertyKeys.Signature] = callable.Signature,
                [CppPropertyKeys.Parameters] = callable.Parameters
            };

            if (!string.IsNullOrWhiteSpace(namespaceName))
            {
                props[CppPropertyKeys.Namespace] = namespaceName;
            }

            if (!isConstructor && !string.IsNullOrWhiteSpace(callable.ReturnType))
            {
                props[CppPropertyKeys.ReturnType] = callable.ReturnType;
            }

            ApplyTemplateProperties(props, declaration);

            AddFlagProperty(props, CppPropertyKeys.IsVirtual, callable.IsVirtual);
            AddFlagProperty(props, CppPropertyKeys.IsPureVirtual, callable.IsPureVirtual);
            AddFlagProperty(props, CppPropertyKeys.IsOverride, callable.IsOverride);
            AddFlagProperty(props, CppPropertyKeys.IsFinal, callable.IsFinal);
            AddFlagProperty(props, CppPropertyKeys.IsNoexcept, callable.IsNoexcept);
            AddFlagProperty(props, CppPropertyKeys.IsConstexpr, callable.IsConstexpr);
            AddFlagProperty(props, CppPropertyKeys.IsStatic, callable.IsStatic);
            AddFlagProperty(props, CppPropertyKeys.IsConst, callable.IsConst);
            AddFlagProperty(props, CppPropertyKeys.IsVariadic, callable.IsVariadic);
            if (LooksLikeCoroutine(sourceText))
            {
                props[CppPropertyKeys.IsCoroutine] = "true";
            }

            AddNode(
                CppNodeKinds.Member,
                props,
                sourceNode,
                headline: $"{kind} {qualifiedName}",
                structure: null);

            AddStructureLine(IndentLevel(), $"{AccessPrefix(accessibility)} {callable.Signature}");

            if (callable.IsPureVirtual)
            {
                typeFrame.HasPureVirtualMethod = true;
            }
        }

        void AddFreeFunction(CallableInfo callable, TsNode sourceNode, string declaration, string sourceText)
        {
            var namespaceName = CurrentNamespace();
            var qualifiedName = string.IsNullOrWhiteSpace(namespaceName)
                ? callable.Name
                : $"{namespaceName}::{callable.Name}";

            var props = new JsonObject
            {
                [CppPropertyKeys.Name] = callable.Name,
                [CppPropertyKeys.QualifiedName] = qualifiedName,
                [CppPropertyKeys.Kind] = "function",
                [CppPropertyKeys.Signature] = callable.Signature,
                [CppPropertyKeys.Parameters] = callable.Parameters
            };

            if (!string.IsNullOrWhiteSpace(namespaceName))
            {
                props[CppPropertyKeys.Namespace] = namespaceName;
            }

            if (!string.IsNullOrWhiteSpace(callable.ReturnType))
            {
                props[CppPropertyKeys.ReturnType] = callable.ReturnType;
            }

            ApplyTemplateProperties(props, declaration);

            AddFlagProperty(props, CppPropertyKeys.IsNoexcept, callable.IsNoexcept);
            AddFlagProperty(props, CppPropertyKeys.IsConstexpr, callable.IsConstexpr);
            AddFlagProperty(props, CppPropertyKeys.IsStatic, callable.IsStatic);
            AddFlagProperty(props, CppPropertyKeys.IsInline, callable.IsInline);
            AddFlagProperty(props, CppPropertyKeys.IsVariadic, callable.IsVariadic);
            if (LooksLikeCoroutine(sourceText))
            {
                props[CppPropertyKeys.IsCoroutine] = "true";
            }

            AddNode(
                CppNodeKinds.Function,
                props,
                sourceNode,
                headline: $"function {qualifiedName}",
                structure: null);

            topLevelFunctions.Add(callable.Name);
            AddStructureLine(IndentLevel(), $"{AccessPrefix(CppValues.Public)} {callable.Signature}");
        }

        void ApplyAccessSpecifier(TsNode node)
        {
            if (typeStack.Count == 0)
            {
                return;
            }

            var normalized = NormalizeName(node.Text.Trim().TrimEnd(':'));
            var current = typeStack.Peek();
            if (string.Equals(normalized, CppValues.Public, StringComparison.Ordinal)
                || string.Equals(normalized, CppValues.Private, StringComparison.Ordinal)
                || string.Equals(normalized, CppValues.Protected, StringComparison.Ordinal))
            {
                current.CurrentAccess = normalized;
            }
        }

        Node AddNode(
            string kind,
            JsonObject props,
            TsNode sourceNode,
            string? headline,
            string? structure)
        {
            var parent = parentStack.Peek();
            var span = CreateSpan(sourceNode.StartIndex, sourceNode.EndIndex, document, documentId);
            spans.Add(span);

            var node = new Node
            {
                Id = Guid.NewGuid(),
                Kind = kind,
                SpanId = span.Id,
                Uri = BuildNodeUri(document.Uri, props[CppPropertyKeys.QualifiedName]?.ToString(), span.StartLine, span.EndLine),
                ArtifactId = artifactId,
                Props = props,
                Headline = headline,
                Structure = structure,
                CreatedAt = now,
                UpdatedAt = now
            };

            nodes.Add(node);
            RegisterSymbol(node.Id, props);
            edges.Add(new Edge
            {
                Id = Guid.NewGuid(),
                SrcId = parent.NodeId,
                DstId = node.Id,
                Type = CppEdgeTypes.HasPart,
                IsComposition = true,
                Ordinal = parent.NextOrdinal++,
                ScopeDocumentId = documentId,
                CreatedAt = now
            });

            return node;
        }

        void RegisterSymbol(Guid nodeId, JsonObject props)
        {
            var name = props[CppPropertyKeys.Name]?.ToString();
            if (!string.IsNullOrWhiteSpace(name) && !symbolLookup.ContainsKey(name))
            {
                symbolLookup[name] = nodeId;
            }

            var qualifiedName = props[CppPropertyKeys.QualifiedName]?.ToString();
            if (!string.IsNullOrWhiteSpace(qualifiedName) && !symbolLookup.ContainsKey(qualifiedName))
            {
                symbolLookup[qualifiedName] = nodeId;
            }
        }

        Guid? ResolveReferenceTargetNode(string target)
        {
            if (string.IsNullOrWhiteSpace(target))
            {
                return null;
            }

            var cleaned = NormalizeWhitespace(target);
            cleaned = ClassStructTypenameRegex().Replace(cleaned, string.Empty).Trim();
            if (symbolLookup.TryGetValue(cleaned, out var exact))
            {
                return exact;
            }

            var tail = cleaned.Split(new[] { "::" }, StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
            if (!string.IsNullOrWhiteSpace(tail) && symbolLookup.TryGetValue(tail, out var shortName))
            {
                return shortName;
            }

            return null;
        }

        void AddReferenceEdge(Guid sourceNodeId, Guid? destinationNodeId, string relationship, string targetName, bool isResolved)
        {
            edges.Add(new Edge
            {
                Id = Guid.NewGuid(),
                SrcId = sourceNodeId,
                DstId = destinationNodeId,
                Type = CppEdgeTypes.RefersTo,
                IsComposition = false,
                ScopeDocumentId = documentId,
                CreatedAt = now,
                Props = new JsonObject
                {
                    [CppPropertyKeys.Relationship] = relationship,
                    [CppPropertyKeys.Target] = targetName,
                    [CppPropertyKeys.IsResolved] = isResolved ? "true" : "false"
                }
            });
        }

        void AddStructureLine(int indentLevel, string line)
        {
            var indent = new string(' ', Math.Max(0, indentLevel) * 2);
            structureLines.Add($"{indent}{line}");
        }

        int IndentLevel()
            => namespaceStack.Count + typeStack.Count;

        string CurrentNamespace()
        {
            if (namespaceStack.Count == 0)
            {
                return string.Empty;
            }

            return string.Join("::", namespaceStack.Reverse());
        }

        string BuildScopeQualifiedName()
        {
            var parts = new List<string>();
            if (namespaceStack.Count > 0)
            {
                parts.AddRange(namespaceStack.Reverse());
            }

            if (typeStack.Count > 0)
            {
                parts.AddRange(typeStack.Reverse().Select(t => t.Name));
            }

            return string.Join("::", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
        }

        void AddTypeAliasMember(TsNode sourceNode, string aliasName, string aliasTarget, string aliasKind, string declaration)
        {
            var normalizedAlias = NormalizeName(aliasName);
            var normalizedTarget = NormalizeWhitespace(aliasTarget);
            if (string.IsNullOrWhiteSpace(normalizedAlias) || string.IsNullOrWhiteSpace(normalizedTarget))
            {
                return;
            }

            var scope = BuildScopeQualifiedName();
            var qualifiedName = string.IsNullOrWhiteSpace(scope)
                ? normalizedAlias
                : $"{scope}::{normalizedAlias}";

            var props = new JsonObject
            {
                [CppPropertyKeys.Name] = normalizedAlias,
                [CppPropertyKeys.QualifiedName] = qualifiedName,
                [CppPropertyKeys.Kind] = aliasKind,
                [CppPropertyKeys.TargetType] = normalizedTarget,
                [CppPropertyKeys.Signature] = declaration
            };

            if (!string.IsNullOrWhiteSpace(CurrentNamespace()))
            {
                props[CppPropertyKeys.Namespace] = CurrentNamespace();
            }

            if (typeStack.Count > 0)
            {
                var typeFrame = typeStack.Peek();
                props[CppPropertyKeys.Accessibility] = typeFrame.CurrentAccess;
                props[CppPropertyKeys.DeclaringType] = typeFrame.Name;
            }
            else
            {
                props[CppPropertyKeys.Accessibility] = CppValues.Public;
            }

            if (TryParseFunctionPointerDeclaration(normalizedTarget, out var functionPointer))
            {
                props[CppPropertyKeys.IsFunctionPointer] = "true";
                props[CppPropertyKeys.PointedSignature] = functionPointer.PointedSignature ?? normalizedTarget;
            }

            AddNode(
                CppNodeKinds.Member,
                props,
                sourceNode,
                headline: $"{aliasKind} {qualifiedName}",
                structure: null);
        }

        void ApplyTemplateProperties(JsonObject props, string declarationText)
        {
            if (templateStack.Count > 0)
            {
                props[CppPropertyKeys.IsTemplate] = "true";
                var parameters = templateStack.Peek()
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .ToArray();
                if (parameters.Length > 0)
                {
                    props[CppPropertyKeys.TemplateParams] = string.Join(", ", parameters);
                }
            }

            if (TryExtractTemplateSpecialization(declarationText, out var baseTemplate, out var specializationArgs))
            {
                props[CppPropertyKeys.BaseTemplate] = baseTemplate;
                props[CppPropertyKeys.SpecializationArgs] = specializationArgs;
            }
        }

        void EmitExceptionAnnotations(TsNode functionNode)
        {
            var text = functionNode.Text;
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            var span = CreateSpan(functionNode.StartIndex, functionNode.EndIndex, document, documentId);
            spans.Add(span);

            var caughtTypes = CatchTypeRegex().Matches(text)
                .Select(m => NormalizeWhitespace(m.Groups["type"].Value))
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (TryKeywordRegex().IsMatch(text)
                || CatchKeywordRegex().IsMatch(text))
            {
                AddLintAnnotation(
                    CppAnnotationRuleIds.ExceptionHandler,
                    "info",
                    "Exception handling block detected.",
                    span,
                    dataBuilder: data =>
                    {
                        var caughtTypesArray = new JsonArray();
                        foreach (var caught in caughtTypes)
                        {
                            caughtTypesArray.Add(caught);
                        }

                        data[CppPropertyKeys.CaughtTypes] = caughtTypesArray;
                    });
            }

            foreach (Match throwMatch in ThrowExprRegex().Matches(text))
            {
                var thrownType = InferThrownType(throwMatch.Groups["expr"].Value);
                AddLintAnnotation(
                    CppAnnotationRuleIds.ThrowExpression,
                    "info",
                    "Throw expression detected.",
                    span,
                    dataBuilder: data =>
                    {
                        data[CppPropertyKeys.ThrownType] = thrownType;
                    });
            }
        }

        void AddLintAnnotation(
            string ruleId,
            string severity,
            string message,
            Span span,
            Guid? targetNodeId = null,
            Action<JsonObject>? dataBuilder = null)
        {
            var data = new JsonObject
            {
                [CppPropertyKeys.StartLine] = span.StartLine,
                [CppPropertyKeys.EndLine] = span.EndLine
            };
            dataBuilder?.Invoke(data);

            annotations.Add(new Annotation
            {
                Id = Guid.NewGuid(),
                Kind = "lint",
                Severity = severity,
                Source = CppValues.AnalyzerAnnotationSource,
                RuleId = ruleId,
                Message = message,
                ScopeDocumentId = documentId,
                TargetSpanId = span.Id,
                TargetNodeId = targetNodeId,
                Data = data,
                CreatedAt = now
            });
        }

        void EmitUnsupportedModuleAnnotations(TsNode rootNode)
        {
            Visit(rootNode, inModule: false);

            void Visit(TsNode node, bool inModule, int depth = 0)
            {
                if (IsNullNode(node))
                {
                    return;
                }

                if (depth > 256)
                {
                    EmitDepthLimitWarning(node, depth, "scanning module syntax");
                    return;
                }

                var contextWindow = ExtractWindow(document.Text, node.StartIndex, node.EndIndex, 96);
                var hasModuleContext = ModuleKeywordRegex().IsMatch(contextWindow);
                var nextInModule = inModule
                                   || string.Equals(node.Type, "module_declaration", StringComparison.Ordinal)
                                   || hasModuleContext;
                var isErrorNode = node.IsError || string.Equals(node.Type, "ERROR", StringComparison.Ordinal);
                if (isErrorNode && (nextInModule || hasModuleContext))
                {
                    var span = CreateSpan(node.StartIndex, node.EndIndex, document, documentId);
                    spans.Add(span);
                    AddLintAnnotation(
                        CppAnnotationRuleIds.UnsupportedModuleSyntax,
                        "warning",
                        "Module declaration contains unsupported or partially parsed syntax.",
                        span);
                }

                foreach (var child in node.Children)
                {
                    Visit(child, nextInModule, depth + 1);
                }
            }
        }

        void EmitDepthLimitWarning(TsNode node, int depth, string context)
        {
            if (depthLimitWarningEmitted || IsNullNode(node))
            {
                return;
            }

            depthLimitWarningEmitted = true;
            var span = CreateSpan(node.StartIndex, node.EndIndex, document, documentId);
            spans.Add(span);
            AddLintAnnotation(
                CppAnnotationRuleIds.ParseFailure,
                "warning",
                $"C/C++ traversal depth exceeded 256 while {context}. Nested nodes were skipped.",
                span,
                dataBuilder: data =>
                {
                    data[CppPropertyKeys.Depth] = depth;
                });
        }

        string? ExtractMacroWarningName(IReadOnlyCollection<Annotation> values)
        {
            var macroAnnotation = values.FirstOrDefault(a =>
                string.Equals(a.RuleId, CppAnnotationRuleIds.MacroInterference, StringComparison.Ordinal));
            if (macroAnnotation is null)
            {
                return null;
            }

            var macroName = macroAnnotation.Data[CppPropertyKeys.MacroName]?.ToString();
            if (!string.IsNullOrWhiteSpace(macroName))
            {
                return macroName;
            }

            return "macro";
        }

        string ExtractConditionalPredicate(string text)
        {
            var match = PreprocIfRegex().Match(text);
            return match.Success ? NormalizeWhitespace(match.Groups["predicate"].Value) : string.Empty;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _client.Dispose();
        _disposed = true;
    }

    private static Annotation CreateAnnotation(
        string ruleId,
        string message,
        Guid documentId,
        DateTimeOffset createdAt,
        Guid? targetSpanId = null,
        Guid? targetNodeId = null)
    {
        return new Annotation
        {
            Id = Guid.NewGuid(),
            Kind = "lint",
            Severity = "error",
            Source = CppValues.AnnotationSource,
            RuleId = ruleId,
            Message = message,
            ScopeDocumentId = documentId,
            TargetSpanId = targetSpanId,
            TargetNodeId = targetNodeId,
            CreatedAt = createdAt
        };
    }

    private static Span CreateSpan(int startByte, int endByte, DocumentModel document, Guid documentId)
    {
        var safeStart = Math.Clamp(startByte, 0, document.Text.Length);
        var safeEnd = Math.Clamp(endByte, safeStart, document.Text.Length);
        var mapped = document.LineMap.GetSpan(safeStart, safeEnd);

        return new Span
        {
            Id = Guid.NewGuid(),
            DocumentId = documentId,
            StartByte = mapped.StartChar,
            EndByte = mapped.EndChar,
            StartLine = mapped.StartLine,
            StartColumn = mapped.StartColumn,
            EndLine = mapped.EndLine,
            EndColumn = mapped.EndColumn
        };
    }

    private static RepoUri BuildNodeUri(RepoUri documentUri, string? qualifiedName, int? startLine, int? endLine)
    {
        if (!string.IsNullOrWhiteSpace(qualifiedName) && startLine.HasValue && endLine.HasValue)
        {
            return RepoUri.FromSymbol(documentUri.Container, qualifiedName, startLine.Value, endLine.Value);
        }

        if (startLine.HasValue && endLine.HasValue)
        {
            return RepoUri.FromLines(documentUri.Container, startLine.Value, endLine.Value);
        }

        return documentUri;
    }

    private static bool IsNullNode(TsNode? node)
        => node is null || node.Id == IntPtr.Zero;

    private static TsNode? TryGetField(TsNode node, string fieldName)
    {
        try
        {
            return node[fieldName];
        }
        catch (KeyNotFoundException)
        {
            return null;
        }
    }

    private static string ResolveTypeName(TsNode node)
    {
        var nameNode = TryGetField(node, "name");
        if (!IsNullNode(nameNode))
        {
            return NormalizeName(nameNode!.Text);
        }

        var header = ExtractDeclarationPrefix(node.Text, '{');
        var match = TypeNameExtractRegex().Match(header);
        return match.Success ? NormalizeName(match.Groups["name"].Value) : string.Empty;
    }

    private static string ExtractBaseTypes(TsNode node)
    {
        var baseNode = TryGetField(node, "bases") ?? node.NamedChildren.FirstOrDefault(c => c.Type == "base_class_clause");
        if (IsNullNode(baseNode))
        {
            return string.Empty;
        }

        var raw = baseNode!.Text.Trim();
        if (raw.StartsWith(':'))
        {
            raw = raw[1..].Trim();
        }

        var segments = SplitTopLevel(raw, ',');
        var bases = new List<string>();
        foreach (var segment in segments)
        {
            var candidate = NormalizeWhitespace(segment);
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            candidate = AccessModifierRegex().Replace(candidate, string.Empty);
            candidate = NormalizeWhitespace(candidate);
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                bases.Add(candidate);
            }
        }

        return string.Join(", ", bases);
    }

    private static string ExtractEnumUnderlyingType(string header)
    {
        var match = EnumUnderlyingTypeRegex().Match(header);
        return match.Success ? NormalizeWhitespace(match.Groups["type"].Value) : string.Empty;
    }

    private static List<string> ResolveNamespaceSegments(TsNode node, string header)
    {
        var nameNode = TryGetField(node, "name");
        if (!IsNullNode(nameNode))
        {
            var nameText = NormalizeWhitespace(nameNode!.Text);
            if (!string.IsNullOrWhiteSpace(nameText))
            {
                return nameText
                    .Split(new[] { "::" }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(NormalizeName)
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .ToList();
            }
        }

        var match = NamespaceNameRegex().Match(header);
        if (match.Success)
        {
            return match.Groups["name"].Value
                .Split(new[] { "::" }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(NormalizeName)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToList();
        }

        if (AnonymousNamespaceRegex().IsMatch(header))
        {
            return ["(anonymous)"];
        }

        return [];
    }

    private static IReadOnlyList<string> ExtractTemplateParameters(string text)
    {
        var normalized = NormalizeWhitespace(text);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return [];
        }

        var templateIndex = normalized.IndexOf("template", StringComparison.Ordinal);
        if (templateIndex < 0)
        {
            return [];
        }

        var start = normalized.IndexOf('<', templateIndex);
        if (start < 0)
        {
            return [];
        }

        var end = FindMatchingAngleBracket(normalized, start);
        if (end <= start)
        {
            return [];
        }

        var body = normalized[(start + 1)..end];
        var parts = SplitTopLevel(body, ',');
        return parts
            .Select(p => NormalizeWhitespace(p))
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToArray();
    }

    private static bool LooksLikeCallable(string declaration)
    {
        var open = declaration.IndexOf('(');
        var close = declaration.LastIndexOf(')');
        return open > 0 && close > open;
    }

    private static bool TryParseCallable(string signature, out CallableInfo callable)
    {
        callable = default;
        var normalized = NormalizeWhitespace(signature);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        var parsingSignature = StripLeadingAttributes(normalized);
        if (string.IsNullOrWhiteSpace(parsingSignature))
        {
            return false;
        }

        var openIndex = parsingSignature.IndexOf('(');
        if (openIndex <= 0)
        {
            return false;
        }

        var closeIndex = FindMatchingParen(parsingSignature, openIndex);
        if (closeIndex <= openIndex)
        {
            return false;
        }

        var before = parsingSignature[..openIndex].Trim();
        var after = parsingSignature[(closeIndex + 1)..].Trim();
        var nameMatch = CallableNameRegex().Match(before);
        if (!nameMatch.Success)
        {
            return false;
        }

        var fullName = NormalizeWhitespace(nameMatch.Groups["name"].Value);
        var name = fullName.Split(new[] { "::" }, StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var returnTypeRaw = before[..nameMatch.Index].Trim();
        var returnType = NormalizeReturnType(returnTypeRaw);
        var parametersRaw = parsingSignature[(openIndex + 1)..closeIndex];
        var parameters = ParseParameters(parametersRaw);

        var isPureVirtual = PureVirtualRegex().IsMatch(parsingSignature);
        callable = new CallableInfo(
            Name: name,
            ReturnType: string.IsNullOrWhiteSpace(returnType) ? null : returnType,
            Signature: normalized,
            Parameters: parameters,
            IsVirtual: ContainsWord(parsingSignature, "virtual"),
            IsPureVirtual: isPureVirtual,
            IsOverride: ContainsWord(after, "override"),
            IsFinal: ContainsWord(after, "final"),
            IsNoexcept: ContainsWord(parsingSignature, "noexcept"),
            IsConstexpr: ContainsWord(parsingSignature, "constexpr"),
            IsStatic: ContainsWord(parsingSignature, "static"),
            IsConst: ContainsWord(after, "const"),
            IsInline: ContainsWord(parsingSignature, "inline"),
            IsVariadic: IsVariadicParameterList(parametersRaw));
        return true;
    }

    private static JsonArray ParseParameters(string parametersRaw)
    {
        var result = new JsonArray();
        var parts = SplitTopLevel(parametersRaw, ',');
        if (parts.Count == 1 && string.Equals(NormalizeWhitespace(parts[0]), "void", StringComparison.Ordinal))
        {
            return result;
        }

        foreach (var part in parts)
        {
            var cleaned = StripDefaultValue(part).Trim();
            if (string.IsNullOrWhiteSpace(cleaned))
            {
                continue;
            }

            var (name, type) = SplitParameter(cleaned);
            result.Add((JsonNode)new JsonObject
            {
                ["name"] = name,
                ["type"] = type
            });
        }

        return result;
    }

    private static bool IsVariadicParameterList(string parametersRaw)
    {
        var parts = SplitTopLevel(parametersRaw, ',');
        if (parts.Count == 0)
        {
            return false;
        }

        var last = NormalizeWhitespace(parts[^1]);
        return string.Equals(last, "...", StringComparison.Ordinal)
               || last.EndsWith(" ...", StringComparison.Ordinal);
    }

    private static string StripLeadingAttributes(string signature)
    {
        var working = signature.TrimStart();
        while (!string.IsNullOrWhiteSpace(working))
        {
            if (TryStripLeadingDoubleBracketAttribute(working, out var afterBracketAttribute))
            {
                working = afterBracketAttribute.TrimStart();
                continue;
            }

            if (TryStripLeadingParenAttribute(working, "__attribute__", out var afterGnuAttribute))
            {
                working = afterGnuAttribute.TrimStart();
                continue;
            }

            if (TryStripLeadingParenAttribute(working, "__declspec", out var afterDeclspec))
            {
                working = afterDeclspec.TrimStart();
                continue;
            }

            break;
        }

        return working.TrimStart();
    }

    private static bool TryStripLeadingDoubleBracketAttribute(string value, out string remainder)
    {
        remainder = value;
        if (!value.StartsWith("[[", StringComparison.Ordinal))
        {
            return false;
        }

        var end = value.IndexOf("]]", StringComparison.Ordinal);
        if (end < 0)
        {
            return false;
        }

        remainder = end + 2 < value.Length ? value[(end + 2)..] : string.Empty;
        return true;
    }

    private static bool TryStripLeadingParenAttribute(string value, string prefix, out string remainder)
    {
        remainder = value;
        if (!value.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var openIndex = value.IndexOf('(');
        if (openIndex < 0)
        {
            return false;
        }

        var closeIndex = FindMatchingParen(value, openIndex);
        if (closeIndex <= openIndex)
        {
            return false;
        }

        remainder = closeIndex + 1 < value.Length ? value[(closeIndex + 1)..] : string.Empty;
        return true;
    }

    private static (string Name, string Type) SplitParameter(string parameter)
    {
        var trimmed = NormalizeWhitespace(parameter);
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return (string.Empty, string.Empty);
        }

        var nameMatch = EnumeratorNameRegex().Match(trimmed);
        if (!nameMatch.Success)
        {
            return (string.Empty, trimmed);
        }

        var name = nameMatch.Groups["name"].Value;
        if (ParameterNameKeywords.Contains(name))
        {
            return (string.Empty, trimmed);
        }

        var typePart = trimmed[..nameMatch.Index].Trim();
        if (string.IsNullOrWhiteSpace(typePart) || typePart.EndsWith("::", StringComparison.Ordinal))
        {
            return (string.Empty, trimmed);
        }

        return (name, NormalizeWhitespace(typePart));
    }

    private static IReadOnlyList<FieldDeclarator> ParseFieldDeclarators(string declaration)
    {
        var normalized = NormalizeWhitespace(declaration.Trim().TrimEnd(';'));
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return [];
        }

        var segments = SplitTopLevel(normalized, ',');
        if (segments.Count == 0)
        {
            return [];
        }

        var first = segments[0].Trim();
        if (TryParseFunctionPointerDeclaration(first, out var pointerField))
        {
            return [pointerField];
        }

        var firstName = ExtractTrailingIdentifier(first);
        if (string.IsNullOrWhiteSpace(firstName))
        {
            return [];
        }

        var firstType = NormalizeWhitespace(first[..first.LastIndexOf(firstName, StringComparison.Ordinal)].Trim().TrimEnd('*', '&').Trim());
        if (string.IsNullOrWhiteSpace(firstType))
        {
            firstType = first;
        }

        var firstBitfieldWidth = TryExtractBitfieldWidth(first, firstName);
        var fields = new List<FieldDeclarator>
        {
            new(firstName, firstType, firstBitfieldWidth, IsFunctionPointer: false, PointedSignature: null)
        };

        foreach (var segment in segments.Skip(1))
        {
            if (TryParseFunctionPointerDeclaration(segment, out pointerField))
            {
                fields.Add(pointerField);
                continue;
            }

            var name = ExtractTrailingIdentifier(segment);
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            fields.Add(new FieldDeclarator(
                Name: name,
                Type: firstType,
                BitfieldWidth: TryExtractBitfieldWidth(segment, name),
                IsFunctionPointer: false,
                PointedSignature: null));
        }

        return fields;
    }

    private static bool TryParseFunctionPointerDeclaration(string declaration, out FieldDeclarator field)
    {
        field = default;
        var normalized = NormalizeWhitespace(declaration.Trim().TrimEnd(';'));
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        var match = FunctionPointerRegex().Match(normalized);
        if (!match.Success)
        {
            return false;
        }

        var returnType = NormalizeWhitespace(match.Groups["return"].Value);
        var name = NormalizeName(match.Groups["name"].Value);
        var pointedSignature = $"{returnType} ({NormalizeWhitespace(match.Groups["signature"].Value)})";
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        field = new FieldDeclarator(
            Name: name,
            Type: returnType,
            BitfieldWidth: null,
            IsFunctionPointer: true,
            PointedSignature: pointedSignature);
        return true;
    }

    private static string? TryExtractBitfieldWidth(string segment, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(segment) || string.IsNullOrWhiteSpace(fieldName))
        {
            return null;
        }

        foreach (Match match in BitfieldWidthRegex().Matches(segment))
        {
            if (string.Equals(match.Groups["name"].Value, fieldName, StringComparison.Ordinal))
            {
                return match.Groups["width"].Value;
            }
        }

        return null;
    }

    private static bool TryExtractTemplateSpecialization(string declarationText, out string baseTemplate, out string specializationArgs)
    {
        baseTemplate = string.Empty;
        specializationArgs = string.Empty;

        var normalized = NormalizeWhitespace(declarationText);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        var match = TemplateBaseRegex().Match(normalized);
        if (!match.Success)
        {
            return false;
        }

        baseTemplate = NormalizeWhitespace(match.Groups["base"].Value);
        specializationArgs = $"<{NormalizeWhitespace(match.Groups["args"].Value)}>";
        return !string.IsNullOrWhiteSpace(baseTemplate) && !string.IsNullOrWhiteSpace(specializationArgs);
    }

    private static bool TryParseUsingAlias(string declaration, out string aliasName, out string aliasTarget)
    {
        aliasName = string.Empty;
        aliasTarget = string.Empty;

        var match = UsingAliasRegex().Match(declaration.Trim().TrimEnd(';'));
        if (!match.Success)
        {
            return false;
        }

        aliasName = NormalizeName(match.Groups["alias"].Value);
        aliasTarget = NormalizeWhitespace(match.Groups["target"].Value);
        return !string.IsNullOrWhiteSpace(aliasName) && !string.IsNullOrWhiteSpace(aliasTarget);
    }

    private static bool TryParseTypedef(string declaration, out string aliasName, out string aliasTarget)
    {
        aliasName = string.Empty;
        aliasTarget = string.Empty;

        var normalized = declaration.Trim().TrimEnd(';');
        var withoutTypedefKeyword = TypedefPrefixRegex().Replace(normalized, string.Empty);
        if (TryParseFunctionPointerDeclaration(withoutTypedefKeyword, out var functionPointer))
        {
            aliasName = functionPointer.Name;
            aliasTarget = NormalizeWhitespace(withoutTypedefKeyword);
            return true;
        }

        var match = TypedefRegex().Match(normalized);
        if (!match.Success)
        {
            return false;
        }

        aliasName = NormalizeName(match.Groups["alias"].Value);
        aliasTarget = NormalizeWhitespace(match.Groups["target"].Value);
        return !string.IsNullOrWhiteSpace(aliasName) && !string.IsNullOrWhiteSpace(aliasTarget);
    }

    private static bool TryParseConstexprVariable(string declaration, out string variableName, out string variableType)
    {
        variableName = string.Empty;
        variableType = string.Empty;

        if (!ContainsWord(declaration, "constexpr") || declaration.Contains('('))
        {
            return false;
        }

        var normalized = NormalizeWhitespace(StripDefaultValue(declaration));
        var nameMatch = TrailingNameRegex().Match(normalized);
        if (!nameMatch.Success)
        {
            return false;
        }

        variableName = nameMatch.Groups["name"].Value;
        var typePart = normalized[..nameMatch.Index];
        typePart = ConstexprKeywordRegex().Replace(typePart, string.Empty);
        variableType = NormalizeWhitespace(typePart);
        return !string.IsNullOrWhiteSpace(variableName);
    }

    private static string ExtractFriendTarget(string declaration)
    {
        if (string.IsNullOrWhiteSpace(declaration))
        {
            return string.Empty;
        }

        var callableMatch = FriendCallableRegex().Match(declaration);
        if (callableMatch.Success)
        {
            return callableMatch.Groups["name"].Value;
        }

        var match = FriendTargetRegex().Match(declaration);
        return match.Success ? NormalizeWhitespace(match.Groups["target"].Value) : string.Empty;
    }

    private static bool LooksLikeCoroutine(string text)
    {
        return ContainsWord(text, "co_await")
               || ContainsWord(text, "co_yield")
               || ContainsWord(text, "co_return");
    }

    private static string InferThrownType(string expression)
    {
        var normalized = NormalizeWhitespace(expression.Trim());
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return "unknown";
        }

        var ctor = CtorPatternRegex().Match(normalized);
        if (ctor.Success)
        {
            return ctor.Groups["type"].Value;
        }

        var token = LeadingTypeRegex().Match(normalized);
        return token.Success ? token.Groups["type"].Value : "unknown";
    }

    private static string ExtractTrailingIdentifier(string text)
    {
        var withoutInitializer = StripDefaultValue(text);
        var withoutBitfield = BitfieldSuffixRegex().Replace(withoutInitializer, string.Empty);
        var match = FieldNameRegex().Match(withoutBitfield);
        return match.Success ? match.Groups["name"].Value : string.Empty;
    }

    private static string StripDefaultValue(string text)
    {
        var depthParen = 0;
        var depthAngle = 0;
        var depthBracket = 0;
        var depthBrace = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            switch (ch)
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
                    return text[..i].Trim();
            }
        }

        return text.Trim();
    }

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

    private static int FindMatchingAngleBracket(string text, int openIndex)
    {
        var depth = 0;
        for (var i = openIndex; i < text.Length; i++)
        {
            if (text[i] == '<')
            {
                depth++;
                continue;
            }

            if (text[i] == '>')
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
        var parts = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return parts;
        }

        var builder = new StringBuilder();
        var depthParen = 0;
        var depthAngle = 0;
        var depthBracket = 0;
        var depthBrace = 0;
        char quote = '\0';
        var escaped = false;

        foreach (var ch in text)
        {
            builder.Append(ch);

            if (quote != '\0')
            {
                if (quote != '`')
                {
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
                continue;
            }

            switch (ch)
            {
                case '(':
                    depthParen++;
                    continue;
                case ')':
                    depthParen = Math.Max(0, depthParen - 1);
                    continue;
                case '<':
                    depthAngle++;
                    continue;
                case '>':
                    depthAngle = Math.Max(0, depthAngle - 1);
                    continue;
                case '[':
                    depthBracket++;
                    continue;
                case ']':
                    depthBracket = Math.Max(0, depthBracket - 1);
                    continue;
                case '{':
                    depthBrace++;
                    continue;
                case '}':
                    depthBrace = Math.Max(0, depthBrace - 1);
                    continue;
                default:
                    if (ch == separator && depthParen == 0 && depthAngle == 0 && depthBracket == 0 && depthBrace == 0)
                    {
                        var segment = builder.ToString();
                        if (segment.EndsWith(separator))
                        {
                            segment = segment[..^1];
                        }

                        var normalized = NormalizeWhitespace(segment);
                        if (!string.IsNullOrWhiteSpace(normalized))
                        {
                            parts.Add(normalized);
                        }

                        builder.Clear();
                    }

                    break;
            }
        }

        var trailing = NormalizeWhitespace(builder.ToString());
        if (!string.IsNullOrWhiteSpace(trailing))
        {
            parts.Add(trailing);
        }

        return parts;
    }

    private static string NormalizeReturnType(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var normalized = NormalizeWhitespace(raw);
        normalized = StorageSpecifierRegex().Replace(normalized, string.Empty);
        return NormalizeWhitespace(normalized);
    }

    private static string ExtractDeclarationPrefix(string text, char terminal)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var index = text.IndexOf(terminal);
        var prefix = index >= 0 ? text[..index] : text;
        return NormalizeWhitespace(prefix);
    }

    private static string ExtractBefore(string text, char separator)
    {
        var index = text.IndexOf(separator);
        return index >= 0 ? NormalizeWhitespace(text[..index]) : NormalizeWhitespace(text);
    }

    private static string ExtractAfter(string text, char separator)
    {
        var index = text.IndexOf(separator);
        return index >= 0 && index + 1 < text.Length
            ? NormalizeWhitespace(text[(index + 1)..].Trim().TrimEnd(','))
            : string.Empty;
    }

    private static string ExtractWindow(string source, int start, int end, int padding)
    {
        if (string.IsNullOrEmpty(source))
        {
            return string.Empty;
        }

        var from = Math.Max(0, Math.Clamp(start, 0, source.Length) - Math.Max(0, padding));
        var to = Math.Min(source.Length, Math.Clamp(end, 0, source.Length) + Math.Max(0, padding));
        if (to <= from)
        {
            return string.Empty;
        }

        return source[from..to];
    }

    private static bool ContainsWord(string text, string word)
    {
        var textSpan = text.AsSpan();
        var wordSpan = word.AsSpan();
        int index = 0;
        while (index <= textSpan.Length - wordSpan.Length)
        {
            var pos = textSpan.Slice(index).IndexOf(wordSpan, StringComparison.Ordinal);
            if (pos < 0) return false;
            pos += index;

            var leftOk = pos == 0 || !IsWordChar(textSpan[pos - 1]);
            var rightOk = pos + wordSpan.Length >= textSpan.Length || !IsWordChar(textSpan[pos + wordSpan.Length]);

            if (leftOk && rightOk) return true;
            index = pos + 1;
        }
        return false;

        static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';
    }

    private static string NormalizeName(string text)
        => text.Trim().Trim('"', '\'', '`');

    private static string NormalizeWhitespace(string value)
        => string.Join(" ", value.Split(default(string[]), StringSplitOptions.RemoveEmptyEntries));

    private static void AddFlagProperty(JsonObject props, string key, bool enabled)
    {
        if (enabled)
        {
            props[key] = "true";
        }
    }

    private static string AccessPrefix(string accessibility)
    {
        if (string.Equals(accessibility, CppValues.Public, StringComparison.Ordinal))
        {
            return "+";
        }

        if (string.Equals(accessibility, CppValues.Private, StringComparison.Ordinal))
        {
            return "-";
        }

        return "#";
    }

    private static string SafeFileName(RepoUri uri)
    {
        try
        {
            if (uri.IsFile && !string.IsNullOrEmpty(uri.LocalPath))
            {
                return Path.GetFileName(uri.LocalPath);
            }
        }
        catch
        {
            // ignored
        }

        var absolutePath = Uri.UnescapeDataString(uri.AbsolutePath);
        var slash = absolutePath.LastIndexOf('/') >= 0 ? absolutePath[(absolutePath.LastIndexOf('/') + 1)..] : absolutePath;
        return string.IsNullOrEmpty(slash) ? uri.AbsoluteUri : slash;
    }

    private sealed class ParentFrame(Guid nodeId)
    {
        public Guid NodeId { get; } = nodeId;
        public int NextOrdinal { get; set; }
    }

    private sealed class TypeFrame(string name, string qualifiedName, Guid nodeId, string defaultAccess, JsonObject typeProps)
    {
        public string Name { get; } = name;
        public string QualifiedName { get; } = qualifiedName;
        public Guid NodeId { get; } = nodeId;
        public string DefaultAccess { get; } = defaultAccess;
        public string CurrentAccess { get; set; } = defaultAccess;
        public bool HasPureVirtualMethod { get; set; }
        public JsonObject TypeProps { get; } = typeProps;
    }

    private sealed record CppDocumentState(
        string Digest,
        long Size,
        SemanticMediaType MediaType,
        string StoreUri);

    private readonly record struct CallableInfo(
        string Name,
        string? ReturnType,
        string Signature,
        JsonArray Parameters,
        bool IsVirtual,
        bool IsPureVirtual,
        bool IsOverride,
        bool IsFinal,
        bool IsNoexcept,
        bool IsConstexpr,
        bool IsStatic,
        bool IsConst,
        bool IsInline,
        bool IsVariadic);

    private readonly record struct FieldDeclarator(
        string Name,
        string Type,
        string? BitfieldWidth,
        bool IsFunctionPointer,
        string? PointedSignature);

    // ── Source-generated regex declarations ──────────────────────────────

    [GeneratedRegex(@"\bnamespace\s+(?<name>[A-Za-z_][A-Za-z0-9_:]*)", RegexOptions.CultureInvariant)]
    private static partial Regex NamespaceNameRegex();

    [GeneratedRegex(@"\benum(?:\s+class|\s+struct)?\s+[A-Za-z_][A-Za-z0-9_]*\s*:\s*(?<type>[^{]+)", RegexOptions.CultureInvariant)]
    private static partial Regex EnumUnderlyingTypeRegex();

    [GeneratedRegex(@"(?<name>(?:~?[A-Za-z_][A-Za-z0-9_]*|operator\s*[^\s(]+)(?:::(?:~?[A-Za-z_][A-Za-z0-9_]*|operator\s*[^\s(]+))*)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex CallableNameRegex();

    [GeneratedRegex("#\\s*include\\s*(?<style><|\")(?<target>[^\">]+)(?<close>>|\")", RegexOptions.CultureInvariant)]
    private static partial Regex IncludeRegex();

    [GeneratedRegex(@"(?:#\s*define\s+)?(?<name>[A-Za-z_][A-Za-z0-9_]*)(?:\((?<parameters>[^)]*)\))?\s*(?<replacement>.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex MacroDefinitionRegex();

    [GeneratedRegex(@"\busing\s+(?<alias>[A-Za-z_][A-Za-z0-9_]*)\s*=\s*(?<target>.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex UsingAliasRegex();

    [GeneratedRegex(@"\btypedef\s+(?<target>.+?)\s+(?<alias>[A-Za-z_][A-Za-z0-9_]*)$", RegexOptions.CultureInvariant)]
    private static partial Regex TypedefRegex();

    [GeneratedRegex(@"(?<return>.+?)\(\s*\*\s*(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\)\s*\((?<signature>[^)]*)\)", RegexOptions.CultureInvariant)]
    private static partial Regex FunctionPointerRegex();

    [GeneratedRegex(@"(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*:\s*(?<width>\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex BitfieldWidthRegex();

    [GeneratedRegex(@"\b(?<export>export\s+)?module\s+(?<name>[A-Za-z_][A-Za-z0-9_.]*)(?::(?<partition>[A-Za-z_][A-Za-z0-9_.]*))?", RegexOptions.CultureInvariant)]
    private static partial Regex ModuleRegex();

    [GeneratedRegex(@"\bconcept\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*=\s*(?<constraint>.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex ConceptRegex();

    [GeneratedRegex(@"\bfriend\s+(?:class|struct|typename)?\s*(?<target>[A-Za-z_][A-Za-z0-9_:]*)", RegexOptions.CultureInvariant)]
    private static partial Regex FriendTargetRegex();

    [GeneratedRegex(@"\binline\s+namespace\b", RegexOptions.CultureInvariant)]
    private static partial Regex InlineNamespaceRegex();

    [GeneratedRegex(@"\benum\s+(class|struct)\b", RegexOptions.CultureInvariant)]
    private static partial Regex ScopedEnumRegex();

    [GeneratedRegex(@"\b(class|struct|typename)\b", RegexOptions.CultureInvariant)]
    private static partial Regex ClassStructTypenameRegex();

    [GeneratedRegex(@"catch\s*\(\s*(?<type>[^)]+)\)", RegexOptions.CultureInvariant)]
    private static partial Regex CatchTypeRegex();

    [GeneratedRegex(@"\btry\b", RegexOptions.CultureInvariant)]
    private static partial Regex TryKeywordRegex();

    [GeneratedRegex(@"\bcatch\s*\(", RegexOptions.CultureInvariant)]
    private static partial Regex CatchKeywordRegex();

    [GeneratedRegex(@"throw\s+(?<expr>[^;]+);", RegexOptions.CultureInvariant)]
    private static partial Regex ThrowExprRegex();

    [GeneratedRegex(@"\bmodule\b", RegexOptions.CultureInvariant)]
    private static partial Regex ModuleKeywordRegex();

    [GeneratedRegex(@"#\s*if(?:n?def)?\s+(?<predicate>[^\r\n]+)", RegexOptions.CultureInvariant)]
    private static partial Regex PreprocIfRegex();

    [GeneratedRegex(@"\b(?:class|struct|union|enum(?:\s+class|\s+struct)?)\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)", RegexOptions.CultureInvariant)]
    private static partial Regex TypeNameExtractRegex();

    [GeneratedRegex(@"\b(public|private|protected|virtual)\b", RegexOptions.CultureInvariant)]
    private static partial Regex AccessModifierRegex();

    [GeneratedRegex(@"\bnamespace\s*\{", RegexOptions.CultureInvariant)]
    private static partial Regex AnonymousNamespaceRegex();

    [GeneratedRegex(@"=\s*0\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex PureVirtualRegex();

    [GeneratedRegex(@"(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*(?:\[[^\]]*\])?$", RegexOptions.CultureInvariant)]
    private static partial Regex EnumeratorNameRegex();

    [GeneratedRegex(@"(?<base>[A-Za-z_][A-Za-z0-9_:]*)\s*<(?<args>[^>]+)>", RegexOptions.CultureInvariant)]
    private static partial Regex TemplateBaseRegex();

    [GeneratedRegex(@"^\s*typedef\s+", RegexOptions.CultureInvariant)]
    private static partial Regex TypedefPrefixRegex();

    [GeneratedRegex(@"(?<name>[A-Za-z_][A-Za-z0-9_]*)$", RegexOptions.CultureInvariant)]
    private static partial Regex TrailingNameRegex();

    [GeneratedRegex(@"\bconstexpr\b", RegexOptions.CultureInvariant)]
    private static partial Regex ConstexprKeywordRegex();

    [GeneratedRegex(@"friend\s+.*?\b(?<name>[A-Za-z_][A-Za-z0-9_:]*)\s*\(", RegexOptions.CultureInvariant)]
    private static partial Regex FriendCallableRegex();

    [GeneratedRegex(@"^(?<type>[A-Za-z_][A-Za-z0-9_:]*)\s*\(", RegexOptions.CultureInvariant)]
    private static partial Regex CtorPatternRegex();

    [GeneratedRegex(@"^(?<type>[A-Za-z_][A-Za-z0-9_:]*)", RegexOptions.CultureInvariant)]
    private static partial Regex LeadingTypeRegex();

    [GeneratedRegex(@":\s*\d+\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex BitfieldSuffixRegex();

    [GeneratedRegex(@"(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*(?:\[[^\]]*\])?$", RegexOptions.CultureInvariant)]
    private static partial Regex FieldNameRegex();

    [GeneratedRegex(@"\b(?:virtual|static|inline|constexpr|friend|explicit|extern)\b", RegexOptions.CultureInvariant)]
    private static partial Regex StorageSpecifierRegex();
}
