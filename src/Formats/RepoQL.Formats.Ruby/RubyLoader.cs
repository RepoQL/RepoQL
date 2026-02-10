using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RepoQL.Contracts;
using RepoQL.Contracts.Models;
using RepoQL.Formats.Ruby.Surface;
using RepoQL.Formats.Ruby.TreeSitter;

namespace RepoQL.Formats.Ruby;

/// <summary>
/// Ruby format loader and materializer.
///
/// Purpose: Parse Ruby files into a stable surface model and emit graph records.
///
/// Complexity: Handles classification compatibility, structural materialization,
/// and X-ray summary generation for the Ruby pipeline.
/// </summary>
public sealed class RubyLoader : IFormatLoader, IFormatMaterializer, IFormatSchemaProvider, IDisposable
{
    private readonly RubyTreeSitterClient _client;
    private readonly ILogger<RubyLoader> _logger;

    private static readonly Lazy<string> RubyViewsSql = new(
        () => ReadEmbeddedResource("RepoQL.Formats.Ruby.Schema.ruby_views.sql"));

    private const string StateMetadataKey = "ruby.state";
    private const string AnnotationSource = "repoql.formats.ruby";

    public RubyLoader(ILogger<RubyLoader>? logger = null)
    {
        _logger = logger ?? NullLogger<RubyLoader>.Instance;
        _client = new RubyTreeSitterClient();
    }

    public bool Supports(SemanticMediaType mediaType)
    {
        ArgumentNullException.ThrowIfNull(mediaType);

        return RubyMediaTypes.IsSupportedKind(mediaType.Kind)
               || (string.Equals(mediaType.Type, "text", StringComparison.OrdinalIgnoreCase)
                   && string.Equals(mediaType.Subtype, "x-ruby", StringComparison.OrdinalIgnoreCase));
    }

    public Task<bool> CanLoadAsync(DiscoveredArtifact artifact, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);

        var extension = Path.GetExtension(artifact.File.Name);
        if (RubyMediaTypes.IsErb(extension))
        {
            return Task.FromResult(false);
        }

        if (RubyMediaTypes.TryResolve(artifact.File.Name, out var mediaType))
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
            throw new InvalidOperationException("RepoUri required to load Ruby files.");

        var loaded = await FileContentReader.ReadAllTextWithDigestAsync(
            artifact.File,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var text = loaded.Text;
        var surface = _client.Parse(text);
        var mediaType = artifact.MediaType ?? RubyMediaTypes.Ruby;

        var metadata = new Dictionary<string, object?>
        {
            [StateMetadataKey] = new RubyDocumentState(
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
        var state = document.GetMetadataOrDefault<RubyDocumentState>(StateMetadataKey)
            ?? throw new InvalidOperationException("Ruby document missing state metadata.");

        var patterns = ExtractMaterializationPatterns(document.Text, state.Surface.MetaprogrammingHints);
        var tokenCount = TokenEstimator.EstimateTokensSafe(document.Text);
        string? headline = null;
        string? structure = null;
        try
        {
            headline = BuildHeadline(document, state.Surface, tokenCount);
            structure = BuildStructure(state.Surface, patterns);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to build Ruby X-ray summaries");
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

        var documentId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var docNode = new Node
        {
            Id = documentId,
            Kind = RubyNodeKinds.Document,
            Uri = document.Uri,
            ArtifactId = artifact.Id,
            Props = new JsonObject
            {
                [RubyPropertyKeys.Language] = RubyValues.LanguageName,
                [RubyPropertyKeys.LineCount] = document.LineMap.LineCount,
                [RubyPropertyKeys.ByteSize] = artifact.Size
            },
            CreatedAt = now,
            UpdatedAt = now
        };

        var nodes = new List<Node> { docNode };
        var spans = new List<Span>();
        var edges = new List<Edge>();
        var annotations = new List<Annotation>();
        var typeScopes = new List<TypeScope>();
        var membersByOwner = new Dictionary<Guid, Dictionary<string, Node>>(capacity: 16);

        var rootEntries = new List<RootEntry>();
        var reopeningClassStarts = DetectWithinFileReopenings(state.Surface.Classes);
        foreach (var klass in state.Surface.Classes)
            rootEntries.Add(new RootEntry("class", klass.ByteRange.StartByte, Class: klass));
        foreach (var mod in state.Surface.Modules)
            rootEntries.Add(new RootEntry("module", mod.ByteRange.StartByte, Module: mod));
        foreach (var func in state.Surface.Functions)
            rootEntries.Add(new RootEntry("function", func.ByteRange.StartByte, Function: func));

        var ordinal = 0;
        foreach (var entry in rootEntries.OrderBy(e => e.StartByte))
        {
            if (entry.Class is not null)
            {
                var isReopening = reopeningClassStarts.Contains(entry.Class.ByteRange.StartByte)
                                  && !entry.Class.HasSuperclassDeclaration;
                MaterializeClass(
                    entry.Class,
                    isReopening,
                    patterns,
                    document,
                    artifact.Id,
                    documentId,
                    now,
                    ordinal++,
                    nodes,
                    spans,
                    edges,
                    annotations,
                    typeScopes,
                    membersByOwner);
                continue;
            }

            if (entry.Module is not null)
            {
                MaterializeModule(
                    entry.Module,
                    patterns,
                    document,
                    artifact.Id,
                    documentId,
                    now,
                    ordinal++,
                    nodes,
                    spans,
                    edges,
                    annotations,
                    typeScopes,
                    membersByOwner);
                continue;
            }

            if (entry.Function is not null)
            {
                var functionNode = CreateFunctionNode(entry.Function, document, artifact.Id, documentId, now, out var functionSpan);
                nodes.Add(functionNode);
                spans.Add(functionSpan);
                edges.Add(CreateComposition(documentId, functionNode.Id, ordinal++, documentId, now));
            }
        }

        foreach (var require in state.Surface.Requires.OrderBy(r => r.ByteRange.StartByte))
        {
            edges.Add(CreateRequireEdge(documentId, documentId, now, require));
        }

        foreach (var alias in state.Surface.Aliases.OrderBy(a => a.ByteRange.StartByte))
        {
            var ownerScope = ResolveAliasOwner(alias.ByteRange, typeScopes);
            var ownerNodeId = ownerScope?.NodeId ?? documentId;
            var ownerMembers = membersByOwner.TryGetValue(ownerNodeId, out var members)
                ? members
                : null;

            var sourceNodeId = ownerMembers?.GetValueOrDefault(alias.NewName)?.Id ?? ownerNodeId;
            var destinationNodeId = ownerMembers?.GetValueOrDefault(alias.OriginalName)?.Id;

            edges.Add(CreateAliasEdge(sourceNodeId, destinationNodeId, documentId, now, alias.AliasType));
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
        yield return new FormatSqlScript("ruby_views", RubyViewsSql.Value);
    }

    public void Dispose()
    {
        _client.Dispose();
    }

    private static void MaterializeClass(
        RubyClassInfo classInfo,
        bool isReopening,
        MaterializationPatterns patterns,
        DocumentModel document,
        Guid artifactId,
        Guid documentId,
        DateTimeOffset now,
        int ordinal,
        List<Node> nodes,
        List<Span> spans,
        List<Edge> edges,
        List<Annotation> annotations,
        List<TypeScope> typeScopes,
        Dictionary<Guid, Dictionary<string, Node>> membersByOwner)
    {
        var classNode = CreateTypeNode(
            classInfo.Name,
            classInfo.QualifiedName,
            "class",
            classInfo.Superclass,
            isReopening,
            classInfo.ByteRange,
            document,
            artifactId,
            documentId,
            now,
            out var classSpan);

        nodes.Add(classNode);
        spans.Add(classSpan);
        edges.Add(CreateComposition(documentId, classNode.Id, ordinal, documentId, now));
        typeScopes.Add(new TypeScope(classNode.Id, classInfo.ByteRange.StartByte, classInfo.ByteRange.EndByte));
        membersByOwner[classNode.Id] = new Dictionary<string, Node>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(classInfo.Superclass))
        {
            edges.Add(CreateReferenceEdge(classNode.Id, RubyEdgeTypes.Extends, documentId, now, classInfo.Superclass));
        }

        foreach (var mixin in classInfo.Mixins.OrderBy(m => m.Ordinal))
        {
            var mechanism = NormalizeMixinMechanism(mixin.Mechanism);
            if (mechanism is null)
                continue;

            if (mechanism == RubyEdgeTypes.ExtendsModule && IsSelfMixinTarget(mixin.ModuleName))
            {
                edges.Add(CreateReferenceEdge(classNode.Id, RubyEdgeTypes.ExtendsModule, documentId, now, target: null, ordinal: mixin.Ordinal));
                continue;
            }

            edges.Add(CreateReferenceEdge(classNode.Id, mechanism, documentId, now, target: mixin.ModuleName, ordinal: mixin.Ordinal));
        }

        var memberOrdinal = 0;
        var members = new List<MemberEntry>();
        members.AddRange(classInfo.Methods.Select(m => new MemberEntry(m.ByteRange.StartByte, Method: m)));
        members.AddRange(classInfo.SingletonMethods.Select(m => new MemberEntry(m.ByteRange.StartByte, SingletonMethod: m)));
        members.AddRange(classInfo.Constants.Select(c => new MemberEntry(c.ByteRange.StartByte, Constant: c)));

        foreach (var member in members.OrderBy(m => m.StartByte))
        {
            if (member.Method is not null)
            {
                var memberNode = CreateMemberNode(
                    classInfo.QualifiedName,
                    member.Method.Name,
                    member.Method.Visibility,
                    member.Method.IsStatic,
                    member.Method.ParameterText,
                    member.Method.AcceptsBlock,
                    receiver: null,
                    kind: "method",
                    member.Method.ByteRange,
                    document,
                    artifactId,
                    documentId,
                    now,
                    out var memberSpan);

                nodes.Add(memberNode);
                spans.Add(memberSpan);
                edges.Add(CreateComposition(classNode.Id, memberNode.Id, memberOrdinal++, documentId, now));
                membersByOwner[classNode.Id][member.Method.Name] = memberNode;
                continue;
            }

            if (member.SingletonMethod is not null)
            {
                var singletonKind = string.Equals(member.SingletonMethod.Receiver, "self", StringComparison.Ordinal)
                    ? "method"
                    : "singleton_method";
                var visibility = RubyValues.Public;
                var memberNode = CreateMemberNode(
                    classInfo.QualifiedName,
                    member.SingletonMethod.Name,
                    visibility,
                    isStatic: true,
                    parameterText: member.SingletonMethod.ParameterText,
                    acceptsBlock: false,
                    receiver: singletonKind == "singleton_method" ? member.SingletonMethod.Receiver : null,
                    kind: singletonKind,
                    member.SingletonMethod.ByteRange,
                    document,
                    artifactId,
                    documentId,
                    now,
                    out var memberSpan);

                nodes.Add(memberNode);
                spans.Add(memberSpan);
                edges.Add(CreateComposition(classNode.Id, memberNode.Id, memberOrdinal++, documentId, now));
                membersByOwner[classNode.Id][member.SingletonMethod.Name] = memberNode;
                continue;
            }

            if (member.Constant is not null)
            {
                var constantNode = CreateConstantNode(
                    classInfo.QualifiedName,
                    member.Constant,
                    document,
                    artifactId,
                    documentId,
                    now,
                    out var constantSpan);

                nodes.Add(constantNode);
                spans.Add(constantSpan);
                edges.Add(CreateComposition(classNode.Id, constantNode.Id, memberOrdinal++, documentId, now));
            }
        }

        foreach (var attribute in classInfo.Attributes.OrderBy(a => a.ByteRange.StartByte))
        {
            var propertyNode = CreatePropertyNode(
                classInfo.QualifiedName,
                attribute,
                document,
                artifactId,
                documentId,
                now,
                out var propertySpan);
            nodes.Add(propertyNode);
            spans.Add(propertySpan);
            edges.Add(CreateComposition(classNode.Id, propertyNode.Id, memberOrdinal++, documentId, now));

            foreach (var generatedMethod in CreateGeneratedAttributeMethods(attribute))
            {
                var generatedNode = CreateMemberNode(
                    classInfo.QualifiedName,
                    generatedMethod.Name,
                    attribute.Visibility,
                    isStatic: false,
                    parameterText: generatedMethod.ParameterText,
                    acceptsBlock: false,
                    receiver: null,
                    kind: "method",
                    generatedMethod.ByteRange,
                    document,
                    artifactId,
                    documentId,
                    now,
                    out var generatedSpan,
                    isGenerated: true,
                    generator: generatedMethod.Generator);

                nodes.Add(generatedNode);
                spans.Add(generatedSpan);
                edges.Add(CreateComposition(classNode.Id, generatedNode.Id, memberOrdinal++, documentId, now));
                membersByOwner[classNode.Id][generatedMethod.Name] = generatedNode;
            }
        }

        var generatedPatterns = patterns.GeneratedMembers
            .Where(g => ContainsRange(classInfo.ByteRange, g.ByteRange))
            .OrderBy(g => g.ByteRange.StartByte);
        foreach (var generated in generatedPatterns)
        {
            var generatedNode = CreateMemberNode(
                classInfo.QualifiedName,
                generated.Name,
                RubyValues.Public,
                generated.IsStatic,
                parameterText: null,
                acceptsBlock: false,
                receiver: null,
                kind: "method",
                generated.ByteRange,
                document,
                artifactId,
                documentId,
                now,
                out var generatedSpan,
                isGenerated: true,
                generator: generated.Generator,
                delegateTo: generated.DelegateTo);

            nodes.Add(generatedNode);
            spans.Add(generatedSpan);
            edges.Add(CreateComposition(classNode.Id, generatedNode.Id, memberOrdinal++, documentId, now));
            membersByOwner[classNode.Id][generated.Name] = generatedNode;
        }

        foreach (var association in patterns.Associations.Where(a => ContainsRange(classInfo.ByteRange, a.ByteRange)))
        {
            edges.Add(CreateReferenceEdge(
                classNode.Id,
                RubyEdgeTypes.Associates,
                documentId,
                now,
                target: association.Target,
                association: association.AssociationType));
        }

        foreach (var validation in patterns.Validations.Where(v => ContainsRange(classInfo.ByteRange, v.ByteRange)))
        {
            var validationSpan = CreateSpan(validation.ByteRange, document, documentId);
            spans.Add(validationSpan);
            annotations.Add(CreateAnnotation(
                kind: "ruby.validation",
                ruleId: validation.FieldName,
                message: validation.Message,
                documentId,
                targetSpanId: validationSpan.Id,
                options: validation.Options,
                now));
        }

        foreach (var callback in patterns.Callbacks.Where(c => ContainsRange(classInfo.ByteRange, c.ByteRange)))
        {
            var callbackSpan = CreateSpan(callback.ByteRange, document, documentId);
            spans.Add(callbackSpan);
            annotations.Add(CreateAnnotation(
                kind: "ruby.callback",
                ruleId: callback.CallbackType,
                message: callback.MethodName,
                documentId,
                targetSpanId: callbackSpan.Id,
                options: callback.Options,
                now));
        }

        foreach (var hint in patterns.MetaprogrammingHints.Where(h => ContainsRange(classInfo.ByteRange, h.ByteRange)))
        {
            if (TryBuildMetaprogrammingMessage(hint, out var message))
            {
                var hintSpan = CreateSpan(hint.ByteRange, document, documentId);
                spans.Add(hintSpan);
                annotations.Add(CreateAnnotation(
                    kind: "ruby.metaprogramming",
                    ruleId: hint.PatternName,
                    message: message,
                    documentId,
                    targetSpanId: hintSpan.Id,
                    options: null,
                    now));
            }
        }
    }

    private static void MaterializeModule(
        RubyModuleInfo moduleInfo,
        MaterializationPatterns patterns,
        DocumentModel document,
        Guid artifactId,
        Guid documentId,
        DateTimeOffset now,
        int ordinal,
        List<Node> nodes,
        List<Span> spans,
        List<Edge> edges,
        List<Annotation> annotations,
        List<TypeScope> typeScopes,
        Dictionary<Guid, Dictionary<string, Node>> membersByOwner)
    {
        var moduleNode = CreateTypeNode(
            moduleInfo.Name,
            moduleInfo.QualifiedName,
            "module",
            extends: null,
            isReopening: false,
            moduleInfo.ByteRange,
            document,
            artifactId,
            documentId,
            now,
            out var moduleSpan);

        nodes.Add(moduleNode);
        spans.Add(moduleSpan);
        edges.Add(CreateComposition(documentId, moduleNode.Id, ordinal, documentId, now));
        typeScopes.Add(new TypeScope(moduleNode.Id, moduleInfo.ByteRange.StartByte, moduleInfo.ByteRange.EndByte));
        membersByOwner[moduleNode.Id] = new Dictionary<string, Node>(StringComparer.Ordinal);

        foreach (var mixin in moduleInfo.Mixins.OrderBy(m => m.Ordinal))
        {
            var mechanism = NormalizeMixinMechanism(mixin.Mechanism);
            if (mechanism is null)
                continue;

            if (mechanism == RubyEdgeTypes.ExtendsModule && IsSelfMixinTarget(mixin.ModuleName))
            {
                edges.Add(CreateReferenceEdge(moduleNode.Id, RubyEdgeTypes.ExtendsModule, documentId, now, target: null, ordinal: mixin.Ordinal));
                continue;
            }

            edges.Add(CreateReferenceEdge(moduleNode.Id, mechanism, documentId, now, target: mixin.ModuleName, ordinal: mixin.Ordinal));
        }

        var memberOrdinal = 0;
        var members = new List<MemberEntry>();
        members.AddRange(moduleInfo.Methods.Select(m => new MemberEntry(m.ByteRange.StartByte, Method: m)));
        members.AddRange(moduleInfo.Constants.Select(c => new MemberEntry(c.ByteRange.StartByte, Constant: c)));
        foreach (var member in members.OrderBy(m => m.StartByte))
        {
            if (member.Method is not null)
            {
                var memberNode = CreateMemberNode(
                    moduleInfo.QualifiedName,
                    member.Method.Name,
                    member.Method.Visibility,
                    member.Method.IsStatic,
                    member.Method.ParameterText,
                    member.Method.AcceptsBlock,
                    receiver: null,
                    kind: "method",
                    member.Method.ByteRange,
                    document,
                    artifactId,
                    documentId,
                    now,
                    out var memberSpan);

                nodes.Add(memberNode);
                spans.Add(memberSpan);
                edges.Add(CreateComposition(moduleNode.Id, memberNode.Id, memberOrdinal++, documentId, now));
                membersByOwner[moduleNode.Id][member.Method.Name] = memberNode;
                continue;
            }

            if (member.Constant is not null)
            {
                var constantNode = CreateConstantNode(
                    moduleInfo.QualifiedName,
                    member.Constant,
                    document,
                    artifactId,
                    documentId,
                    now,
                    out var constantSpan);

                nodes.Add(constantNode);
                spans.Add(constantSpan);
                edges.Add(CreateComposition(moduleNode.Id, constantNode.Id, memberOrdinal++, documentId, now));
            }
        }

        var generatedPatterns = patterns.GeneratedMembers
            .Where(g => ContainsRange(moduleInfo.ByteRange, g.ByteRange))
            .OrderBy(g => g.ByteRange.StartByte);
        foreach (var generated in generatedPatterns)
        {
            var generatedNode = CreateMemberNode(
                moduleInfo.QualifiedName,
                generated.Name,
                RubyValues.Public,
                generated.IsStatic,
                parameterText: null,
                acceptsBlock: false,
                receiver: null,
                kind: "method",
                generated.ByteRange,
                document,
                artifactId,
                documentId,
                now,
                out var generatedSpan,
                isGenerated: true,
                generator: generated.Generator,
                delegateTo: generated.DelegateTo);

            nodes.Add(generatedNode);
            spans.Add(generatedSpan);
            edges.Add(CreateComposition(moduleNode.Id, generatedNode.Id, memberOrdinal++, documentId, now));
            membersByOwner[moduleNode.Id][generated.Name] = generatedNode;
        }

        foreach (var validation in patterns.Validations.Where(v => ContainsRange(moduleInfo.ByteRange, v.ByteRange)))
        {
            var validationSpan = CreateSpan(validation.ByteRange, document, documentId);
            spans.Add(validationSpan);
            annotations.Add(CreateAnnotation(
                kind: "ruby.validation",
                ruleId: validation.FieldName,
                message: validation.Message,
                documentId,
                targetSpanId: validationSpan.Id,
                options: validation.Options,
                now));
        }

        foreach (var callback in patterns.Callbacks.Where(c => ContainsRange(moduleInfo.ByteRange, c.ByteRange)))
        {
            var callbackSpan = CreateSpan(callback.ByteRange, document, documentId);
            spans.Add(callbackSpan);
            annotations.Add(CreateAnnotation(
                kind: "ruby.callback",
                ruleId: callback.CallbackType,
                message: callback.MethodName,
                documentId,
                targetSpanId: callbackSpan.Id,
                options: callback.Options,
                now));
        }

        foreach (var hint in patterns.MetaprogrammingHints.Where(h => ContainsRange(moduleInfo.ByteRange, h.ByteRange)))
        {
            if (TryBuildMetaprogrammingMessage(hint, out var message))
            {
                var hintSpan = CreateSpan(hint.ByteRange, document, documentId);
                spans.Add(hintSpan);
                annotations.Add(CreateAnnotation(
                    kind: "ruby.metaprogramming",
                    ruleId: hint.PatternName,
                    message: message,
                    documentId,
                    targetSpanId: hintSpan.Id,
                    options: null,
                    now));
            }
        }
    }

    private static Node CreateTypeNode(
        string name,
        string qualifiedName,
        string kind,
        string? extends,
        bool isReopening,
        RubyByteRange byteRange,
        DocumentModel document,
        Guid artifactId,
        Guid documentId,
        DateTimeOffset now,
        out Span span)
    {
        span = CreateSpan(byteRange, document, documentId);

        var props = new JsonObject
        {
            [RubyPropertyKeys.Name] = name,
            [RubyPropertyKeys.QualifiedName] = qualifiedName,
            [RubyPropertyKeys.Kind] = kind,
            [RubyPropertyKeys.Accessibility] = RubyValues.Public,
            [RubyPropertyKeys.IsReopening] = isReopening ? "true" : "false"
        };

        var @namespace = TryGetNamespace(qualifiedName);
        if (!string.IsNullOrWhiteSpace(@namespace))
            props[RubyPropertyKeys.Namespace] = @namespace;
        if (!string.IsNullOrWhiteSpace(extends))
            props[RubyPropertyKeys.Extends] = extends;

        return new Node
        {
            Id = Guid.NewGuid(),
            Kind = RubyNodeKinds.Type,
            SpanId = span.Id,
            Uri = RepoUri.FromSymbol(document.Uri.Container, qualifiedName, span.StartLine, span.EndLine),
            ArtifactId = artifactId,
            Props = props,
            Headline = kind == "class" && !string.IsNullOrWhiteSpace(extends)
                ? $"class {qualifiedName} < {extends}"
                : $"{kind} {qualifiedName}",
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static Node CreateMemberNode(
        string declaringType,
        string name,
        string? accessibility,
        bool isStatic,
        string? parameterText,
        bool acceptsBlock,
        string? receiver,
        string kind,
        RubyByteRange byteRange,
        DocumentModel document,
        Guid artifactId,
        Guid documentId,
        DateTimeOffset now,
        out Span span,
        bool isGenerated = false,
        string? generator = null,
        string? delegateTo = null)
    {
        span = CreateSpan(byteRange, document, documentId);

        var visibility = NormalizeVisibility(accessibility);
        var qualifiedName = BuildMemberQualifiedName(declaringType, name, isStatic);
        var props = new JsonObject
        {
            [RubyPropertyKeys.Name] = name,
            [RubyPropertyKeys.QualifiedName] = qualifiedName,
            [RubyPropertyKeys.Kind] = kind,
            [RubyPropertyKeys.DeclaringType] = declaringType,
            [RubyPropertyKeys.Accessibility] = visibility,
            [RubyPropertyKeys.IsStatic] = isStatic,
            [RubyPropertyKeys.Parameters] = parameterText,
            [RubyPropertyKeys.AcceptsBlock] = acceptsBlock,
            [RubyPropertyKeys.ReturnType] = null,
            [RubyPropertyKeys.IsGenerated] = isGenerated ? "true" : "false"
        };

        if (!string.IsNullOrWhiteSpace(receiver))
            props[RubyPropertyKeys.Receiver] = receiver;
        if (!string.IsNullOrWhiteSpace(generator))
            props[RubyPropertyKeys.Generator] = generator;
        if (!string.IsNullOrWhiteSpace(delegateTo))
            props[RubyPropertyKeys.DelegateTo] = delegateTo;

        var parameterDisplay = NormalizeParameterDisplay(parameterText, acceptsBlock);
        var headline = $"{visibility} {name}({parameterDisplay})";
        var structure = $"{VisibilitySymbol(visibility)}{name}({parameterDisplay})";
        if (isGenerated)
        {
            var source = string.IsNullOrWhiteSpace(generator) ? "generated" : generator;
            headline = $"{visibility} {name}({parameterDisplay}) [{source}]";
            structure = $"~{name} ({source})";
        }

        return new Node
        {
            Id = Guid.NewGuid(),
            Kind = RubyNodeKinds.Member,
            SpanId = span.Id,
            Uri = RepoUri.FromSymbol(document.Uri.Container, qualifiedName, span.StartLine, span.EndLine),
            ArtifactId = artifactId,
            Props = props,
            Headline = headline,
            Structure = structure,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static Node CreateFunctionNode(
        RubyMethodInfo function,
        DocumentModel document,
        Guid artifactId,
        Guid documentId,
        DateTimeOffset now,
        out Span span)
    {
        span = CreateSpan(function.ByteRange, document, documentId);

        var props = new JsonObject
        {
            [RubyPropertyKeys.Name] = function.Name,
            [RubyPropertyKeys.Kind] = "function",
            [RubyPropertyKeys.Parameters] = function.ParameterText,
            [RubyPropertyKeys.AcceptsBlock] = function.AcceptsBlock,
            [RubyPropertyKeys.ReturnType] = null
        };

        return new Node
        {
            Id = Guid.NewGuid(),
            Kind = RubyNodeKinds.Function,
            SpanId = span.Id,
            Uri = RepoUri.FromSymbol(document.Uri.Container, function.Name, span.StartLine, span.EndLine),
            ArtifactId = artifactId,
            Props = props,
            Headline = $"function {function.Name}({NormalizeParameterDisplay(function.ParameterText, function.AcceptsBlock)})",
            Structure = $"function {function.Name}({NormalizeParameterDisplay(function.ParameterText, function.AcceptsBlock)})",
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static Node CreateConstantNode(
        string enclosingQualifiedName,
        RubyConstantInfo constant,
        DocumentModel document,
        Guid artifactId,
        Guid documentId,
        DateTimeOffset now,
        out Span span)
    {
        span = CreateSpan(constant.ByteRange, document, documentId);
        var qualifiedName = $"{enclosingQualifiedName}::{constant.Name}";
        return new Node
        {
            Id = Guid.NewGuid(),
            Kind = RubyNodeKinds.Constant,
            SpanId = span.Id,
            Uri = RepoUri.FromSymbol(document.Uri.Container, qualifiedName, span.StartLine, span.EndLine),
            ArtifactId = artifactId,
            Props = new JsonObject
            {
                [RubyPropertyKeys.Name] = constant.Name,
                [RubyPropertyKeys.QualifiedName] = qualifiedName
            },
            Headline = $"constant {qualifiedName}",
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static Node CreatePropertyNode(
        string declaringType,
        RubyAttributeInfo attribute,
        DocumentModel document,
        Guid artifactId,
        Guid documentId,
        DateTimeOffset now,
        out Span span)
    {
        span = CreateSpan(attribute.ByteRange, document, documentId);
        var qualifiedName = $"{declaringType}.@{attribute.Name}";
        return new Node
        {
            Id = Guid.NewGuid(),
            Kind = RubyNodeKinds.Property,
            SpanId = span.Id,
            Uri = RepoUri.FromSymbol(document.Uri.Container, qualifiedName, span.StartLine, span.EndLine),
            ArtifactId = artifactId,
            Props = new JsonObject
            {
                [RubyPropertyKeys.Name] = attribute.Name,
                [RubyPropertyKeys.AccessorType] = attribute.AccessorType
            },
            Headline = $"property {attribute.Name} ({attribute.AccessorType})",
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
            Type = RubyEdgeTypes.HasPart,
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
        string? target = null,
        string? association = null,
        int? ordinal = null)
    {
        JsonObject? props = null;
        if (!string.IsNullOrWhiteSpace(target) || !string.IsNullOrWhiteSpace(association) || ordinal.HasValue)
        {
            props = new JsonObject();
            if (!string.IsNullOrWhiteSpace(target))
                props[RubyPropertyKeys.Target] = target;
            if (!string.IsNullOrWhiteSpace(association))
                props[RubyPropertyKeys.Association] = association;
            if (ordinal.HasValue)
                props[RubyPropertyKeys.Ordinal] = ordinal.Value;
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

    private static Edge CreateRequireEdge(Guid srcId, Guid scopeDocId, DateTimeOffset now, RubyRequireInfo require)
    {
        return new Edge
        {
            Id = Guid.NewGuid(),
            SrcId = srcId,
            DstId = null,
            Type = RubyEdgeTypes.Requires,
            IsComposition = false,
            ScopeDocumentId = scopeDocId,
            Props = new JsonObject
            {
                [RubyPropertyKeys.Path] = require.Path,
                [RubyPropertyKeys.IsRelative] = require.IsRelative ? "true" : "false"
            },
            CreatedAt = now
        };
    }

    private static Edge CreateAliasEdge(
        Guid srcId,
        Guid? dstId,
        Guid scopeDocId,
        DateTimeOffset now,
        string aliasType)
    {
        return new Edge
        {
            Id = Guid.NewGuid(),
            SrcId = srcId,
            DstId = dstId,
            Type = RubyEdgeTypes.Aliases,
            IsComposition = false,
            ScopeDocumentId = scopeDocId,
            Props = new JsonObject
            {
                [RubyPropertyKeys.AliasType] = aliasType
            },
            CreatedAt = now
        };
    }

    private static TypeScope? ResolveAliasOwner(RubyByteRange range, IReadOnlyList<TypeScope> typeScopes)
    {
        TypeScope? best = null;
        foreach (var scope in typeScopes)
        {
            if (scope.StartByte > range.StartByte || scope.EndByte < range.EndByte)
                continue;

            if (best is null || scope.StartByte >= best.StartByte)
                best = scope;
        }

        return best;
    }

    private static HashSet<int> DetectWithinFileReopenings(IReadOnlyList<RubyClassInfo> classes)
    {
        var reopenings = new HashSet<int>();
        var classesByName = classes.GroupBy(c => c.QualifiedName, StringComparer.Ordinal);
        foreach (var group in classesByName)
        {
            var hasSuperclassDefinition = group.Any(c => c.HasSuperclassDeclaration);
            var hasNoSuperclassDefinition = group.Any(c => !c.HasSuperclassDeclaration);
            if (!hasSuperclassDefinition || !hasNoSuperclassDefinition)
                continue;

            foreach (var reopened in group.Where(c => !c.HasSuperclassDeclaration))
            {
                reopenings.Add(reopened.ByteRange.StartByte);
            }
        }

        return reopenings;
    }

    private static string? NormalizeMixinMechanism(string mechanism)
        => mechanism.Trim().ToLowerInvariant() switch
        {
            "include" => RubyEdgeTypes.Includes,
            "prepend" => RubyEdgeTypes.Prepends,
            "extend" => RubyEdgeTypes.ExtendsModule,
            _ => null
        };

    private static bool IsSelfMixinTarget(string moduleName)
        => string.Equals(moduleName.Trim(), "self", StringComparison.OrdinalIgnoreCase);

    private static Span CreateSpan(RubyByteRange range, DocumentModel document, Guid documentId)
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

    private MaterializationPatterns ExtractMaterializationPatterns(
        string sourceCode,
        IReadOnlyList<RubyMetaprogrammingHint> metaprogrammingHints)
    {
        var generatedMembers = new List<GeneratedMemberPattern>();
        var associations = new List<AssociationPattern>();
        var validations = new List<ValidationPattern>();
        var callbacks = new List<CallbackPattern>();

        foreach (var match in _client.ExecuteQuery(RubyQueries.DelegateCalls, sourceCode))
        {
            var args = match.Captures.FirstOrDefault(c => c.Name == "args");
            var call = match.Captures.FirstOrDefault(c => c.Name == "delegate_call");
            if (args is null || call is null)
                continue;

            var target = ExtractKeywordSymbol(args.Text, "to");
            if (string.IsNullOrWhiteSpace(target))
                continue;

            var methodSymbols = ExtractSymbols(SplitBeforeKeyword(args.Text, "to"));
            foreach (var methodName in methodSymbols)
            {
                generatedMembers.Add(new GeneratedMemberPattern(
                    methodName,
                    "delegate",
                    IsStatic: false,
                    DelegateTo: target,
                    call.ByteRange));
            }
        }

        foreach (var match in _client.ExecuteQuery(RubyQueries.ScopeCalls, sourceCode))
        {
            var args = match.Captures.FirstOrDefault(c => c.Name == "args");
            var call = match.Captures.FirstOrDefault(c => c.Name == "scope_call");
            if (args is null || call is null)
                continue;

            var scopeName = ExtractFirstSymbol(args.Text);
            if (string.IsNullOrWhiteSpace(scopeName))
                continue;

            generatedMembers.Add(new GeneratedMemberPattern(
                scopeName,
                "scope",
                IsStatic: true,
                DelegateTo: null,
                call.ByteRange));
        }

        foreach (var match in _client.ExecuteQuery(RubyQueries.AssociationCalls, sourceCode))
        {
            var method = match.Captures.FirstOrDefault(c => c.Name == "method");
            var args = match.Captures.FirstOrDefault(c => c.Name == "args");
            var call = match.Captures.FirstOrDefault(c => c.Name == "association_call");
            if (method is null || args is null || call is null)
                continue;

            var target = ExtractFirstSymbol(args.Text);
            if (string.IsNullOrWhiteSpace(target))
                continue;

            associations.Add(new AssociationPattern(method.Text.Trim(), target, call.ByteRange));
        }

        foreach (var match in _client.ExecuteQuery(RubyQueries.ValidationCalls, sourceCode))
        {
            var args = match.Captures.FirstOrDefault(c => c.Name == "args");
            var call = match.Captures.FirstOrDefault(c => c.Name == "validation_call");
            if (args is null || call is null)
                continue;

            var field = ExtractFirstSymbol(args.Text);
            if (string.IsNullOrWhiteSpace(field))
                continue;

            var options = ExtractOptionsText(args.Text);
            var message = string.IsNullOrWhiteSpace(options)
                ? $"validates :{field}"
                : $"validates :{field}, {options}";
            validations.Add(new ValidationPattern(field, message, options, call.ByteRange));
        }

        foreach (var match in _client.ExecuteQuery(RubyQueries.CallbackCalls, sourceCode))
        {
            var method = match.Captures.FirstOrDefault(c => c.Name == "method");
            var args = match.Captures.FirstOrDefault(c => c.Name == "args");
            var call = match.Captures.FirstOrDefault(c => c.Name == "callback_call");
            if (method is null || args is null || call is null)
                continue;

            var callbackMethod = ExtractFirstSymbol(args.Text);
            if (string.IsNullOrWhiteSpace(callbackMethod))
                continue;

            callbacks.Add(new CallbackPattern(
                method.Text.Trim(),
                callbackMethod,
                ExtractOptionsText(args.Text),
                call.ByteRange));
        }

        var defineMethodHints = metaprogrammingHints
            .Where(h => string.Equals(h.PatternName, "define_method", StringComparison.Ordinal))
            .ToDictionary(h => $"{h.ByteRange.StartByte}:{h.ByteRange.EndByte}", h => h, StringComparer.Ordinal);
        foreach (var match in _client.ExecuteQuery(RubyQueries.DefineMethodCalls, sourceCode))
        {
            var args = match.Captures.FirstOrDefault(c => c.Name == "args");
            var call = match.Captures.FirstOrDefault(c => c.Name == "define_method_call");
            if (args is null || call is null)
                continue;

            var key = $"{call.ByteRange.StartByte}:{call.ByteRange.EndByte}";
            if (!defineMethodHints.TryGetValue(key, out var hint) || !hint.Extractable)
                continue;

            var literalName = ExtractFirstLiteralMethodName(args.Text);
            if (string.IsNullOrWhiteSpace(literalName))
                continue;

            generatedMembers.Add(new GeneratedMemberPattern(
                literalName,
                "define_method",
                IsStatic: false,
                DelegateTo: null,
                call.ByteRange));
        }

        var pendingHonestyHints = metaprogrammingHints
            .Where(h => !string.Equals(h.PatternName, "define_method", StringComparison.Ordinal) || !h.Extractable)
            .ToList();

        return new MaterializationPatterns(
            generatedMembers,
            associations,
            validations,
            callbacks,
            pendingHonestyHints);
    }

    private static bool ContainsRange(RubyByteRange owner, RubyByteRange candidate)
        => owner.StartByte <= candidate.StartByte && owner.EndByte >= candidate.EndByte;

    private static IReadOnlyList<GeneratedAttributeMethod> CreateGeneratedAttributeMethods(RubyAttributeInfo attribute)
    {
        return attribute.AccessorType switch
        {
            "reader" => [new GeneratedAttributeMethod(attribute.Name, null, "attr_reader", attribute.ByteRange)],
            "writer" => [new GeneratedAttributeMethod($"{attribute.Name}=", "value", "attr_writer", attribute.ByteRange)],
            _ => [
                new GeneratedAttributeMethod(attribute.Name, null, "attr_accessor", attribute.ByteRange),
                new GeneratedAttributeMethod($"{attribute.Name}=", "value", "attr_accessor", attribute.ByteRange)
            ]
        };
    }

    private static Annotation CreateAnnotation(
        string kind,
        string? ruleId,
        string message,
        Guid documentId,
        Guid targetSpanId,
        string? options,
        DateTimeOffset createdAt)
    {
        var data = new JsonObject();
        if (!string.IsNullOrWhiteSpace(options))
            data[RubyPropertyKeys.Options] = options;

        return new Annotation
        {
            Id = Guid.NewGuid(),
            Kind = kind,
            Severity = "info",
            Source = AnnotationSource,
            RuleId = ruleId,
            Message = message,
            Data = data,
            ScopeDocumentId = documentId,
            TargetSpanId = targetSpanId,
            CreatedAt = createdAt
        };
    }

    private static bool TryBuildMetaprogrammingMessage(RubyMetaprogrammingHint hint, out string message)
    {
        message = hint.PatternName switch
        {
            "define_method" => "dynamic method definition detected, name not extractable",
            "class_eval" => "class_eval detected, definitions not extractable",
            "module_eval" => "module_eval detected, definitions not extractable",
            "instance_eval" => "instance_eval detected, definitions not extractable",
            "method_missing" => "method_missing defined, dynamic dispatch possible",
            _ => string.Empty
        };

        return !string.IsNullOrWhiteSpace(message);
    }

    private static string SplitBeforeKeyword(string argsText, string keyword)
    {
        var index = argsText.IndexOf($"{keyword}:", StringComparison.Ordinal);
        return index >= 0 ? argsText[..index] : argsText;
    }

    private static string? ExtractKeywordSymbol(string argsText, string keyword)
    {
        var match = Regex.Match(argsText, $@"\b{Regex.Escape(keyword)}\s*:\s*:(?<value>[A-Za-z_]\w*[!?=]?)");
        return match.Success ? match.Groups["value"].Value : null;
    }

    private static IReadOnlyList<string> ExtractSymbols(string argsText)
    {
        var matches = Regex.Matches(argsText, @":(?<name>[A-Za-z_]\w*[!?=]?)");
        return matches
            .Select(m => m.Groups["name"].Value)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static string? ExtractFirstSymbol(string argsText)
        => ExtractSymbols(argsText).FirstOrDefault();

    private static string? ExtractOptionsText(string argsText)
    {
        var comma = argsText.IndexOf(',', StringComparison.Ordinal);
        return comma >= 0
            ? argsText[(comma + 1)..].Trim()
            : null;
    }

    private static string? ExtractFirstLiteralMethodName(string argsText)
    {
        var trimmed = argsText.Trim();
        if (trimmed.StartsWith('(') && trimmed.EndsWith(')') && trimmed.Length >= 2)
        {
            trimmed = trimmed[1..^1].Trim();
        }

        if (trimmed.StartsWith(':'))
        {
            var symbol = Regex.Match(trimmed, @"^:(?<name>[A-Za-z_]\w*[!?=]?)");
            if (symbol.Success)
                return symbol.Groups["name"].Value;
        }

        var str = Regex.Match(trimmed, "^['\"](?<name>[^'\"]+)['\"]");
        return str.Success ? str.Groups["name"].Value : null;
    }

    private static string BuildHeadline(DocumentModel document, RubyDocumentSurface surface, int? tokenCount)
    {
        var fileName = GetFileName(document.Uri);
        var declaration = BuildPrimaryDeclaration(surface);
        var keyMembers = BuildKeyMembers(surface);
        var sizePart = $"{document.LineMap.LineCount} ln";
        if (tokenCount.HasValue)
            sizePart = $"{sizePart}, ~{tokenCount.Value} tok";

        return string.Join(" | ", new[] { fileName, declaration, keyMembers, sizePart }
            .Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    private static string BuildPrimaryDeclaration(RubyDocumentSurface surface)
    {
        if (surface.Classes.Count == 1 && surface.Modules.Count == 0 && surface.Functions.Count == 0)
        {
            var klass = surface.Classes[0];
            return string.IsNullOrWhiteSpace(klass.Superclass)
                ? $"class {klass.QualifiedName}"
                : $"class {klass.QualifiedName} < {klass.Superclass}";
        }

        if (surface.Modules.Count == 1 && surface.Classes.Count == 0 && surface.Functions.Count == 0)
        {
            return $"module {surface.Modules[0].QualifiedName}";
        }

        if (surface.Classes.Count > 1 && surface.Modules.Count == 0 && surface.Functions.Count == 0)
            return $"{surface.Classes.Count} classes";
        if (surface.Modules.Count > 1 && surface.Classes.Count == 0 && surface.Functions.Count == 0)
            return $"{surface.Modules.Count} modules";

        var total = surface.Classes.Count + surface.Modules.Count + surface.Functions.Count;
        return total == 0 ? "ruby file" : $"{total} declarations";
    }

    private static string? BuildKeyMembers(RubyDocumentSurface surface)
    {
        var names = new List<string>();
        names.AddRange(surface.Classes.SelectMany(c => c.Methods).Where(m => IsPublic(m.Visibility)).Select(m => m.Name));
        names.AddRange(surface.Modules.SelectMany(m => m.Methods).Where(m => IsPublic(m.Visibility)).Select(m => m.Name));
        names.AddRange(surface.Functions.Select(f => f.Name));

        var unique = names
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal)
            .Take(8)
            .ToList();
        return unique.Count == 0 ? null : string.Join(", ", unique);
    }

    private static string BuildStructure(RubyDocumentSurface surface, MaterializationPatterns patterns)
    {
        var lines = new List<string>();
        var entries = new List<RootEntry>();
        entries.AddRange(surface.Classes.Select(c => new RootEntry("class", c.ByteRange.StartByte, Class: c)));
        entries.AddRange(surface.Modules.Select(m => new RootEntry("module", m.ByteRange.StartByte, Module: m)));
        entries.AddRange(surface.Functions.Select(f => new RootEntry("function", f.ByteRange.StartByte, Function: f)));

        foreach (var entry in entries.OrderBy(e => e.StartByte))
        {
            if (entry.Class is not null)
            {
                var classLine = string.IsNullOrWhiteSpace(entry.Class.Superclass)
                    ? $"class {entry.Class.QualifiedName}"
                    : $"class {entry.Class.QualifiedName} < {entry.Class.Superclass}";
                lines.Add(classLine);

                var classItems = new List<StructureLine>();
                classItems.AddRange(entry.Class.Methods.Select(m =>
                    new StructureLine(
                        m.ByteRange.StartByte,
                        $"{VisibilitySymbol(m.Visibility)}{m.Name}({NormalizeParameterDisplay(m.ParameterText, m.AcceptsBlock)})    #symbol={m.Name}")));
                classItems.AddRange(entry.Class.SingletonMethods.Select(m =>
                    new StructureLine(
                        m.ByteRange.StartByte,
                        $"{VisibilitySymbol(RubyValues.Public)}{m.Name}({NormalizeParameterDisplay(m.ParameterText, false)})    #symbol={m.Name}")));
                classItems.AddRange(entry.Class.Attributes.Select(a =>
                    new StructureLine(
                        a.ByteRange.StartByte,
                        $"~{a.Name} (attr_{a.AccessorType})    #symbol={a.Name}")));
                classItems.AddRange(patterns.GeneratedMembers
                    .Where(g => ContainsRange(entry.Class.ByteRange, g.ByteRange))
                    .Select(g => new StructureLine(
                        g.ByteRange.StartByte,
                        $"~{g.Name} ({g.Generator})    #symbol={g.Name}")));
                classItems.AddRange(patterns.Associations
                    .Where(a => ContainsRange(entry.Class.ByteRange, a.ByteRange))
                    .Select(a => new StructureLine(
                        a.ByteRange.StartByte,
                        $"{a.AssociationType} :{a.Target}")));
                classItems.AddRange(patterns.Validations
                    .Where(v => ContainsRange(entry.Class.ByteRange, v.ByteRange))
                    .Select(v => new StructureLine(
                        v.ByteRange.StartByte,
                        $"validates :{v.FieldName}")));

                foreach (var item in classItems.OrderBy(m => m.StartByte))
                {
                    lines.Add($"  {item.Text}");
                }

                continue;
            }

            if (entry.Module is not null)
            {
                lines.Add($"module {entry.Module.QualifiedName}");
                var moduleItems = new List<StructureLine>();
                moduleItems.AddRange(entry.Module.Methods.Select(method =>
                    new StructureLine(
                        method.ByteRange.StartByte,
                        $"{VisibilitySymbol(method.Visibility)}{method.Name}({NormalizeParameterDisplay(method.ParameterText, method.AcceptsBlock)})    #symbol={method.Name}")));
                moduleItems.AddRange(patterns.GeneratedMembers
                    .Where(g => ContainsRange(entry.Module.ByteRange, g.ByteRange))
                    .Select(g => new StructureLine(
                        g.ByteRange.StartByte,
                        $"~{g.Name} ({g.Generator})    #symbol={g.Name}")));
                foreach (var item in moduleItems.OrderBy(i => i.StartByte))
                {
                    lines.Add($"  {item.Text}");
                }

                continue;
            }

            if (entry.Function is not null)
            {
                lines.Add($"function {entry.Function.Name}({NormalizeParameterDisplay(entry.Function.ParameterText, entry.Function.AcceptsBlock)})    #symbol={entry.Function.Name}");
            }
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static bool IsPublic(string? visibility)
        => string.Equals(NormalizeVisibility(visibility), RubyValues.Public, StringComparison.Ordinal);

    private static string NormalizeVisibility(string? visibility)
    {
        var normalized = visibility?.Trim().ToLowerInvariant();
        return normalized switch
        {
            RubyValues.Private => RubyValues.Private,
            RubyValues.Protected => RubyValues.Protected,
            _ => RubyValues.Public
        };
    }

    private static char VisibilitySymbol(string? visibility)
        => NormalizeVisibility(visibility) switch
        {
            RubyValues.Private => '-',
            RubyValues.Protected => '#',
            _ => '+'
        };

    private static string NormalizeParameterDisplay(string? parameterText, bool acceptsBlock)
    {
        var text = parameterText?.Trim();
        if (!string.IsNullOrWhiteSpace(text) && text.StartsWith('(') && text.EndsWith(')'))
        {
            text = text[1..^1].Trim();
        }

        if (acceptsBlock && (string.IsNullOrWhiteSpace(text) || !text.Contains("&block", StringComparison.Ordinal)))
        {
            text = string.IsNullOrWhiteSpace(text) ? "&block" : $"{text}, &block";
        }

        return text ?? string.Empty;
    }

    private static string BuildMemberQualifiedName(string declaringType, string memberName, bool isStatic)
        => isStatic ? $"{declaringType}.{memberName}" : $"{declaringType}#{memberName}";

    private static string? TryGetNamespace(string qualifiedName)
    {
        var index = qualifiedName.LastIndexOf("::", StringComparison.Ordinal);
        if (index <= 0)
            return null;
        return qualifiedName[..index];
    }

    private static string ReadEmbeddedResource(string resourceName)
    {
        using var stream = typeof(RubyLoader).Assembly.GetManifestResourceStream(resourceName)
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
        string Kind,
        int StartByte,
        RubyClassInfo? Class = null,
        RubyModuleInfo? Module = null,
        RubyMethodInfo? Function = null);

    private sealed record MemberEntry(
        int StartByte,
        RubyMethodInfo? Method = null,
        RubySingletonMethodInfo? SingletonMethod = null,
        RubyConstantInfo? Constant = null);

    private sealed record TypeScope(
        Guid NodeId,
        int StartByte,
        int EndByte);

    private sealed record StructureLine(
        int StartByte,
        string Text);

    private sealed record GeneratedAttributeMethod(
        string Name,
        string? ParameterText,
        string Generator,
        RubyByteRange ByteRange);

    private sealed record GeneratedMemberPattern(
        string Name,
        string Generator,
        bool IsStatic,
        string? DelegateTo,
        RubyByteRange ByteRange);

    private sealed record AssociationPattern(
        string AssociationType,
        string Target,
        RubyByteRange ByteRange);

    private sealed record ValidationPattern(
        string FieldName,
        string Message,
        string? Options,
        RubyByteRange ByteRange);

    private sealed record CallbackPattern(
        string CallbackType,
        string MethodName,
        string? Options,
        RubyByteRange ByteRange);

    private sealed record MaterializationPatterns(
        IReadOnlyList<GeneratedMemberPattern> GeneratedMembers,
        IReadOnlyList<AssociationPattern> Associations,
        IReadOnlyList<ValidationPattern> Validations,
        IReadOnlyList<CallbackPattern> Callbacks,
        IReadOnlyList<RubyMetaprogrammingHint> MetaprogrammingHints);
}
