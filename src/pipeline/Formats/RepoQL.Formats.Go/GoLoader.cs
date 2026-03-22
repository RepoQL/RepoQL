using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RepoQL.Contracts;
using RepoQL.Contracts.Models;
using RepoQL.Formats.Go.GoMod;
using RepoQL.Formats.Go.Surface;
using RepoQL.Formats.Go.TreeSitter;

namespace RepoQL.Formats.Go;

/// <summary>
/// Go format loader and materializer.
///
/// Purpose: Parse Go source into a stable surface model and emit graph records.
///
/// Complexity: Handles classification compatibility, resilient materialization,
/// and X-ray summary generation for Go documents.
/// </summary>
public sealed class GoLoader : IFormatLoader, IFormatMaterializer, IFormatSchemaProvider, IDisposable
{
    private readonly GoTreeSitterClient _client;
    private readonly GoModParser _goModParser;
    private readonly ILogger<GoLoader> _logger;

    private static readonly Lazy<string> GoViewsSql = new(
        () => ReadEmbeddedResource("RepoQL.Formats.Go.Schema.go_views.sql"));

    private const string StateMetadataKey = "go.state";

    public GoLoader(ILogger<GoLoader>? logger = null)
    {
        _logger = logger ?? NullLogger<GoLoader>.Instance;
        _client = new GoTreeSitterClient();
        _goModParser = new GoModParser();
    }

    public bool Supports(SemanticMediaType mediaType)
    {
        ArgumentNullException.ThrowIfNull(mediaType);

        if (GoMediaTypes.IsSupportedKind(mediaType.Kind))
        {
            return true;
        }

        if (!string.Equals(mediaType.Type, "text", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return string.Equals(mediaType.Subtype, "x-go", StringComparison.OrdinalIgnoreCase)
               || string.Equals(mediaType.Subtype, "x-go-mod", StringComparison.OrdinalIgnoreCase)
               || string.Equals(mediaType.Subtype, "x-go-work", StringComparison.OrdinalIgnoreCase);
    }

    public Task<bool> CanLoadAsync(DiscoveredArtifact artifact, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);

        if (GoMediaTypes.TryResolve(artifact.File.Name, out var mediaType))
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
            throw new InvalidOperationException("RepoUri required to load Go files.");

        var loaded = await FileContentReader.ReadAllTextWithDigestAsync(
            artifact.File,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var text = loaded.Text;
        var mediaType = artifact.MediaType ?? GoMediaTypes.Go;

        GoDocumentSurface surface;
        GoModInfo? moduleInfo = null;
        if (GoMediaTypes.IsGoSourceKind(mediaType.Kind))
        {
            surface = _client.Parse(text);
        }
        else if (GoMediaTypes.IsGoModuleMetadataKind(mediaType.Kind))
        {
            surface = CreateEmptySurface(text);
            moduleInfo = _goModParser.Parse(text);
        }
        else
        {
            surface = CreateEmptySurface(text);
        }

        var metadata = new Dictionary<string, object?>
        {
            [StateMetadataKey] = new GoDocumentState(
                Surface: surface,
                ModuleInfo: moduleInfo,
                Digest: loaded.Digest,
                Size: loaded.ByteLength,
                MediaType: mediaType,
                StoreUri: artifact.RepoUri.ToString())
        };

        return new DocumentModel(artifact.RepoUri, mediaType, text, moduleInfo ?? (object)surface, metadata);
    }

    public Records Materialize(DocumentModel document)
    {
        if (!GoMediaTypes.IsSupportedKind(document.MediaType.Kind))
        {
            return Records.Empty;
        }

        var state = document.GetMetadataOrDefault<GoDocumentState>(StateMetadataKey)
            ?? throw new InvalidOperationException("Go document missing state metadata.");

        if (GoMediaTypes.IsGoModuleMetadataKind(document.MediaType.Kind))
        {
            return MaterializeModuleMetadata(document, state);
        }

        if (!GoMediaTypes.IsGoSourceKind(document.MediaType.Kind))
        {
            return Records.Empty;
        }

        var tokenCount = TokenEstimator.EstimateTokensSafe(document.Text);
        string? headline = null;
        string? structure = null;

        try
        {
            headline = BuildHeadline(document, state.Surface, tokenCount);
            structure = BuildStructure(state.Surface);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to build Go X-ray summaries");
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
            Structure = structure,
            TokenCount = tokenCount
        };

        var now = DateTimeOffset.UtcNow;
        var documentId = Guid.NewGuid();
        var packageName = state.Surface.PackageName;
        var docNode = new Node
        {
            Id = documentId,
            Kind = GoNodeKinds.Document,
            Uri = document.Uri,
            ArtifactId = artifact.Id,
            Props = new JsonObject
            {
                [GoPropertyKeys.Language] = GoValues.LanguageName,
                [GoPropertyKeys.PackageName] = packageName,
                [GoPropertyKeys.LineCount] = document.LineMap.LineCount,
                [GoPropertyKeys.ByteSize] = artifact.Size
            },
            CreatedAt = now,
            UpdatedAt = now
        };

        var nodes = new List<Node> { docNode };
        var spans = new List<Span>();
        var edges = new List<Edge>();
        var annotations = new List<Annotation>();
        var typeNodesBySimpleName = new Dictionary<string, Node>(StringComparer.Ordinal);
        var memberOrdinalsByTypeId = new Dictionary<Guid, int>();
        var enumTypeByConstantKey = BuildEnumTypeLookup(state.Surface.ConstantBlocks);
        var isTestFile = IsGoTestFile(document.Uri);
        var ordinal = 0;

        foreach (var structInfo in state.Surface.Structs.OrderBy(s => s.ByteRange.StartByte))
        {
            try
            {
                var typeNode = CreateTypeNode(
                    packageName,
                    structInfo.Name,
                    kind: "struct",
                    structInfo.IsExported,
                    structInfo.ByteRange,
                    document,
                    artifact.Id,
                    documentId,
                    now,
                    out var typeSpan);

                nodes.Add(typeNode);
                spans.Add(typeSpan);
                edges.Add(CreateComposition(documentId, typeNode.Id, ordinal++, documentId, now));
                typeNodesBySimpleName[structInfo.Name] = typeNode;

                foreach (var field in structInfo.Fields.OrderBy(f => f.ByteRange.StartByte))
                {
                    try
                    {
                        var fieldNode = CreateFieldNode(
                            packageName,
                            structInfo.Name,
                            field,
                            document,
                            artifact.Id,
                            documentId,
                            now,
                            out var fieldSpan);

                        nodes.Add(fieldNode);
                        spans.Add(fieldSpan);
                        edges.Add(CreateComposition(
                            typeNode.Id,
                            fieldNode.Id,
                            NextMemberOrdinal(memberOrdinalsByTypeId, typeNode.Id),
                            documentId,
                            now));

                        if (field.IsEmbedded)
                        {
                            edges.Add(CreateReferenceEdge(
                                typeNode.Id,
                                GoEdgeTypes.Embeds,
                                documentId,
                                now,
                                field.TypeName));
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(
                            ex,
                            "Failed to materialize Go struct field {FieldName} on {StructName}",
                            field.Name,
                            structInfo.Name);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to materialize Go struct {StructName}", structInfo.Name);
            }
        }

        foreach (var interfaceInfo in state.Surface.Interfaces.OrderBy(i => i.ByteRange.StartByte))
        {
            try
            {
                var typeNode = CreateTypeNode(
                    packageName,
                    interfaceInfo.Name,
                    kind: "interface",
                    interfaceInfo.IsExported,
                    interfaceInfo.ByteRange,
                    document,
                    artifact.Id,
                    documentId,
                    now,
                    out var typeSpan);

                nodes.Add(typeNode);
                spans.Add(typeSpan);
                edges.Add(CreateComposition(documentId, typeNode.Id, ordinal++, documentId, now));
                typeNodesBySimpleName[interfaceInfo.Name] = typeNode;

                foreach (var embeddedInterface in interfaceInfo.EmbeddedInterfaces)
                {
                    if (string.IsNullOrWhiteSpace(embeddedInterface))
                    {
                        continue;
                    }

                    edges.Add(CreateReferenceEdge(
                        typeNode.Id,
                        GoEdgeTypes.Embeds,
                        documentId,
                        now,
                        embeddedInterface));
                }

                foreach (var method in interfaceInfo.Methods.OrderBy(m => m.ByteRange.StartByte))
                {
                    try
                    {
                        var memberNode = CreateInterfaceMethodNode(
                            packageName,
                            interfaceInfo.Name,
                            method,
                            document,
                            artifact.Id,
                            documentId,
                            now,
                            out var memberSpan);

                        nodes.Add(memberNode);
                        spans.Add(memberSpan);
                        edges.Add(CreateComposition(
                            typeNode.Id,
                            memberNode.Id,
                            NextMemberOrdinal(memberOrdinalsByTypeId, typeNode.Id),
                            documentId,
                            now));
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(
                            ex,
                            "Failed to materialize Go interface method {MethodName} on {InterfaceName}",
                            method.Name,
                            interfaceInfo.Name);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to materialize Go interface {InterfaceName}", interfaceInfo.Name);
            }
        }

        foreach (var typeDefinition in state.Surface.TypeDefinitions.OrderBy(t => t.ByteRange.StartByte))
        {
            try
            {
                var typeNode = CreateTypeNode(
                    packageName,
                    typeDefinition.Name,
                    kind: typeDefinition.IsAlias ? "type_alias" : "type_definition",
                    typeDefinition.IsExported,
                    typeDefinition.ByteRange,
                    document,
                    artifact.Id,
                    documentId,
                    now,
                    out var typeSpan,
                    typeDefinition.UnderlyingType);

                nodes.Add(typeNode);
                spans.Add(typeSpan);
                edges.Add(CreateComposition(documentId, typeNode.Id, ordinal++, documentId, now));
                typeNodesBySimpleName[typeDefinition.Name] = typeNode;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to materialize Go type definition {TypeName}", typeDefinition.Name);
            }
        }

        foreach (var method in state.Surface.Methods.OrderBy(m => m.ByteRange.StartByte))
        {
            try
            {
                var memberNode = CreateMethodNode(
                    packageName,
                    method,
                    document,
                    artifact.Id,
                    documentId,
                    now,
                    out var memberSpan);

                nodes.Add(memberNode);
                spans.Add(memberSpan);

                if (typeNodesBySimpleName.TryGetValue(method.ReceiverType, out var ownerType))
                {
                    edges.Add(CreateComposition(
                        ownerType.Id,
                        memberNode.Id,
                        NextMemberOrdinal(memberOrdinalsByTypeId, ownerType.Id),
                        documentId,
                        now));
                }
                else
                {
                    edges.Add(CreateComposition(documentId, memberNode.Id, ordinal++, documentId, now));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to materialize Go method {MethodName}", method.Name);
            }
        }

        foreach (var constant in state.Surface.Constants.OrderBy(c => c.ByteRange.StartByte))
        {
            try
            {
                var enumType = enumTypeByConstantKey.TryGetValue(
                    BuildConstantKey(constant.Name, constant.ByteRange),
                    out var matchedEnumType)
                    ? matchedEnumType
                    : null;

                var constantNode = CreateConstantNode(
                    packageName,
                    constant,
                    enumType,
                    document,
                    artifact.Id,
                    documentId,
                    now,
                    out var constantSpan);

                nodes.Add(constantNode);
                spans.Add(constantSpan);
                edges.Add(CreateComposition(documentId, constantNode.Id, ordinal++, documentId, now));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to materialize Go constant {ConstantName}", constant.Name);
            }
        }

        foreach (var variable in state.Surface.Variables.OrderBy(v => v.ByteRange.StartByte))
        {
            try
            {
                var variableNode = CreateVariableNode(
                    packageName,
                    variable,
                    document,
                    artifact.Id,
                    documentId,
                    now,
                    out var variableSpan);

                nodes.Add(variableNode);
                spans.Add(variableSpan);
                edges.Add(CreateComposition(documentId, variableNode.Id, ordinal++, documentId, now));

                if (variable.IsInterfaceAssertion)
                {
                    annotations.Add(CreateAnnotation(
                        GoAnnotationKinds.InterfaceAssertion,
                        "info",
                        "go.interface_assertion",
                        string.IsNullOrWhiteSpace(variable.AssertedType) || string.IsNullOrWhiteSpace(variable.AssertedInterface)
                            ? variable.Name
                            : $"{variable.AssertedType} implements {variable.AssertedInterface}",
                        documentId,
                        now,
                        targetNodeId: variableNode.Id,
                        targetSpanId: variableSpan.Id,
                        data: new JsonObject
                        {
                            [GoPropertyKeys.AssertedInterface] = variable.AssertedInterface,
                            [GoPropertyKeys.AssertedType] = variable.AssertedType
                        }));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to materialize Go variable {VariableName}", variable.Name);
            }
        }

        foreach (var function in state.Surface.Functions.OrderBy(f => f.ByteRange.StartByte))
        {
            try
            {
                var isInit = string.Equals(function.Name, "init", StringComparison.Ordinal);
                var hasTestKind = TryGetTestKind(function.Name, isTestFile, out var testKind, out var testsSymbol);
                var functionNode = CreateFunctionNode(
                    packageName,
                    function,
                    document,
                    artifact.Id,
                    documentId,
                    now,
                    out var functionSpan,
                    isInit,
                    hasTestKind ? testKind : null,
                    hasTestKind ? testsSymbol : null);

                nodes.Add(functionNode);
                spans.Add(functionSpan);
                edges.Add(CreateComposition(documentId, functionNode.Id, ordinal++, documentId, now));

                if (hasTestKind)
                {
                    annotations.Add(CreateAnnotation(
                        GoAnnotationKinds.Test,
                        "info",
                        "go.test",
                        function.Name,
                        documentId,
                        now,
                        targetNodeId: functionNode.Id,
                        targetSpanId: functionSpan.Id,
                        data: new JsonObject
                        {
                            [GoPropertyKeys.Name] = function.Name,
                            [GoPropertyKeys.TestKind] = testKind,
                            [GoPropertyKeys.TestsSymbol] = testsSymbol
                        }));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to materialize Go function {FunctionName}", function.Name);
            }
        }

        foreach (var import in state.Surface.Imports.OrderBy(i => i.ByteRange.StartByte))
        {
            try
            {
                edges.Add(CreateImportEdge(documentId, documentId, now, import));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to materialize Go import {ImportPath}", import.Path);
            }
        }

        foreach (var enumBlock in state.Surface.ConstantBlocks.Where(b => b.HasIota && !string.IsNullOrWhiteSpace(b.TypeName)))
        {
            try
            {
                var span = CreateSpan(enumBlock.ByteRange, document, documentId);
                spans.Add(span);

                var constantNames = new JsonArray(enumBlock.Constants
                    .Select(c => (JsonNode?)JsonValue.Create(c.Name))
                    .ToArray());

                annotations.Add(CreateAnnotation(
                    GoAnnotationKinds.EnumBlock,
                    "info",
                    "go.enum_block",
                    enumBlock.TypeName!,
                    documentId,
                    now,
                    targetSpanId: span.Id,
                    data: new JsonObject
                    {
                        ["type_name"] = enumBlock.TypeName,
                        ["constant_names"] = constantNames,
                        ["constant_count"] = enumBlock.Constants.Count
                    }));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to materialize Go enum block for {TypeName}", enumBlock.TypeName);
            }
        }

        foreach (var directive in state.Surface.Directives.OrderBy(d => d.ByteRange.StartByte))
        {
            try
            {
                if (!TryMapDirectiveAnnotation(directive.Kind, out var annotationKind, out var ruleId))
                {
                    continue;
                }

                var span = CreateSpan(directive.ByteRange, document, documentId);
                spans.Add(span);

                annotations.Add(CreateAnnotation(
                    annotationKind,
                    "info",
                    ruleId,
                    directive.Text,
                    documentId,
                    now,
                    targetSpanId: span.Id,
                    data: new JsonObject
                    {
                        ["directive_kind"] = directive.Kind,
                        ["directive_text"] = directive.Text
                    }));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to materialize Go directive {DirectiveKind}", directive.Kind);
            }
        }

        return new Records
        {
            Artifacts = [artifact],
            Nodes = nodes.ToArray(),
            Spans = spans.ToArray(),
            Edges = edges.ToArray(),
            Annotations = annotations.ToArray()
        };
    }

    public IEnumerable<FormatSqlScript> GetSchemaScripts()
    {
        yield return new FormatSqlScript("go_views", GoViewsSql.Value);
    }

    public void Dispose()
    {
        _client.Dispose();
    }

    private Records MaterializeModuleMetadata(DocumentModel document, GoDocumentState state)
    {
        var moduleInfo = state.ModuleInfo ?? _goModParser.Parse(document.Text);
        var tokenCount = TokenEstimator.EstimateTokensSafe(document.Text);
        string? headline = null;
        string? structure = null;

        try
        {
            headline = BuildModuleMetadataHeadline(document, moduleInfo);
            structure = BuildModuleMetadataStructure(document.MediaType.Kind, moduleInfo);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to build Go module metadata X-ray summaries");
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
            Structure = structure,
            TokenCount = tokenCount
        };

        var now = DateTimeOffset.UtcNow;
        var documentId = Guid.NewGuid();
        var language = GoMediaTypes.IsGoModKind(document.MediaType.Kind)
            ? GoValues.GoModLanguageName
            : GoValues.GoWorkLanguageName;

        var props = new JsonObject
        {
            [GoPropertyKeys.Language] = language,
            [GoPropertyKeys.LineCount] = document.LineMap.LineCount,
            [GoPropertyKeys.ByteSize] = artifact.Size
        };
        if (!string.IsNullOrWhiteSpace(moduleInfo.ModulePath))
        {
            props[GoPropertyKeys.ModulePath] = moduleInfo.ModulePath;
        }

        if (!string.IsNullOrWhiteSpace(moduleInfo.GoVersion))
        {
            props[GoPropertyKeys.GoVersion] = moduleInfo.GoVersion;
        }

        if (!string.IsNullOrWhiteSpace(moduleInfo.Toolchain))
        {
            props[GoPropertyKeys.Toolchain] = moduleInfo.Toolchain;
        }

        var docNode = new Node
        {
            Id = documentId,
            Kind = GoNodeKinds.Document,
            Uri = document.Uri,
            ArtifactId = artifact.Id,
            Props = props,
            CreatedAt = now,
            UpdatedAt = now
        };

        var edges = new List<Edge>();
        var annotations = new List<Annotation>();

        if (GoMediaTypes.IsGoModKind(document.MediaType.Kind))
        {
            foreach (var requirement in moduleInfo.Requirements)
            {
                try
                {
                    edges.Add(CreateDependsOnEdge(documentId, documentId, now, requirement));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to materialize Go module dependency {ModulePath}", requirement.ModulePath);
                }
            }
        }

        foreach (var replacement in moduleInfo.Replacements)
        {
            try
            {
                annotations.Add(CreateAnnotation(
                    GoAnnotationKinds.GoModReplace,
                    "info",
                    "go.mod.replace",
                    $"{replacement.OldPath} => {replacement.NewPath}",
                    documentId,
                    now,
                    data: new JsonObject
                    {
                        [GoPropertyKeys.OldPath] = replacement.OldPath,
                        [GoPropertyKeys.OldVersion] = replacement.OldVersion,
                        [GoPropertyKeys.NewPath] = replacement.NewPath,
                        [GoPropertyKeys.NewVersion] = replacement.NewVersion,
                        [GoPropertyKeys.IsLocalPath] = replacement.IsLocalPath
                    }));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to materialize Go replacement {OldPath}", replacement.OldPath);
            }
        }

        if (GoMediaTypes.IsGoModKind(document.MediaType.Kind))
        {
            foreach (var retraction in moduleInfo.Retractions)
            {
                try
                {
                    var message = retraction.Low == retraction.High
                        ? retraction.Low
                        : $"[{retraction.Low}, {retraction.High}]";
                    annotations.Add(CreateAnnotation(
                        GoAnnotationKinds.GoModRetract,
                        "info",
                        "go.mod.retract",
                        message,
                        documentId,
                        now,
                        data: new JsonObject
                        {
                            ["low"] = retraction.Low,
                            ["high"] = retraction.High,
                            ["comment"] = retraction.Comment
                        }));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to materialize Go retraction {Low}..{High}", retraction.Low, retraction.High);
                }
            }
        }

        if (GoMediaTypes.IsGoWorkKind(document.MediaType.Kind))
        {
            foreach (var use in moduleInfo.Uses)
            {
                try
                {
                    annotations.Add(CreateAnnotation(
                        GoAnnotationKinds.GoWorkUse,
                        "info",
                        "go.work.use",
                        use.Path,
                        documentId,
                        now,
                        data: new JsonObject
                        {
                            [GoPropertyKeys.Path] = use.Path
                        }));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to materialize Go workspace use path {Path}", use.Path);
                }
            }
        }

        return new Records
        {
            Artifacts = [artifact],
            Nodes = [docNode],
            Spans = [],
            Edges = edges.ToArray(),
            Annotations = annotations.ToArray()
        };
    }

    private static int NextMemberOrdinal(Dictionary<Guid, int> ordinals, Guid ownerId)
    {
        var ordinal = ordinals.TryGetValue(ownerId, out var current) ? current : 0;
        ordinals[ownerId] = ordinal + 1;
        return ordinal;
    }

    private static GoDocumentSurface CreateEmptySurface(string text)
    {
        var lineCount = text.Length == 0 ? 0 : text.Count(ch => ch == '\n') + 1;
        return new GoDocumentSurface(
            PackageName: null,
            Imports: [],
            Structs: [],
            Interfaces: [],
            TypeDefinitions: [],
            Constants: [],
            ConstantBlocks: [],
            Variables: [],
            Directives: [],
            Functions: [],
            InitFunctions: [],
            Methods: [],
            Stats: new GoParseStats(0, 0, 0, 0, 0, lineCount),
            ErrorNodeCount: 0);
    }

    private static Node CreateTypeNode(
        string? packageName,
        string name,
        string kind,
        bool isExported,
        GoByteRange byteRange,
        DocumentModel document,
        Guid artifactId,
        Guid documentId,
        DateTimeOffset now,
        out Span span,
        string? underlyingType = null)
    {
        span = CreateSpan(byteRange, document, documentId);

        var qualifiedName = BuildQualifiedName(packageName, name);
        var accessibility = ToAccessibility(isExported);
        var props = new JsonObject
        {
            [GoPropertyKeys.Name] = name,
            [GoPropertyKeys.QualifiedName] = qualifiedName,
            [GoPropertyKeys.Kind] = kind,
            [GoPropertyKeys.Accessibility] = accessibility,
            [GoPropertyKeys.IsExported] = isExported
        };
        if (!string.IsNullOrWhiteSpace(underlyingType))
        {
            props[GoPropertyKeys.UnderlyingType] = underlyingType;
        }

        return new Node
        {
            Id = Guid.NewGuid(),
            Kind = GoNodeKinds.Type,
            SpanId = span.Id,
            Uri = RepoUri.FromSymbol(document.Uri.Container, qualifiedName, span.StartLine, span.EndLine),
            ArtifactId = artifactId,
            Props = props,
            Headline = $"{kind} {qualifiedName}",
            Structure = string.IsNullOrWhiteSpace(underlyingType)
                ? $"{VisibilitySymbol(isExported)} {kind} {name}"
                : $"{VisibilitySymbol(isExported)} {kind} {name} {underlyingType}",
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static Node CreateFieldNode(
        string? packageName,
        string declaringTypeName,
        GoFieldInfo field,
        DocumentModel document,
        Guid artifactId,
        Guid documentId,
        DateTimeOffset now,
        out Span span)
    {
        span = CreateSpan(field.ByteRange, document, documentId);

        var declaringTypeQualifiedName = BuildQualifiedName(packageName, declaringTypeName);
        var qualifiedName = BuildQualifiedName(declaringTypeQualifiedName, field.Name);
        var accessibility = ToAccessibility(field.IsExported);
        var signature = $"field {field.Name} {field.TypeName}".Trim();

        return new Node
        {
            Id = Guid.NewGuid(),
            Kind = GoNodeKinds.Member,
            SpanId = span.Id,
            Uri = RepoUri.FromSymbol(document.Uri.Container, qualifiedName, span.StartLine, span.EndLine),
            ArtifactId = artifactId,
            Props = new JsonObject
            {
                [GoPropertyKeys.Name] = field.Name,
                [GoPropertyKeys.QualifiedName] = qualifiedName,
                [GoPropertyKeys.Kind] = "field",
                [GoPropertyKeys.DeclaringType] = declaringTypeQualifiedName,
                [GoPropertyKeys.Accessibility] = accessibility,
                [GoPropertyKeys.IsStatic] = false,
                [GoPropertyKeys.FieldType] = field.TypeName,
                [GoPropertyKeys.Tag] = field.Tag,
                [GoPropertyKeys.IsEmbedded] = field.IsEmbedded,
                [GoPropertyKeys.Signature] = signature,
                [GoPropertyKeys.IsExported] = field.IsExported
            },
            Headline = signature,
            Structure = $"{VisibilitySymbol(field.IsExported)} field {field.Name} {field.TypeName}".TrimEnd(),
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static Node CreateConstantNode(
        string? packageName,
        GoConstantInfo constant,
        string? enumType,
        DocumentModel document,
        Guid artifactId,
        Guid documentId,
        DateTimeOffset now,
        out Span span)
    {
        span = CreateSpan(constant.ByteRange, document, documentId);

        var qualifiedName = BuildQualifiedName(packageName, constant.Name);
        var accessibility = ToAccessibility(constant.IsExported);
        var signature = BuildConstantSignature(constant);
        var props = new JsonObject
        {
            [GoPropertyKeys.Name] = constant.Name,
            [GoPropertyKeys.QualifiedName] = qualifiedName,
            [GoPropertyKeys.Kind] = "constant",
            [GoPropertyKeys.DeclaringType] = null,
            [GoPropertyKeys.Accessibility] = accessibility,
            [GoPropertyKeys.IsStatic] = true,
            [GoPropertyKeys.ConstType] = constant.TypeName,
            [GoPropertyKeys.ConstValue] = constant.Value,
            [GoPropertyKeys.EnumType] = enumType,
            [GoPropertyKeys.Signature] = signature,
            [GoPropertyKeys.IsExported] = constant.IsExported
        };

        return new Node
        {
            Id = Guid.NewGuid(),
            Kind = GoNodeKinds.Member,
            SpanId = span.Id,
            Uri = RepoUri.FromSymbol(document.Uri.Container, qualifiedName, span.StartLine, span.EndLine),
            ArtifactId = artifactId,
            Props = props,
            Headline = signature,
            Structure = $"{VisibilitySymbol(constant.IsExported)} {signature}",
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static Node CreateVariableNode(
        string? packageName,
        GoVariableInfo variable,
        DocumentModel document,
        Guid artifactId,
        Guid documentId,
        DateTimeOffset now,
        out Span span)
    {
        span = CreateSpan(variable.ByteRange, document, documentId);

        var qualifiedName = BuildQualifiedName(packageName, variable.Name);
        var accessibility = ToAccessibility(variable.IsExported);
        var signature = BuildVariableSignature(variable);
        var props = new JsonObject
        {
            [GoPropertyKeys.Name] = variable.Name,
            [GoPropertyKeys.QualifiedName] = qualifiedName,
            [GoPropertyKeys.Kind] = "variable",
            [GoPropertyKeys.DeclaringType] = null,
            [GoPropertyKeys.Accessibility] = accessibility,
            [GoPropertyKeys.IsStatic] = true,
            [GoPropertyKeys.VarType] = variable.TypeName,
            [GoPropertyKeys.VarValue] = variable.Value,
            [GoPropertyKeys.IsSentinelError] = variable.IsSentinelError,
            [GoPropertyKeys.IsInterfaceAssertion] = variable.IsInterfaceAssertion,
            [GoPropertyKeys.AssertedInterface] = variable.AssertedInterface,
            [GoPropertyKeys.AssertedType] = variable.AssertedType,
            [GoPropertyKeys.Signature] = signature,
            [GoPropertyKeys.IsExported] = variable.IsExported
        };

        return new Node
        {
            Id = Guid.NewGuid(),
            Kind = GoNodeKinds.Member,
            SpanId = span.Id,
            Uri = RepoUri.FromSymbol(document.Uri.Container, qualifiedName, span.StartLine, span.EndLine),
            ArtifactId = artifactId,
            Props = props,
            Headline = signature,
            Structure = $"{VisibilitySymbol(variable.IsExported)} {signature}",
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static Node CreateMethodNode(
        string? packageName,
        GoMethodInfo method,
        DocumentModel document,
        Guid artifactId,
        Guid documentId,
        DateTimeOffset now,
        out Span span)
    {
        span = CreateSpan(method.ByteRange, document, documentId);

        var receiverType = string.IsNullOrWhiteSpace(method.ReceiverType) ? "(unknown)" : method.ReceiverType.Trim();
        var declaringType = BuildQualifiedName(packageName, receiverType);
        var qualifiedName = BuildQualifiedName(declaringType, method.Name);
        var accessibility = ToAccessibility(method.IsExported);
        var signature = BuildMethodSignature(method);

        return new Node
        {
            Id = Guid.NewGuid(),
            Kind = GoNodeKinds.Member,
            SpanId = span.Id,
            Uri = RepoUri.FromSymbol(document.Uri.Container, qualifiedName, span.StartLine, span.EndLine),
            ArtifactId = artifactId,
            Props = new JsonObject
            {
                [GoPropertyKeys.Name] = method.Name,
                [GoPropertyKeys.QualifiedName] = qualifiedName,
                [GoPropertyKeys.Kind] = "method",
                [GoPropertyKeys.DeclaringType] = declaringType,
                [GoPropertyKeys.Accessibility] = accessibility,
                [GoPropertyKeys.IsStatic] = false,
                [GoPropertyKeys.Parameters] = method.Parameters,
                [GoPropertyKeys.ReturnType] = method.ReturnType,
                [GoPropertyKeys.Signature] = signature,
                [GoPropertyKeys.IsExported] = method.IsExported,
                [GoPropertyKeys.Receiver] = method.ReceiverName,
                [GoPropertyKeys.ReceiverType] = method.ReceiverType,
                [GoPropertyKeys.IsPointerReceiver] = method.IsPointerReceiver
            },
            Headline = signature,
            Structure = $"{VisibilitySymbol(method.IsExported)} {signature}",
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static Node CreateInterfaceMethodNode(
        string? packageName,
        string interfaceName,
        GoInterfaceMethodInfo method,
        DocumentModel document,
        Guid artifactId,
        Guid documentId,
        DateTimeOffset now,
        out Span span)
    {
        span = CreateSpan(method.ByteRange, document, documentId);

        var declaringType = BuildQualifiedName(packageName, interfaceName);
        var qualifiedName = BuildQualifiedName(declaringType, method.Name);
        var isExported = IsExportedName(method.Name);
        var accessibility = ToAccessibility(isExported);
        var signature = BuildInterfaceMethodSignature(method);

        return new Node
        {
            Id = Guid.NewGuid(),
            Kind = GoNodeKinds.Member,
            SpanId = span.Id,
            Uri = RepoUri.FromSymbol(document.Uri.Container, qualifiedName, span.StartLine, span.EndLine),
            ArtifactId = artifactId,
            Props = new JsonObject
            {
                [GoPropertyKeys.Name] = method.Name,
                [GoPropertyKeys.QualifiedName] = qualifiedName,
                [GoPropertyKeys.Kind] = "method",
                [GoPropertyKeys.DeclaringType] = declaringType,
                [GoPropertyKeys.Accessibility] = accessibility,
                [GoPropertyKeys.IsStatic] = false,
                [GoPropertyKeys.Parameters] = method.Parameters,
                [GoPropertyKeys.ReturnType] = method.ReturnType,
                [GoPropertyKeys.Signature] = signature,
                [GoPropertyKeys.IsExported] = isExported,
                [GoPropertyKeys.Receiver] = null,
                [GoPropertyKeys.ReceiverType] = interfaceName,
                [GoPropertyKeys.IsPointerReceiver] = false
            },
            Headline = signature,
            Structure = $"{VisibilitySymbol(isExported)} {signature}",
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static Node CreateFunctionNode(
        string? packageName,
        GoFunctionInfo function,
        DocumentModel document,
        Guid artifactId,
        Guid documentId,
        DateTimeOffset now,
        out Span span,
        bool isInit = false,
        string? testKind = null,
        string? testsSymbol = null)
    {
        span = CreateSpan(function.ByteRange, document, documentId);

        var qualifiedName = BuildQualifiedName(packageName, function.Name);
        var accessibility = ToAccessibility(function.IsExported);
        var signature = BuildFunctionSignature(function);
        var props = new JsonObject
        {
            [GoPropertyKeys.Name] = function.Name,
            [GoPropertyKeys.QualifiedName] = qualifiedName,
            [GoPropertyKeys.Kind] = "function",
            [GoPropertyKeys.Accessibility] = accessibility,
            [GoPropertyKeys.Parameters] = function.Parameters,
            [GoPropertyKeys.ReturnType] = function.ReturnType,
            [GoPropertyKeys.Signature] = signature,
            [GoPropertyKeys.IsExported] = function.IsExported,
            [GoPropertyKeys.IsInit] = isInit
        };
        if (!string.IsNullOrWhiteSpace(testKind))
        {
            props[GoPropertyKeys.TestKind] = testKind;
        }

        if (!string.IsNullOrWhiteSpace(testsSymbol))
        {
            props[GoPropertyKeys.TestsSymbol] = testsSymbol;
        }

        return new Node
        {
            Id = Guid.NewGuid(),
            Kind = GoNodeKinds.Function,
            SpanId = span.Id,
            Uri = RepoUri.FromSymbol(document.Uri.Container, qualifiedName, span.StartLine, span.EndLine),
            ArtifactId = artifactId,
            Props = props,
            Headline = signature,
            Structure = $"{VisibilitySymbol(function.IsExported)} {signature}",
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static Edge CreateComposition(Guid srcId, Guid dstId, int ordinal, Guid scopeDocId, DateTimeOffset now)
        => new()
        {
            Id = Guid.NewGuid(),
            SrcId = srcId,
            DstId = dstId,
            Type = GoEdgeTypes.HasPart,
            IsComposition = true,
            Ordinal = ordinal,
            ScopeDocumentId = scopeDocId,
            CreatedAt = now
        };

    private static Edge CreateReferenceEdge(
        Guid srcId,
        string edgeType,
        Guid scopeDocId,
        DateTimeOffset now,
        string? target)
    {
        var props = new JsonObject();
        if (!string.IsNullOrWhiteSpace(target))
        {
            props[GoPropertyKeys.Target] = target;
        }

        return new Edge
        {
            Id = Guid.NewGuid(),
            SrcId = srcId,
            DstId = null,
            Type = edgeType,
            IsComposition = false,
            ScopeDocumentId = scopeDocId,
            Props = props,
            CreatedAt = now
        };
    }

    private static Edge CreateImportEdge(Guid srcId, Guid scopeDocId, DateTimeOffset now, GoImportInfo import)
    {
        return new Edge
        {
            Id = Guid.NewGuid(),
            SrcId = srcId,
            DstId = null,
            Type = GoEdgeTypes.Imports,
            IsComposition = false,
            ScopeDocumentId = scopeDocId,
            Props = new JsonObject
            {
                [GoPropertyKeys.Target] = import.Path,
                [GoPropertyKeys.Alias] = import.Alias,
                [GoPropertyKeys.ImportCategory] = import.Category
            },
            CreatedAt = now
        };
    }

    private static Edge CreateDependsOnEdge(Guid srcId, Guid scopeDocId, DateTimeOffset now, GoModRequirement requirement)
    {
        return new Edge
        {
            Id = Guid.NewGuid(),
            SrcId = srcId,
            DstId = null,
            Type = GoEdgeTypes.DependsOn,
            IsComposition = false,
            ScopeDocumentId = scopeDocId,
            Props = new JsonObject
            {
                [GoPropertyKeys.Target] = requirement.ModulePath,
                [GoPropertyKeys.Version] = requirement.Version,
                [GoPropertyKeys.Indirect] = requirement.IsIndirect
            },
            CreatedAt = now
        };
    }

    private static Span CreateSpan(GoByteRange range, DocumentModel document, Guid documentId)
    {
        var start = Math.Clamp(range.StartByte, 0, document.Text.Length);
        var end = Math.Clamp(range.EndByte, start, document.Text.Length);
        var mapped = document.LineMap.GetSpan(start, end);
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

    private static Dictionary<string, string> BuildEnumTypeLookup(IReadOnlyList<GoConstantBlockInfo> constantBlocks)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var block in constantBlocks.Where(b => b.HasIota && !string.IsNullOrWhiteSpace(b.TypeName)))
        {
            foreach (var constant in block.Constants)
            {
                map[BuildConstantKey(constant.Name, constant.ByteRange)] = block.TypeName!;
            }
        }

        return map;
    }

    private static string BuildConstantKey(string name, GoByteRange byteRange)
        => $"{name}:{byteRange.StartByte}:{byteRange.EndByte}";

    private static bool TryMapDirectiveAnnotation(string directiveKind, out string annotationKind, out string ruleId)
    {
        switch (directiveKind)
        {
            case "build":
                annotationKind = GoAnnotationKinds.BuildConstraint;
                ruleId = "go.build";
                return true;
            case "generate":
                annotationKind = GoAnnotationKinds.Generate;
                ruleId = "go.generate";
                return true;
            case "embed":
                annotationKind = GoAnnotationKinds.Embed;
                ruleId = "go.embed";
                return true;
            case "linkname":
                annotationKind = GoAnnotationKinds.Linkname;
                ruleId = "go.linkname";
                return true;
            case "goroutine":
                annotationKind = GoAnnotationKinds.Goroutine;
                ruleId = "go.goroutine";
                return true;
            case "channel":
                annotationKind = GoAnnotationKinds.Channel;
                ruleId = "go.channel";
                return true;
            case "select":
                annotationKind = GoAnnotationKinds.Select;
                ruleId = "go.select";
                return true;
            default:
                annotationKind = string.Empty;
                ruleId = string.Empty;
                return false;
        }
    }

    private static bool IsGoTestFile(RepoUri uri)
        => GetFileName(uri).EndsWith("_test.go", StringComparison.OrdinalIgnoreCase);

    private static bool TryGetTestKind(
        string functionName,
        bool isTestFile,
        out string testKind,
        out string testsSymbol)
    {
        testKind = string.Empty;
        testsSymbol = string.Empty;

        if (!isTestFile || string.IsNullOrWhiteSpace(functionName))
        {
            return false;
        }

        if (string.Equals(functionName, "TestMain", StringComparison.Ordinal))
        {
            testKind = "testmain";
            testsSymbol = "Main";
            return true;
        }

        if (TryMatchTestPattern(functionName, "Test", out testsSymbol)
            || TryMatchTestPattern(functionName, "Benchmark", out testsSymbol)
            || TryMatchTestPattern(functionName, "Example", out testsSymbol)
            || TryMatchTestPattern(functionName, "Fuzz", out testsSymbol))
        {
            testKind = functionName.StartsWith("Benchmark", StringComparison.Ordinal) ? "benchmark"
                : functionName.StartsWith("Example", StringComparison.Ordinal) ? "example"
                : functionName.StartsWith("Fuzz", StringComparison.Ordinal) ? "fuzz"
                : "test";
            return true;
        }

        return false;
    }

    private static bool TryMatchTestPattern(string functionName, string prefix, out string testsSymbol)
    {
        testsSymbol = string.Empty;
        if (!functionName.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        if (functionName.Length <= prefix.Length)
        {
            return false;
        }

        var symbol = functionName[prefix.Length..];
        if (symbol.Length == 0 || !char.IsUpper(symbol[0]))
        {
            return false;
        }

        testsSymbol = symbol;
        return true;
    }

    private static Annotation CreateAnnotation(
        string kind,
        string severity,
        string ruleId,
        string message,
        Guid documentId,
        DateTimeOffset createdAt,
        Guid? targetNodeId = null,
        Guid? targetSpanId = null,
        JsonObject? data = null)
    {
        return new Annotation
        {
            Id = Guid.NewGuid(),
            Kind = kind,
            Severity = severity,
            Source = GoValues.AnnotationSource,
            RuleId = ruleId,
            Message = message,
            Data = data ?? new JsonObject(),
            ScopeDocumentId = documentId,
            TargetNodeId = targetNodeId,
            TargetSpanId = targetSpanId,
            CreatedAt = createdAt
        };
    }

    private static string BuildModuleMetadataHeadline(DocumentModel document, GoModInfo moduleInfo)
    {
        var fileName = GetFileName(document.Uri);
        var lineCount = document.LineMap.LineCount;

        if (GoMediaTypes.IsGoModKind(document.MediaType.Kind))
        {
            var directCount = moduleInfo.Requirements.Count(r => !r.IsIndirect);
            var indirectCount = moduleInfo.Requirements.Count(r => r.IsIndirect);
            var modulePath = string.IsNullOrWhiteSpace(moduleInfo.ModulePath) ? "(none)" : moduleInfo.ModulePath;
            return $"{fileName} | {GoMediaTypes.GoMod.Kind} | {lineCount} ln | module:{modulePath} | {directCount} direct, {indirectCount} indirect deps";
        }

        return $"{fileName} | {GoMediaTypes.GoWork.Kind} | {lineCount} ln | {moduleInfo.Uses.Count} workspace modules";
    }

    private static string BuildModuleMetadataStructure(string? mediaKind, GoModInfo moduleInfo)
    {
        var lines = new List<string>();
        if (GoMediaTypes.IsGoModKind(mediaKind))
        {
            if (!string.IsNullOrWhiteSpace(moduleInfo.ModulePath))
            {
                lines.Add($"module {moduleInfo.ModulePath}");
            }

            if (!string.IsNullOrWhiteSpace(moduleInfo.GoVersion))
            {
                lines.Add($"go {moduleInfo.GoVersion}");
            }

            if (!string.IsNullOrWhiteSpace(moduleInfo.Toolchain))
            {
                lines.Add($"toolchain {moduleInfo.Toolchain}");
            }

            var directDependencies = moduleInfo.Requirements.Where(r => !r.IsIndirect).ToList();
            if (directDependencies.Count > 0)
            {
                lines.Add("direct dependencies:");
                lines.AddRange(directDependencies.Select(r => $"  {r.ModulePath} {r.Version}"));
            }

            var indirectDependencies = moduleInfo.Requirements.Where(r => r.IsIndirect).ToList();
            if (indirectDependencies.Count > 0)
            {
                lines.Add("indirect dependencies:");
                lines.AddRange(indirectDependencies.Select(r => $"  {r.ModulePath} {r.Version}"));
            }
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(moduleInfo.GoVersion))
            {
                lines.Add($"go {moduleInfo.GoVersion}");
            }

            if (!string.IsNullOrWhiteSpace(moduleInfo.Toolchain))
            {
                lines.Add($"toolchain {moduleInfo.Toolchain}");
            }

            if (moduleInfo.Uses.Count > 0)
            {
                lines.Add("workspace modules:");
                lines.AddRange(moduleInfo.Uses.Select(u => $"  {u.Path}"));
            }
        }

        if (moduleInfo.Replacements.Count > 0)
        {
            lines.Add("replacements:");
            lines.AddRange(moduleInfo.Replacements.Select(r =>
                $"  {r.OldPath}{(string.IsNullOrWhiteSpace(r.OldVersion) ? string.Empty : $" {r.OldVersion}")} => {r.NewPath}{(string.IsNullOrWhiteSpace(r.NewVersion) ? string.Empty : $" {r.NewVersion}")}"));
        }

        if (GoMediaTypes.IsGoModKind(mediaKind) && moduleInfo.Retractions.Count > 0)
        {
            lines.Add("retractions:");
            lines.AddRange(moduleInfo.Retractions.Select(r =>
            {
                var range = r.Low == r.High ? r.Low : $"[{r.Low}, {r.High}]";
                return string.IsNullOrWhiteSpace(r.Comment) ? $"  {range}" : $"  {range} // {r.Comment}";
            }));
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string BuildHeadline(DocumentModel document, GoDocumentSurface surface, int? tokenCount)
    {
        var fileName = GetFileName(document.Uri);
        var sizePart = $"{document.LineMap.LineCount} ln";
        if (tokenCount.HasValue)
        {
            sizePart = $"{sizePart}, ~{tokenCount.Value} tok";
        }

        var packagePart = $"pkg:{(string.IsNullOrWhiteSpace(surface.PackageName) ? "(none)" : surface.PackageName)}";
        var declaration = BuildPrimaryDeclaration(surface);
        var keyNames = BuildKeyNames(surface);

        return string.Join(
            " | ",
            new[] { fileName, "code.go", sizePart, packagePart, declaration, keyNames }
                .Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    private static string BuildPrimaryDeclaration(GoDocumentSurface surface)
    {
        if (surface.Functions.Any(f => string.Equals(f.Name, "main", StringComparison.Ordinal)))
        {
            return "func main";
        }

        if (surface.Structs.Count == 1 && surface.Interfaces.Count == 0)
        {
            return surface.Structs[0].Name;
        }

        if (surface.Interfaces.Count == 1 && surface.Structs.Count == 0)
        {
            return $"interface {surface.Interfaces[0].Name}";
        }

        if (surface.Functions.Count == 1 && surface.Structs.Count == 0 && surface.Interfaces.Count == 0)
        {
            return $"func {surface.Functions[0].Name}";
        }

        var typeCount = surface.Structs.Count + surface.Interfaces.Count + surface.TypeDefinitions.Count;
        var functionCount = surface.Functions.Count;
        if (typeCount > 0 || functionCount > 0)
        {
            return $"{typeCount} types, {functionCount} funcs";
        }

        return "go file";
    }

    private static string? BuildKeyNames(GoDocumentSurface surface)
    {
        var names = new List<string>();
        names.AddRange(surface.Structs.Where(s => s.IsExported).Select(s => s.Name));
        names.AddRange(surface.Interfaces.Where(i => i.IsExported).Select(i => i.Name));
        names.AddRange(surface.TypeDefinitions.Where(t => t.IsExported).Select(t => t.Name));
        names.AddRange(surface.Constants.Where(c => c.IsExported).Select(c => c.Name));
        names.AddRange(surface.Variables.Where(v => v.IsExported).Select(v => v.Name));
        names.AddRange(surface.Functions.Where(f => f.IsExported).Select(f => f.Name));
        names.AddRange(surface.Methods.Where(m => m.IsExported).Select(m => m.Name));
        names.AddRange(surface.Interfaces
            .SelectMany(i => i.Methods)
            .Where(m => IsExportedName(m.Name))
            .Select(m => m.Name));

        var unique = names
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.Ordinal)
            .Take(8)
            .ToList();

        return unique.Count == 0 ? null : string.Join(", ", unique);
    }

    private static string BuildStructure(GoDocumentSurface surface)
    {
        var lines = new List<string>();
        var methodsByReceiver = surface.Methods
            .GroupBy(m => m.ReceiverType, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderBy(m => m.ByteRange.StartByte).ToList(), StringComparer.Ordinal);

        var rootEntries = new List<RootEntry>();
        rootEntries.AddRange(surface.Structs.Select(s => new RootEntry("struct", s.ByteRange.StartByte, Struct: s)));
        rootEntries.AddRange(surface.Interfaces.Select(i => new RootEntry("interface", i.ByteRange.StartByte, Interface: i)));
        rootEntries.AddRange(surface.Functions.Select(f => new RootEntry("function", f.ByteRange.StartByte, Function: f)));

        var knownTypeNames = new HashSet<string>(surface.Structs.Select(s => s.Name), StringComparer.Ordinal);
        knownTypeNames.UnionWith(surface.Interfaces.Select(i => i.Name));

        foreach (var entry in rootEntries.OrderBy(e => e.StartByte))
        {
            if (entry.Struct is not null)
            {
                var structInfo = entry.Struct;
                lines.Add($"{VisibilitySymbol(structInfo.IsExported)} struct {structInfo.Name}");

                foreach (var field in structInfo.Fields.OrderBy(f => f.ByteRange.StartByte))
                {
                    lines.Add(
                        $"  {VisibilitySymbol(field.IsExported)} field {field.Name} {field.TypeName}    #symbol={field.Name}");
                }

                if (methodsByReceiver.TryGetValue(structInfo.Name, out var methods))
                {
                    foreach (var method in methods)
                    {
                        lines.Add($"  {VisibilitySymbol(method.IsExported)} {BuildMethodSignature(method)}    #symbol={method.Name}");
                    }
                }

                continue;
            }

            if (entry.Interface is not null)
            {
                var interfaceInfo = entry.Interface;
                lines.Add($"{VisibilitySymbol(interfaceInfo.IsExported)} interface {interfaceInfo.Name}");

                foreach (var embedded in interfaceInfo.EmbeddedInterfaces)
                {
                    if (string.IsNullOrWhiteSpace(embedded))
                    {
                        continue;
                    }

                    lines.Add($"  {VisibilitySymbol(IsExportedName(embedded))} embeds {embedded}");
                }

                foreach (var method in interfaceInfo.Methods.OrderBy(m => m.ByteRange.StartByte))
                {
                    lines.Add(
                        $"  {VisibilitySymbol(IsExportedName(method.Name))} {BuildInterfaceMethodSignature(method)}    #symbol={method.Name}");
                }

                continue;
            }

            if (entry.Function is not null)
            {
                var function = entry.Function;
                lines.Add($"{VisibilitySymbol(function.IsExported)} {BuildFunctionSignature(function)}    #symbol={function.Name}");
            }
        }

        foreach (var orphan in surface.Methods
                     .Where(m => !knownTypeNames.Contains(m.ReceiverType))
                     .OrderBy(m => m.ByteRange.StartByte))
        {
            lines.Add($"{VisibilitySymbol(orphan.IsExported)} {BuildMethodSignature(orphan)}    #symbol={orphan.Name}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string BuildFunctionSignature(GoFunctionInfo function)
    {
        var parameters = NormalizeParameters(function.Parameters);
        var returnType = string.IsNullOrWhiteSpace(function.ReturnType) ? string.Empty : $" {function.ReturnType.Trim()}";
        return $"func {function.Name}{parameters}{returnType}";
    }

    private static string BuildMethodSignature(GoMethodInfo method)
    {
        var receiverPrefix = method.IsPointerReceiver ? "*" : string.Empty;
        var receiverType = string.IsNullOrWhiteSpace(method.ReceiverType) ? "(unknown)" : method.ReceiverType.Trim();
        var parameters = NormalizeParameters(method.Parameters);
        var returnType = string.IsNullOrWhiteSpace(method.ReturnType) ? string.Empty : $" {method.ReturnType.Trim()}";
        return $"func ({receiverPrefix}{receiverType}) {method.Name}{parameters}{returnType}";
    }

    private static string BuildInterfaceMethodSignature(GoInterfaceMethodInfo method)
    {
        var parameters = NormalizeParameters(method.Parameters);
        var returnType = string.IsNullOrWhiteSpace(method.ReturnType) ? string.Empty : $" {method.ReturnType.Trim()}";
        return $"func {method.Name}{parameters}{returnType}";
    }

    private static string BuildConstantSignature(GoConstantInfo constant)
    {
        var typePart = string.IsNullOrWhiteSpace(constant.TypeName) ? string.Empty : $" {constant.TypeName.Trim()}";
        var valuePart = string.IsNullOrWhiteSpace(constant.Value) ? string.Empty : $" = {constant.Value.Trim()}";
        return $"const {constant.Name}{typePart}{valuePart}";
    }

    private static string BuildVariableSignature(GoVariableInfo variable)
    {
        var typePart = string.IsNullOrWhiteSpace(variable.TypeName) ? string.Empty : $" {variable.TypeName.Trim()}";
        var valuePart = string.IsNullOrWhiteSpace(variable.Value) ? string.Empty : $" = {variable.Value.Trim()}";
        return $"var {variable.Name}{typePart}{valuePart}";
    }

    private static string NormalizeParameters(string? parameters)
    {
        var value = parameters?.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return "()";
        }

        if (value.StartsWith('(') && value.EndsWith(')'))
        {
            return value;
        }

        return $"({value})";
    }

    private static string ToAccessibility(bool isExported) => isExported ? GoValues.Public : GoValues.Private;

    private static bool IsExportedName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        return trimmed.Length > 0 && char.IsUpper(trimmed[0]);
    }

    private static char VisibilitySymbol(bool isExported) => isExported ? '+' : '-';

    private static string BuildQualifiedName(string? prefix, string name)
    {
        if (string.IsNullOrWhiteSpace(prefix))
        {
            return name;
        }

        return $"{prefix}.{name}";
    }

    private static string ReadEmbeddedResource(string resourceName)
    {
        using var stream = typeof(GoLoader).Assembly.GetManifestResourceStream(resourceName)
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
                var localPath = uri.LocalPath;
                if (!string.IsNullOrEmpty(localPath))
                {
                    return Path.GetFileName(localPath);
                }
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
        string Kind,
        int StartByte,
        GoStructInfo? Struct = null,
        GoInterfaceInfo? Interface = null,
        GoFunctionInfo? Function = null);
}
