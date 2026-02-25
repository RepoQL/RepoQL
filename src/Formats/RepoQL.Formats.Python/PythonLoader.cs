using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RepoQL.Contracts;
using RepoQL.Contracts.Models;
using RepoQL.Formats.Python.Surface;
using RepoQL.Formats.Python.TreeSitter;

namespace RepoQL.Formats.Python;

/// <summary>
/// Python format loader and materializer.
///
/// Purpose: Parse Python files into a stable surface model and emit graph records.
///
/// Complexity: Handles classification compatibility, PEP-263 encoding detection,
/// structural materialization, and X-ray summary generation for Python.
/// </summary>
public sealed class PythonLoader : IFormatLoader, IFormatMaterializer, IFormatSchemaProvider, IDisposable
{
    private readonly PythonTreeSitterClient _client;
    private readonly ILogger<PythonLoader> _logger;

    private static readonly Lazy<string> PythonViewsSql = new(
        () => ReadEmbeddedResource("RepoQL.Formats.Python.Schema.python_views.sql"));

    private static readonly Regex Pep263EncodingRegex = new(
        @"^[\t\f ]*#.*?coding[:=][\t\f ]*(?<encoding>[-_.a-zA-Z0-9]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private const string StateMetadataKey = "python.state";
    private const string AnnotationSource = "repoql.formats.python";
    private const string DocumentKind = "document";
    private const string LanguageName = "python";

    static PythonLoader()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public PythonLoader(ILogger<PythonLoader>? logger = null)
    {
        _logger = logger ?? NullLogger<PythonLoader>.Instance;
        _client = new PythonTreeSitterClient();
    }

    public bool Supports(SemanticMediaType mediaType)
    {
        ArgumentNullException.ThrowIfNull(mediaType);

        return PythonMediaTypes.IsSupportedKind(mediaType.Kind)
               || (string.Equals(mediaType.Type, "text", StringComparison.OrdinalIgnoreCase)
                   && string.Equals(mediaType.Subtype, "x-python", StringComparison.OrdinalIgnoreCase));
    }

    public Task<bool> CanLoadAsync(DiscoveredArtifact artifact, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);

        if (PythonMediaTypes.TryResolve(artifact.File.Name, out var mediaType))
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
            throw new InvalidOperationException("RepoUri required to load Python files.");

        var loaded = await FileContentReader.ReadAllTextWithDigestAsync(
            artifact.File,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var text = loaded.Text;
        if (TryDetectPep263Encoding(text, out var encodingName)
            && TryResolveNonUtf8Encoding(encodingName, out var encoding))
        {
            var reloaded = await FileContentReader.ReadAllTextWithDigestAsync(
                artifact.File,
                encoding,
                detectEncodingFromByteOrderMarks: true,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            text = reloaded.Text;
            loaded = reloaded;
        }

        var surface = _client.Parse(text);
        var mediaType = artifact.MediaType ?? ResolveMediaType(artifact.File.Name);

        var metadata = new Dictionary<string, object?>
        {
            [StateMetadataKey] = new PythonDocumentState(
                Surface: surface,
                Digest: loaded.Digest,
                Size: loaded.ByteLength,
                MediaType: mediaType,
                StoreUri: artifact.RepoUri.ToString())
        };

        return new DocumentModel(artifact.RepoUri, mediaType, text, surface, metadata);
    }

    public Records Materialize(DocumentModel document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var state = document.GetMetadataOrDefault<PythonDocumentState>(StateMetadataKey)
            ?? throw new InvalidOperationException("Python document missing state metadata.");

        var semanticsByClass = state.Surface.Classes.ToDictionary(
            c => c.QualifiedName,
            BuildClassSemantics,
            StringComparer.Ordinal);

        var tokenCount = EstimateTokenCount(document.Text);
        var constants = BuildConstantEntries(state.Surface, semanticsByClass);
        string? headline = null;
        string? summary = null;
        string? structure = null;
        try
        {
            headline = BuildHeadline(document, state.Surface, constants, tokenCount);
            summary = BuildSummary(state.Surface, constants.Count);
            structure = BuildStructure(state.Surface, semanticsByClass, constants);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to build Python X-ray summaries");
        }

        var artifact = new Artifact
        {
            Id = Guid.NewGuid(),
            Digest = state.Digest,
            Size = state.Size,
            MediaType = state.MediaType,
            Text = document.Text,
            StoreUri = state.StoreUri,
            Headline = headline,
            Summary = summary,
            Structure = structure,
            TokenCount = tokenCount > 0 ? tokenCount : null
        };

        var documentId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var documentProps = new JsonObject
        {
            [PythonConstants.PropertyKeys.Language] = LanguageName,
            [PythonConstants.PropertyKeys.LineCount] = document.LineMap.LineCount,
            [PythonConstants.PropertyKeys.ByteSize] = artifact.Size,
            [PythonConstants.PropertyKeys.Constants] = BuildConstantsJson(constants),
            [PythonConstants.PropertyKeys.TypeAliases] = BuildTypeAliasesJson(state.Surface.TypeAliases)
        };

        var role = ResolveDocumentRole(document.Uri, state.MediaType);
        if (!string.IsNullOrWhiteSpace(role))
        {
            documentProps[PythonConstants.PropertyKeys.Role] = role;
        }

        if (!string.IsNullOrWhiteSpace(state.Surface.ModuleDocstring))
        {
            documentProps[PythonConstants.PropertyKeys.Docstring] = state.Surface.ModuleDocstring;
        }

        if (state.Surface.AllExports is { Length: > 0 })
        {
            documentProps[PythonConstants.PropertyKeys.AllExports] = new JsonArray(
                state.Surface.AllExports
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Select(static x => (JsonNode?)JsonValue.Create(x))
                    .ToArray());
        }

        var documentNode = new Node
        {
            Id = documentId,
            Kind = DocumentKind,
            Uri = document.Uri,
            ArtifactId = artifact.Id,
            Props = documentProps,
            CreatedAt = now,
            UpdatedAt = now
        };

        var nodes = new List<Node> { documentNode };
        var spans = new List<Span>();
        var edges = new List<Edge>();
        var annotations = new List<Annotation>();

        var rootEntries = new List<RootEntry>();
        rootEntries.AddRange(state.Surface.Classes.Select(c => new RootEntry(c.ByteRange.StartByte, Class: c)));
        rootEntries.AddRange(state.Surface.Functions.Select(f => new RootEntry(f.ByteRange.StartByte, Function: f)));

        var rootOrdinal = 0;
        foreach (var entry in rootEntries.OrderBy(e => e.StartByte))
        {
            if (entry.Class is not null)
            {
                var semantics = semanticsByClass[entry.Class.QualifiedName];
                MaterializeClass(
                    entry.Class,
                    semantics,
                    document,
                    artifact.Id,
                    documentId,
                    now,
                    rootOrdinal++,
                    nodes,
                    spans,
                    edges);
                continue;
            }

            if (entry.Function is not null)
            {
                var functionNode = CreateFunctionNode(entry.Function, document, artifact.Id, documentId, now, out var functionSpan);
                nodes.Add(functionNode);
                spans.Add(functionSpan);
                edges.Add(CreateComposition(documentId, functionNode.Id, rootOrdinal++, documentId, now));
            }
        }

        foreach (var import in state.Surface.Imports.OrderBy(i => i.ByteRange.StartByte))
        {
            edges.Add(CreateImportEdge(documentId, documentId, now, import));
        }

        foreach (var hint in state.Surface.MetaprogrammingHints.OrderBy(h => h.ByteRange.StartByte))
        {
            try
            {
                var hintSpan = CreateSpan(hint.ByteRange, document, documentId);
                spans.Add(hintSpan);
                annotations.Add(CreateAnnotation(
                    kind: PythonConstants.AnnotationKinds.Metaprogramming,
                    ruleId: hint.PatternName,
                    message: GetMetaprogrammingMessage(hint.PatternName),
                    documentId,
                    targetSpanId: hintSpan.Id,
                    data: null,
                    now));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to emit Python metaprogramming annotation for pattern {PatternName}.", hint.PatternName);
            }
        }

        foreach (var hint in state.Surface.FrameworkHints.OrderBy(h => h.ByteRange.StartByte))
        {
            try
            {
                var hintSpan = CreateSpan(hint.ByteRange, document, documentId);
                spans.Add(hintSpan);
                annotations.Add(CreateAnnotation(
                    kind: PythonConstants.AnnotationKinds.Framework,
                    ruleId: hint.RuleId,
                    message: hint.Message,
                    documentId,
                    targetSpanId: hintSpan.Id,
                    data: new JsonObject
                    {
                        ["confidence"] = "medium"
                    },
                    now));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to emit Python framework annotation for rule {RuleId}.", hint.RuleId);
            }
        }

        return new Records
        {
            Artifacts = [artifact],
            Nodes = nodes.ToArray(),
            Spans = spans.ToArray(),
            Edges = edges.ToArray(),
            Annotations = annotations.ToArray(),
            AnnotationSources = annotations.Count > 0 ? [AnnotationSource] : []
        };
    }

    public IEnumerable<FormatSqlScript> GetSchemaScripts()
    {
        yield return new FormatSqlScript("python_views", PythonViewsSql.Value);
    }

    public void Dispose()
    {
        _client.Dispose();
    }

    private static void MaterializeClass(
        PythonClassInfo classInfo,
        ClassSemantics semantics,
        DocumentModel document,
        Guid artifactId,
        Guid documentId,
        DateTimeOffset now,
        int ordinal,
        List<Node> nodes,
        List<Span> spans,
        List<Edge> edges)
    {
        var classNode = CreateTypeNode(classInfo, semantics, document, artifactId, documentId, now, out var classSpan);
        nodes.Add(classNode);
        spans.Add(classSpan);
        edges.Add(CreateComposition(documentId, classNode.Id, ordinal, documentId, now));

        for (var i = 0; i < classInfo.BaseClasses.Count; i++)
        {
            var baseClass = classInfo.BaseClasses[i].Trim();
            if (string.IsNullOrWhiteSpace(baseClass))
                continue;

            edges.Add(CreateExtendsEdge(classNode.Id, baseClass, i, documentId, now));
        }

        var memberOrdinal = 0;
        foreach (var method in classInfo.Methods.OrderBy(m => m.ByteRange.StartByte))
        {
            var memberNode = CreateMemberNode(method, classInfo.QualifiedName, document, artifactId, documentId, now, out var memberSpan);
            nodes.Add(memberNode);
            spans.Add(memberSpan);
            edges.Add(CreateComposition(classNode.Id, memberNode.Id, memberOrdinal++, documentId, now));
        }

        if (semantics.GenerateDataclassInit)
        {
            var generatedNode = CreateGeneratedDataclassInitNode(
                classInfo,
                document,
                artifactId,
                documentId,
                now,
                out var generatedSpan);
            nodes.Add(generatedNode);
            spans.Add(generatedSpan);
            edges.Add(CreateComposition(classNode.Id, generatedNode.Id, memberOrdinal++, documentId, now));
        }
    }

    private static Node CreateTypeNode(
        PythonClassInfo classInfo,
        ClassSemantics semantics,
        DocumentModel document,
        Guid artifactId,
        Guid documentId,
        DateTimeOffset now,
        out Span span)
    {
        span = CreateSpan(classInfo.ByteRange, document, documentId);
        var extends = classInfo.BaseClasses
            .Select(b => b.Trim())
            .Where(b => !string.IsNullOrWhiteSpace(b))
            .ToArray();

        var props = new JsonObject
        {
            [PythonConstants.PropertyKeys.Name] = classInfo.Name,
            [PythonConstants.PropertyKeys.QualifiedName] = classInfo.QualifiedName,
            [PythonConstants.PropertyKeys.TypeKind] = semantics.TypeKind,
            [PythonConstants.PropertyKeys.Decorators] = DecoratorsToJson(classInfo.Decorators),
            [PythonConstants.PropertyKeys.IsAbstract] = semantics.IsAbstract,
            [PythonConstants.PropertyKeys.Variables] = BuildVariablesJson(classInfo)
        };

        if (extends.Length > 0)
            props[PythonConstants.PropertyKeys.Extends] = string.Join(", ", extends);
        if (!string.IsNullOrWhiteSpace(classInfo.Metaclass))
            props[PythonConstants.PropertyKeys.Metaclass] = classInfo.Metaclass;
        var ns = TryGetNamespace(classInfo.QualifiedName);
        if (!string.IsNullOrWhiteSpace(ns))
            props[PythonConstants.PropertyKeys.Namespace] = ns;
        if (!string.IsNullOrWhiteSpace(classInfo.Docstring))
            props[PythonConstants.PropertyKeys.Docstring] = classInfo.Docstring;
        if (!string.IsNullOrWhiteSpace(classInfo.Slots))
            props[PythonConstants.PropertyKeys.Slots] = classInfo.Slots;

        return new Node
        {
            Id = Guid.NewGuid(),
            Kind = PythonConstants.NodeKinds.Type,
            SpanId = span.Id,
            Uri = RepoUri.FromSymbol(document.Uri.Container, classInfo.QualifiedName, span.StartLine, span.EndLine),
            ArtifactId = artifactId,
            Props = props,
            Headline = BuildTypeHeadline(classInfo),
            Structure = BuildTypeStructure(classInfo, semantics),
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static Node CreateMemberNode(
        PythonMethodInfo method,
        string declaringType,
        DocumentModel document,
        Guid artifactId,
        Guid documentId,
        DateTimeOffset now,
        out Span span)
    {
        span = CreateSpan(method.ByteRange, document, documentId);
        var semantic = BuildMethodSemantics(method.Decorators);
        var qualifiedName = BuildMemberQualifiedName(declaringType, method.Name);

        var props = new JsonObject
        {
            [PythonConstants.PropertyKeys.Name] = method.Name,
            [PythonConstants.PropertyKeys.QualifiedName] = qualifiedName,
            [PythonConstants.PropertyKeys.Kind] = semantic.Kind,
            [PythonConstants.PropertyKeys.DeclaringType] = declaringType,
            [PythonConstants.PropertyKeys.Accessibility] = PythonTreeSitterClient.DetermineVisibility(method.Name),
            [PythonConstants.PropertyKeys.IsStatic] = semantic.IsStatic,
            [PythonConstants.PropertyKeys.IsClassmethod] = semantic.IsClassMethod,
            [PythonConstants.PropertyKeys.IsAsync] = method.IsAsync,
            [PythonConstants.PropertyKeys.IsGenerator] = method.IsGenerator,
            [PythonConstants.PropertyKeys.UsesAsyncWith] = method.UsesAsyncWith,
            [PythonConstants.PropertyKeys.UsesAsyncFor] = method.UsesAsyncFor,
            [PythonConstants.PropertyKeys.Parameters] = ParametersToJson(method.Parameters),
            [PythonConstants.PropertyKeys.Decorators] = DecoratorsToJson(method.Decorators),
            [PythonConstants.PropertyKeys.IsOverload] = semantic.IsOverload
        };

        if (semantic.IsAbstract)
            props[PythonConstants.PropertyKeys.IsAbstract] = true;
        if (!string.IsNullOrWhiteSpace(method.ReturnType))
            props[PythonConstants.PropertyKeys.ReturnType] = method.ReturnType;
        if (!string.IsNullOrWhiteSpace(method.Docstring))
            props[PythonConstants.PropertyKeys.Docstring] = method.Docstring;

        return new Node
        {
            Id = Guid.NewGuid(),
            Kind = PythonConstants.NodeKinds.Member,
            SpanId = span.Id,
            Uri = RepoUri.FromSymbol(document.Uri.Container, qualifiedName, span.StartLine, span.EndLine),
            ArtifactId = artifactId,
            Props = props,
            Headline = BuildMethodHeadline(method, semantic),
            Structure = BuildMethodStructure(method, semantic),
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static Node CreateGeneratedDataclassInitNode(
        PythonClassInfo classInfo,
        DocumentModel document,
        Guid artifactId,
        Guid documentId,
        DateTimeOffset now,
        out Span span)
    {
        var generatedRange = new PythonByteRange(classInfo.ByteRange.StartByte, classInfo.ByteRange.StartByte);
        span = CreateSpan(generatedRange, document, documentId);

        var generatedParameters = classInfo.ClassVariables
            .Where(v => !string.Equals(v.Name, "__slots__", StringComparison.Ordinal))
            .Where(v => !string.IsNullOrWhiteSpace(v.TypeAnnotation))
            .OrderBy(v => v.ByteRange.StartByte)
            .Select(v => new PythonParameterInfo(
                Name: v.Name,
                Type: v.TypeAnnotation,
                Default: null,
                Kind: PythonParameterKind.PositionalOrKeyword))
            .ToArray();

        var qualifiedName = BuildMemberQualifiedName(classInfo.QualifiedName, "__init__");

        var props = new JsonObject
        {
            [PythonConstants.PropertyKeys.Name] = "__init__",
            [PythonConstants.PropertyKeys.QualifiedName] = qualifiedName,
            [PythonConstants.PropertyKeys.Kind] = "method",
            [PythonConstants.PropertyKeys.DeclaringType] = classInfo.QualifiedName,
            [PythonConstants.PropertyKeys.Accessibility] = "public",
            [PythonConstants.PropertyKeys.IsStatic] = false,
            [PythonConstants.PropertyKeys.IsClassmethod] = false,
            [PythonConstants.PropertyKeys.IsAsync] = false,
            [PythonConstants.PropertyKeys.IsGenerator] = false,
            [PythonConstants.PropertyKeys.UsesAsyncWith] = false,
            [PythonConstants.PropertyKeys.UsesAsyncFor] = false,
            [PythonConstants.PropertyKeys.IsGenerated] = true,
            [PythonConstants.PropertyKeys.Generator] = "dataclass",
            [PythonConstants.PropertyKeys.Parameters] = ParametersToJson(generatedParameters),
            [PythonConstants.PropertyKeys.Decorators] = new JsonArray()
        };

        var parameterText = FormatParameters(generatedParameters);
        return new Node
        {
            Id = Guid.NewGuid(),
            Kind = PythonConstants.NodeKinds.Member,
            SpanId = span.Id,
            Uri = RepoUri.FromSymbol(document.Uri.Container, qualifiedName, span.StartLine, span.EndLine),
            ArtifactId = artifactId,
            Props = props,
            Headline = $"method __init__({parameterText}) [generated]",
            Structure = $"+__init__({parameterText}) [generated dataclass]",
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static Node CreateFunctionNode(
        PythonFunctionInfo function,
        DocumentModel document,
        Guid artifactId,
        Guid documentId,
        DateTimeOffset now,
        out Span span)
    {
        span = CreateSpan(function.ByteRange, document, documentId);
        var accessibility = PythonTreeSitterClient.DetermineVisibility(function.Name);

        var props = new JsonObject
        {
            [PythonConstants.PropertyKeys.Name] = function.Name,
            [PythonConstants.PropertyKeys.QualifiedName] = function.Name,
            [PythonConstants.PropertyKeys.Kind] = "function",
            [PythonConstants.PropertyKeys.Accessibility] = accessibility,
            [PythonConstants.PropertyKeys.IsAsync] = function.IsAsync,
            [PythonConstants.PropertyKeys.IsGenerator] = function.IsGenerator,
            [PythonConstants.PropertyKeys.UsesAsyncWith] = function.UsesAsyncWith,
            [PythonConstants.PropertyKeys.UsesAsyncFor] = function.UsesAsyncFor,
            [PythonConstants.PropertyKeys.Parameters] = ParametersToJson(function.Parameters),
            [PythonConstants.PropertyKeys.Decorators] = DecoratorsToJson(function.Decorators)
        };

        if (!string.IsNullOrWhiteSpace(function.ReturnType))
            props[PythonConstants.PropertyKeys.ReturnType] = function.ReturnType;
        if (!string.IsNullOrWhiteSpace(function.Docstring))
            props[PythonConstants.PropertyKeys.Docstring] = function.Docstring;

        return new Node
        {
            Id = Guid.NewGuid(),
            Kind = PythonConstants.NodeKinds.Function,
            SpanId = span.Id,
            Uri = RepoUri.FromSymbol(document.Uri.Container, function.Name, span.StartLine, span.EndLine),
            ArtifactId = artifactId,
            Props = props,
            Headline = BuildFunctionHeadline(function),
            Structure = BuildFunctionStructure(function),
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static Edge CreateComposition(Guid srcId, Guid dstId, int ordinal, Guid scopeDocId, DateTimeOffset now)
    {
        return new Edge
        {
            Id = Guid.NewGuid(),
            SrcId = srcId,
            DstId = dstId,
            Type = PythonConstants.EdgeTypes.HasPart,
            IsComposition = true,
            Ordinal = ordinal,
            ScopeDocumentId = scopeDocId,
            CreatedAt = now
        };
    }

    private static Edge CreateExtendsEdge(Guid srcId, string target, int ordinal, Guid scopeDocId, DateTimeOffset now)
    {
        return new Edge
        {
            Id = Guid.NewGuid(),
            SrcId = srcId,
            DstId = null,
            Type = PythonConstants.EdgeTypes.Extends,
            IsComposition = false,
            Ordinal = ordinal,
            ScopeDocumentId = scopeDocId,
            Props = new JsonObject
            {
                ["target"] = target,
                ["ordinal"] = ordinal
            },
            CreatedAt = now
        };
    }

    private static Edge CreateImportEdge(Guid documentId, Guid scopeDocId, DateTimeOffset now, PythonImportInfo import)
    {
        var specifier = BuildImportSpecifier(import);
        var names = BuildImportNames(import);

        var props = new JsonObject
        {
            ["specifier"] = specifier,
            ["is_relative"] = import.IsRelative,
            ["relative_level"] = import.RelativeLevel,
            ["is_type_checking_only"] = import.IsTypeCheckingOnly
        };
        if (!string.IsNullOrWhiteSpace(names))
            props["names"] = names;

        return new Edge
        {
            Id = Guid.NewGuid(),
            SrcId = documentId,
            DstId = null,
            Type = PythonConstants.EdgeTypes.Imports,
            IsComposition = false,
            ScopeDocumentId = scopeDocId,
            Props = props,
            CreatedAt = now
        };
    }

    private static Span CreateSpan(PythonByteRange range, DocumentModel document, Guid documentId)
    {
        var start = Math.Clamp(range.StartByte, 0, document.Text.Length);
        var end = Math.Clamp(range.EndByte, start, document.Text.Length);
        var mapped = document.LineMap.GetSpan(start, end);
        return new Span
        {
            Id = Guid.NewGuid(),
            StartByte = mapped.StartChar,
            EndByte = mapped.EndChar,
            StartLine = mapped.StartLine,
            StartColumn = mapped.StartColumn,
            EndLine = mapped.EndLine,
            EndColumn = mapped.EndColumn,
            DocumentId = documentId
        };
    }

    private static string GetMetaprogrammingMessage(string patternName) => patternName switch
    {
        "__getattr__" => "dynamic attribute access, graph may be incomplete",
        "__getattr___module" => "dynamic module attribute access (PEP 562), graph may be incomplete",
        "__dir___module" => "module customizes dir(), discoverable API surface may differ from static graph",
        "exec" => "dynamic code execution detected",
        "eval" => "dynamic code execution detected",
        "type_dynamic_class" => "dynamic class creation",
        "setattr" => "dynamic attribute creation",
        "__import__" => "dynamic import detected",
        "importlib.import_module" => "dynamic import detected",
        "__new__" => "metaclass may generate members",
        "__init_subclass__" => "metaclass may generate members",
        _ => $"metaprogramming pattern detected: {patternName}"
    };

    private static Annotation CreateAnnotation(
        string kind,
        string ruleId,
        string message,
        Guid documentId,
        Guid targetSpanId,
        JsonObject? data,
        DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        Kind = kind,
        Severity = "info",
        Source = AnnotationSource,
        RuleId = ruleId,
        Message = message,
        ScopeDocumentId = documentId,
        TargetSpanId = targetSpanId,
        Data = data ?? new JsonObject(),
        CreatedAt = now
    };

    private static string BuildHeadline(
        DocumentModel document,
        PythonDocumentSurface surface,
        IReadOnlyList<ConstantEntry> constants,
        int tokenCount)
    {
        var fileName = GetFileName(document.Uri);
        var isStub = string.Equals(Path.GetExtension(fileName), ".pyi", StringComparison.OrdinalIgnoreCase);
        var primaryDeclaration = BuildPrimaryDeclaration(fileName, surface, constants, isStub);
        var keyMembers = BuildKeyMembers(surface, constants);
        var sizePart = $"{document.LineMap.LineCount} ln, ~{tokenCount} tok";

        return string.Join(" | ", new[] { fileName, primaryDeclaration, keyMembers, sizePart }
            .Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    private static string BuildSummary(PythonDocumentSurface surface, int constantCount)
    {
        var parts = new List<string>
        {
            $"{surface.Classes.Count} classes",
            $"{surface.Functions.Count} functions",
            $"{surface.Imports.Count} imports"
        };

        if (constantCount > 0)
            parts.Add($"{constantCount} constants");
        if (surface.TypeAliases.Count > 0)
            parts.Add($"{surface.TypeAliases.Count} type aliases");

        return string.Join(", ", parts);
    }

    private static string BuildStructure(
        PythonDocumentSurface surface,
        IReadOnlyDictionary<string, ClassSemantics> semanticsByClass,
        IReadOnlyList<ConstantEntry> constants)
    {
        var lines = new List<string>();

        var moduleDoc = FirstDocstringLine(surface.ModuleDocstring);
        if (!string.IsNullOrWhiteSpace(moduleDoc))
            lines.Add($"# {moduleDoc}");

        var entries = new List<StructureEntry>();
        entries.AddRange(surface.Classes.Select(c => new StructureEntry(c.ByteRange.StartByte, Class: c)));
        entries.AddRange(surface.Functions.Select(f => new StructureEntry(f.ByteRange.StartByte, Function: f)));
        entries.AddRange(surface.TypeAliases.Select(a => new StructureEntry(a.ByteRange.StartByte, TypeAlias: a)));
        entries.AddRange(constants.Select(c => new StructureEntry(c.StartByte, Constant: c)));

        foreach (var entry in entries.OrderBy(e => e.StartByte))
        {
            if (entry.Class is not null)
            {
                var classInfo = entry.Class;
                var semantics = semanticsByClass[classInfo.QualifiedName];
                var classDoc = FirstDocstringLine(classInfo.Docstring);
                if (!string.IsNullOrWhiteSpace(classDoc))
                    lines.Add($"# {classDoc}");

                lines.Add($"{BuildTypeHeadline(classInfo)}    #symbol={classInfo.QualifiedName}");

                foreach (var variable in classInfo.ClassVariables.OrderBy(v => v.ByteRange.StartByte))
                {
                    if (string.Equals(variable.Name, "__slots__", StringComparison.Ordinal))
                        continue;

                    var variableVisibility = VisibilitySymbol(variable.Name);
                    lines.Add($"  {variableVisibility}{variable.Name}{FormatTypeSuffix(variable.TypeAnnotation)} (class)    #symbol={classInfo.QualifiedName}.{variable.Name}");
                }

                foreach (var variable in classInfo.InstanceVariables.OrderBy(v => v.ByteRange.StartByte))
                {
                    lines.Add($"  ~{variable.Name}{FormatTypeSuffix(variable.TypeAnnotation)} (instance)    #symbol={classInfo.QualifiedName}.{variable.Name}");
                }

                foreach (var method in classInfo.Methods.OrderBy(m => m.ByteRange.StartByte))
                {
                    var methodDoc = FirstDocstringLine(method.Docstring);
                    if (!string.IsNullOrWhiteSpace(methodDoc))
                        lines.Add($"  # {methodDoc}");

                    var semantic = BuildMethodSemantics(method.Decorators);
                    lines.Add($"  {BuildMethodStructure(method, semantic)}    #symbol={classInfo.QualifiedName}.{method.Name}");
                }

                if (semantics.GenerateDataclassInit)
                {
                    var generatedParameters = classInfo.ClassVariables
                        .Where(v => !string.Equals(v.Name, "__slots__", StringComparison.Ordinal))
                        .Where(v => !string.IsNullOrWhiteSpace(v.TypeAnnotation))
                        .OrderBy(v => v.ByteRange.StartByte)
                        .Select(v => new PythonParameterInfo(v.Name, v.TypeAnnotation, null, PythonParameterKind.PositionalOrKeyword))
                        .ToArray();

                    lines.Add($"  +__init__({FormatParameters(generatedParameters)}) [generated dataclass]    #symbol={classInfo.QualifiedName}.__init__");
                }

                continue;
            }

            if (entry.Function is not null)
            {
                var function = entry.Function;
                var functionDoc = FirstDocstringLine(function.Docstring);
                if (!string.IsNullOrWhiteSpace(functionDoc))
                    lines.Add($"# {functionDoc}");

                lines.Add($"{BuildFunctionStructure(function)}    #symbol={function.Name}");
                continue;
            }

            if (entry.TypeAlias is not null)
            {
                var definition = string.IsNullOrWhiteSpace(entry.TypeAlias.Definition) ? "object" : entry.TypeAlias.Definition;
                lines.Add($"type {entry.TypeAlias.Name} = {definition}");
                continue;
            }

            if (entry.Constant is not null)
            {
                var typePart = string.IsNullOrWhiteSpace(entry.Constant.Type) ? string.Empty : $": {entry.Constant.Type}";
                var valuePart = string.IsNullOrWhiteSpace(entry.Constant.ValuePreview) ? string.Empty : $" = {entry.Constant.ValuePreview}";
                lines.Add($"{entry.Constant.Name}{typePart}{valuePart}");
            }
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string BuildPrimaryDeclaration(
        string fileName,
        PythonDocumentSurface surface,
        IReadOnlyList<ConstantEntry> constants,
        bool isStub)
    {
        if (string.Equals(fileName, "__init__.py", StringComparison.Ordinal) && surface.AllExports is { Length: > 0 })
        {
            var preview = string.Join(", ", surface.AllExports.Take(4));
            return $"package re-exports: {preview}";
        }

        if (surface.Classes.Count == 1)
        {
            var klass = surface.Classes[0];
            var signature = BuildTypeHeadline(klass);
            return isStub ? $"stub {signature}" : signature;
        }

        if (surface.TypeAliases.Count > 0 && surface.Classes.Count > 0)
            return $"{surface.TypeAliases.Count} type aliases, {surface.Classes.Count} classes";

        if (surface.Classes.Count > 1)
            return $"{surface.Classes.Count} classes";

        if (surface.Functions.Count > 0)
            return $"{surface.Functions.Count} functions";

        if (constants.Count > 0)
            return $"{constants.Count} constants";

        if (surface.TypeAliases.Count > 0)
            return $"{surface.TypeAliases.Count} type aliases";

        return isStub ? "stub module" : "python module";
    }

    private static string? BuildKeyMembers(PythonDocumentSurface surface, IReadOnlyList<ConstantEntry> constants)
    {
        var names = new List<string>();

        foreach (var cls in surface.Classes)
        {
            names.AddRange(cls.Methods
                .Where(m => string.Equals(PythonTreeSitterClient.DetermineVisibility(m.Name), "public", StringComparison.Ordinal))
                .Select(m => m.Name));
        }

        names.AddRange(surface.Functions
            .Where(f => string.Equals(PythonTreeSitterClient.DetermineVisibility(f.Name), "public", StringComparison.Ordinal))
            .Select(f => f.Name));

        if (names.Count == 0)
        {
            names.AddRange(constants.Select(c => c.Name));
        }

        var unique = names
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.Ordinal)
            .Take(8)
            .ToArray();

        return unique.Length == 0 ? null : string.Join(", ", unique);
    }

    private static string BuildTypeHeadline(PythonClassInfo classInfo)
    {
        var bases = classInfo.BaseClasses
            .Select(b => b.Trim())
            .Where(b => !string.IsNullOrWhiteSpace(b))
            .ToArray();

        return bases.Length == 0
            ? $"class {classInfo.QualifiedName}"
            : $"class {classInfo.QualifiedName}({string.Join(", ", bases)})";
    }

    private static string BuildTypeStructure(PythonClassInfo classInfo, ClassSemantics semantics)
    {
        var parts = new List<string>
        {
            $"type_kind={semantics.TypeKind}"
        };
        if (classInfo.Methods.Count > 0)
            parts.Add($"{classInfo.Methods.Count} methods");
        if (classInfo.InstanceVariables.Count + classInfo.ClassVariables.Count > 0)
            parts.Add($"{classInfo.InstanceVariables.Count + classInfo.ClassVariables.Count} variables");

        return string.Join(", ", parts);
    }

    private static string BuildMethodHeadline(PythonMethodInfo method, MethodSemantics semantic)
    {
        if (semantic.Kind == "property")
        {
            return string.IsNullOrWhiteSpace(method.ReturnType)
                ? $"property {method.Name}"
                : $"property {method.Name}: {method.ReturnType}";
        }

        var asyncPrefix = method.IsAsync ? "async " : string.Empty;
        var returnType = string.IsNullOrWhiteSpace(method.ReturnType) ? string.Empty : $" -> {method.ReturnType}";
        return $"method {asyncPrefix}{method.Name}({FormatParameters(method.Parameters)}){returnType}";
    }

    private static string BuildMethodStructure(PythonMethodInfo method, MethodSemantics semantic)
    {
        var visibility = VisibilitySymbol(method.Name);
        if (semantic.Kind == "property")
        {
            return string.IsNullOrWhiteSpace(method.ReturnType)
                ? $"{visibility}{method.Name} (property)"
                : $"{visibility}{method.Name}: {method.ReturnType} (property)";
        }

        var asyncPrefix = method.IsAsync ? "async " : string.Empty;
        var returnType = string.IsNullOrWhiteSpace(method.ReturnType) ? string.Empty : $" -> {method.ReturnType}";
        return $"{visibility}{asyncPrefix}{method.Name}({FormatParameters(method.Parameters)}){returnType}";
    }

    private static string BuildFunctionHeadline(PythonFunctionInfo function)
    {
        var asyncPrefix = function.IsAsync ? "async " : string.Empty;
        var returnType = string.IsNullOrWhiteSpace(function.ReturnType) ? string.Empty : $" -> {function.ReturnType}";
        return $"function {asyncPrefix}{function.Name}({FormatParameters(function.Parameters)}){returnType}";
    }

    private static string BuildFunctionStructure(PythonFunctionInfo function)
    {
        var visibility = VisibilitySymbol(function.Name);
        var asyncPrefix = function.IsAsync ? "async " : string.Empty;
        var returnType = string.IsNullOrWhiteSpace(function.ReturnType) ? string.Empty : $" -> {function.ReturnType}";
        return $"{visibility}{asyncPrefix}{function.Name}({FormatParameters(function.Parameters)}){returnType}";
    }

    private static string FormatParameters(IReadOnlyList<PythonParameterInfo> parameters)
    {
        if (parameters.Count == 0)
            return string.Empty;

        return string.Join(", ", parameters.Select(FormatParameter));
    }

    private static string FormatParameter(PythonParameterInfo parameter)
    {
        var prefix = parameter.Kind switch
        {
            PythonParameterKind.VarPositional => "*",
            PythonParameterKind.VarKeyword => "**",
            _ => string.Empty
        };

        var text = $"{prefix}{parameter.Name}";
        if (!string.IsNullOrWhiteSpace(parameter.Type))
            text += $": {parameter.Type}";
        if (!string.IsNullOrWhiteSpace(parameter.Default))
            text += $" = {parameter.Default}";

        return text;
    }

    private static JsonArray DecoratorsToJson(IReadOnlyList<PythonDecoratorInfo> decorators)
    {
        return new JsonArray(
            decorators
                .Select(d => NormalizeDecoratorName(d.Name))
                .Where(d => !string.IsNullOrWhiteSpace(d))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(static d => (JsonNode?)JsonValue.Create(d))
                .ToArray());
    }

    private static JsonArray ParametersToJson(IReadOnlyList<PythonParameterInfo> parameters)
    {
        return new JsonArray(parameters.Select(p => new JsonObject
        {
            ["name"] = p.Name,
            ["type"] = p.Type,
            ["default"] = p.Default,
            ["kind"] = ParameterKindToString(p.Kind)
        }).ToArray());
    }

    private static JsonArray BuildVariablesJson(PythonClassInfo classInfo)
    {
        var entries = new List<(string Name, string? Type, string Kind, int StartByte)>();

        entries.AddRange(classInfo.ClassVariables
            .Where(v => !string.Equals(v.Name, "__slots__", StringComparison.Ordinal))
            .Select(v => (v.Name, v.TypeAnnotation, "class", v.ByteRange.StartByte)));
        entries.AddRange(classInfo.InstanceVariables
            .Select(v => (v.Name, v.TypeAnnotation, "instance", v.ByteRange.StartByte)));

        return new JsonArray(entries
            .OrderBy(v => v.StartByte)
            .Select(v => new JsonObject
            {
                ["name"] = v.Name,
                ["type"] = v.Type,
                ["variable_kind"] = v.Kind
            })
            .ToArray());
    }

    private static JsonArray BuildConstantsJson(IReadOnlyList<ConstantEntry> constants)
    {
        return new JsonArray(constants
            .OrderBy(c => c.StartByte)
            .Select(c => new JsonObject
            {
                ["name"] = c.Name,
                ["type"] = c.Type,
                ["is_final"] = c.IsFinal,
                ["value_preview"] = c.ValuePreview
            })
            .ToArray());
    }

    private static JsonArray BuildTypeAliasesJson(IReadOnlyList<PythonTypeAliasInfo> aliases)
    {
        return new JsonArray(aliases
            .OrderBy(a => a.ByteRange.StartByte)
            .Select(a => new JsonObject
            {
                ["name"] = a.Name,
                ["definition"] = a.Definition
            })
            .ToArray());
    }

    private static IReadOnlyList<ConstantEntry> BuildConstantEntries(
        PythonDocumentSurface surface,
        IReadOnlyDictionary<string, ClassSemantics> semanticsByClass)
    {
        var constants = new List<ConstantEntry>();

        constants.AddRange(surface.Constants.Select(c => new ConstantEntry(
            Name: c.Name,
            Type: c.TypeAnnotation,
            IsFinal: c.IsFinal,
            ValuePreview: c.ValueText,
            StartByte: c.ByteRange.StartByte)));

        foreach (var classInfo in surface.Classes)
        {
            if (!semanticsByClass.TryGetValue(classInfo.QualifiedName, out var semantics)
                || !string.Equals(semantics.TypeKind, "enum", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var variable in classInfo.ClassVariables)
            {
                if (string.Equals(variable.Name, "__slots__", StringComparison.Ordinal))
                    continue;

                constants.Add(new ConstantEntry(
                    Name: variable.Name,
                    Type: classInfo.QualifiedName,
                    IsFinal: true,
                    ValuePreview: null,
                    StartByte: variable.ByteRange.StartByte));
            }
        }

        return constants;
    }

    private static ClassSemantics BuildClassSemantics(PythonClassInfo classInfo)
    {
        var decoratorNames = classInfo.Decorators
            .Select(d => GetDecoratorSimpleName(d.Name))
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var baseNames = classInfo.BaseClasses
            .Select(GetTypeSimpleName)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .ToArray();

        var metaclass = GetTypeSimpleName(classInfo.Metaclass);

        var typeKind = "class";
        if (decoratorNames.Contains("dataclass"))
        {
            typeKind = "dataclass";
        }
        else if (baseNames.Any(IsEnumBase))
        {
            typeKind = "enum";
        }
        else if (baseNames.Any(n => string.Equals(n, "NamedTuple", StringComparison.OrdinalIgnoreCase)))
        {
            typeKind = "namedtuple";
        }
        else if (baseNames.Any(n => string.Equals(n, "TypedDict", StringComparison.OrdinalIgnoreCase)))
        {
            typeKind = "typeddict";
        }
        else if (baseNames.Any(n => string.Equals(n, "Protocol", StringComparison.OrdinalIgnoreCase)))
        {
            typeKind = "protocol";
        }
        else if (baseNames.Any(n => string.Equals(n, "ABC", StringComparison.OrdinalIgnoreCase))
                 || string.Equals(metaclass, "ABCMeta", StringComparison.OrdinalIgnoreCase))
        {
            typeKind = "abstract";
        }

        var methodAbstract = classInfo.Methods.Any(m => HasDecorator(m.Decorators, "abstractmethod"));
        var isAbstract = string.Equals(typeKind, "abstract", StringComparison.Ordinal)
                         || methodAbstract;
        var explicitInit = classInfo.Methods.Any(m => string.Equals(m.Name, "__init__", StringComparison.Ordinal));
        var generateDataclassInit = string.Equals(typeKind, "dataclass", StringComparison.Ordinal) && !explicitInit;

        return new ClassSemantics(typeKind, isAbstract, generateDataclassInit);
    }

    private static MethodSemantics BuildMethodSemantics(IReadOnlyList<PythonDecoratorInfo> decorators)
    {
        var isProperty = HasDecorator(decorators, "property");
        var isClassMethod = HasDecorator(decorators, "classmethod");
        var isStaticMethod = HasDecorator(decorators, "staticmethod") || isClassMethod;
        var isAbstract = HasDecorator(decorators, "abstractmethod");
        var isOverload = HasDecorator(decorators, "overload");

        return new MethodSemantics(
            Kind: isProperty ? "property" : "method",
            IsStatic: isStaticMethod,
            IsClassMethod: isClassMethod,
            IsAbstract: isAbstract,
            IsOverload: isOverload);
    }

    private static bool HasDecorator(IReadOnlyList<PythonDecoratorInfo> decorators, string expected)
    {
        return decorators.Any(d =>
            string.Equals(GetDecoratorSimpleName(d.Name), expected, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeDecoratorName(string? decorator)
    {
        if (string.IsNullOrWhiteSpace(decorator))
            return string.Empty;

        return decorator.Trim().TrimStart('@');
    }

    private static string GetDecoratorSimpleName(string? decorator)
    {
        var normalized = NormalizeDecoratorName(decorator);
        if (string.IsNullOrWhiteSpace(normalized))
            return string.Empty;

        var parenthesis = normalized.IndexOf('(');
        if (parenthesis >= 0)
            normalized = normalized[..parenthesis];

        var dot = normalized.LastIndexOf('.');
        return dot >= 0 ? normalized[(dot + 1)..] : normalized;
    }

    private static string GetTypeSimpleName(string? typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName))
            return string.Empty;

        var name = typeName.Trim();
        var genericIndex = name.IndexOfAny(['[', '(', '<']);
        if (genericIndex > 0)
            name = name[..genericIndex];

        var dot = name.LastIndexOf('.');
        if (dot >= 0)
            name = name[(dot + 1)..];

        return name.Trim();
    }

    private static bool IsEnumBase(string baseName)
    {
        return baseName switch
        {
            "Enum" or "IntEnum" or "Flag" or "IntFlag" or "StrEnum" => true,
            _ => false
        };
    }

    private static string BuildImportSpecifier(PythonImportInfo import)
    {
        if (!import.IsRelative)
        {
            return import.Module ?? string.Empty;
        }

        var prefix = new string('.', Math.Max(1, import.RelativeLevel));
        if (string.IsNullOrWhiteSpace(import.Module))
            return prefix;

        return $"{prefix}{import.Module}";
    }

    private static string? BuildImportNames(PythonImportInfo import)
    {
        if (import.IsStar)
            return "*";
        if (import.Names.Count == 0)
            return null;

        return string.Join(",", import.Names.Select(n =>
            string.IsNullOrWhiteSpace(n.Alias) ? n.Name : $"{n.Name}:{n.Alias}"));
    }

    private static string BuildMemberQualifiedName(string declaringType, string memberName)
        => $"{declaringType}.{memberName}";

    private static string? TryGetNamespace(string qualifiedName)
    {
        var index = qualifiedName.LastIndexOf('.');
        if (index <= 0)
            return null;

        return qualifiedName[..index];
    }

    private static char VisibilitySymbol(string name)
    {
        var visibility = PythonTreeSitterClient.DetermineVisibility(name);
        return string.Equals(visibility, "private", StringComparison.Ordinal) ? '-' : '+';
    }

    private static string FormatTypeSuffix(string? typeAnnotation)
    {
        return string.IsNullOrWhiteSpace(typeAnnotation)
            ? string.Empty
            : $": {typeAnnotation}";
    }

    private static string ParameterKindToString(PythonParameterKind kind)
    {
        return kind switch
        {
            PythonParameterKind.PositionalOnly => "positional_only",
            PythonParameterKind.PositionalOrKeyword => "positional_or_keyword",
            PythonParameterKind.KeywordOnly => "keyword_only",
            PythonParameterKind.VarPositional => "var_positional",
            PythonParameterKind.VarKeyword => "var_keyword",
            _ => "positional_or_keyword"
        };
    }

    private static int EstimateTokenCount(string text)
        => string.IsNullOrEmpty(text) ? 0 : Math.Max(1, text.Length / 4);

    private static string? FirstDocstringLine(string? docstring)
    {
        if (string.IsNullOrWhiteSpace(docstring))
            return null;

        foreach (var line in docstring.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!string.IsNullOrWhiteSpace(trimmed))
                return trimmed;
        }

        return null;
    }

    private static SemanticMediaType ResolveMediaType(string fileName)
    {
        return PythonMediaTypes.TryResolve(fileName, out var mediaType)
            ? mediaType!
            : PythonMediaTypes.Python;
    }

    private static string? ResolveDocumentRole(RepoUri uri, SemanticMediaType mediaType)
    {
        var fileName = GetFileName(uri);
        if (string.Equals(Path.GetExtension(fileName), ".pyi", StringComparison.OrdinalIgnoreCase)
            || string.Equals(mediaType.Kind, PythonMediaTypes.PythonStub.Kind, StringComparison.OrdinalIgnoreCase))
        {
            return "stub";
        }

        return fileName switch
        {
            "__init__.py" => "package_init",
            "__main__.py" => "entry_point",
            _ => null
        };
    }

    private static bool TryDetectPep263Encoding(string text, out string encodingName)
    {
        encodingName = string.Empty;
        using var reader = new StringReader(text);
        for (var i = 0; i < 2; i++)
        {
            var line = reader.ReadLine();
            if (line is null)
                return false;

            var match = Pep263EncodingRegex.Match(line);
            if (!match.Success)
                continue;

            encodingName = match.Groups["encoding"].Value;
            return !string.IsNullOrWhiteSpace(encodingName);
        }

        return false;
    }

    private static bool TryResolveNonUtf8Encoding(string encodingName, out Encoding encoding)
    {
        encoding = Encoding.UTF8;

        if (string.IsNullOrWhiteSpace(encodingName))
            return false;

        var normalized = encodingName.Replace("_", "-", StringComparison.Ordinal).Trim();
        if (normalized.Equals("utf-8", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("utf8", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            encoding = Encoding.GetEncoding(normalized);
            return encoding.CodePage != Encoding.UTF8.CodePage;
        }
        catch
        {
            return false;
        }
    }

    private static string ReadEmbeddedResource(string resourceName)
    {
        using var stream = typeof(PythonLoader).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded SQL resource {resourceName} was not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string GetFileName(RepoUri uri)
    {
        try
        {
            if (uri.IsFile)
            {
                var lp = uri.LocalPath;
                if (!string.IsNullOrEmpty(lp))
                    return Path.GetFileName(lp);
            }
        }
        catch
        {
            // ignored
        }

        var path = Uri.UnescapeDataString(uri.AbsolutePath);
        var slash = path.LastIndexOf('/') >= 0 ? path[(path.LastIndexOf('/') + 1)..] : path;
        return string.IsNullOrEmpty(slash) ? uri.AbsoluteUri : slash;
    }

    private sealed record RootEntry(
        int StartByte,
        PythonClassInfo? Class = null,
        PythonFunctionInfo? Function = null);

    private sealed record StructureEntry(
        int StartByte,
        PythonClassInfo? Class = null,
        PythonFunctionInfo? Function = null,
        PythonTypeAliasInfo? TypeAlias = null,
        ConstantEntry? Constant = null);

    private sealed record ClassSemantics(
        string TypeKind,
        bool IsAbstract,
        bool GenerateDataclassInit);

    private sealed record MethodSemantics(
        string Kind,
        bool IsStatic,
        bool IsClassMethod,
        bool IsAbstract,
        bool IsOverload);

    private sealed record ConstantEntry(
        string Name,
        string? Type,
        bool IsFinal,
        string? ValuePreview,
        int StartByte);
}
