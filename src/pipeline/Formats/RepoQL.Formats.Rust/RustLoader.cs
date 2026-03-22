using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RepoQL.Contracts;
using RepoQL.Contracts.Models;
using RepoQL.Formats.Rust.Surface;
using RepoQL.Formats.Rust.TreeSitter;

namespace RepoQL.Formats.Rust;

public sealed class RustLoader : IFormatLoader, IFormatMaterializer, IFormatSchemaProvider, IDisposable
{
    private readonly RustTreeSitterClient _client;
    private readonly ILogger<RustLoader> _logger;

    private static readonly Lazy<string> RustViewsSql = new(
        () => ReadEmbeddedResource("RepoQL.Formats.Rust.Schema.rust_views.sql"));

    private const string StateMetadataKey = "rust.state";

    public RustLoader(ILogger<RustLoader>? logger = null)
    {
        _logger = logger ?? NullLogger<RustLoader>.Instance;
        _client = new RustTreeSitterClient();
    }

    public bool Supports(SemanticMediaType mediaType)
    {
        ArgumentNullException.ThrowIfNull(mediaType);

        return RustMediaTypes.IsSupportedKind(mediaType.Kind)
               || (string.Equals(mediaType.Type, "text", StringComparison.OrdinalIgnoreCase)
                   && string.Equals(mediaType.Subtype, "x-rust", StringComparison.OrdinalIgnoreCase));
    }

    public Task<bool> CanLoadAsync(DiscoveredArtifact artifact, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);

        if (RustMediaTypes.TryResolve(artifact.File.Name, out var mediaType))
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
            throw new InvalidOperationException("RepoUri required to load Rust files.");

        var loaded = await FileContentReader.ReadAllTextWithDigestAsync(
            artifact.File,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var text = loaded.Text;
        var mediaType = artifact.MediaType ?? RustMediaTypes.Rust;
        var surface = RustMediaTypes.IsSupportedKind(mediaType.Kind)
            ? _client.Parse(text)
            : CreateEmptySurface(text);

        var metadata = new Dictionary<string, object?>
        {
            [StateMetadataKey] = new RustDocumentState(
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
        if (!RustMediaTypes.IsSupportedKind(document.MediaType.Kind))
        {
            return Records.Empty;
        }

        var state = document.GetMetadataOrDefault<RustDocumentState>(StateMetadataKey)
            ?? throw new InvalidOperationException("Rust document missing state metadata.");

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
            _logger.LogWarning(ex, "Failed to build Rust X-ray summaries");
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
        var docNode = new Node
        {
            Id = documentId,
            Kind = RustNodeKinds.Document,
            Uri = document.Uri,
            ArtifactId = artifact.Id,
            Props = new JsonObject
            {
                [RustPropertyKeys.Language] = RustValues.LanguageName,
                [RustPropertyKeys.LineCount] = document.LineMap.LineCount,
                [RustPropertyKeys.ByteSize] = artifact.Size
            },
            CreatedAt = now,
            UpdatedAt = now
        };

        var nodes = new List<Node> { docNode };
        var spans = new List<Span>();
        var edges = new List<Edge>();
        var annotations = new List<Annotation>();

        var documentChildren = new List<ChildEntry>();
        var documentChildIds = new HashSet<Guid>();
        var memberChildrenByOwner = new Dictionary<Guid, List<ChildEntry>>();
        var typeNodesByLookup = new Dictionary<string, Node>(StringComparer.Ordinal);
        var decoratedNodes = new List<DecoratedNodeEntry>();

        foreach (var structInfo in state.Surface.Structs.OrderBy(s => s.ByteRange.StartByte))
        {
            try
            {
                var typeNode = CreateStructNode(
                    structInfo,
                    document,
                    artifact.Id,
                    documentId,
                    now,
                    out var span);
                nodes.Add(typeNode);
                spans.Add(span);
                AddDocumentChild(documentChildren, documentChildIds, structInfo.ByteRange.StartByte, typeNode);
                decoratedNodes.Add(new DecoratedNodeEntry(structInfo.ByteRange.StartByte, structInfo.ByteRange.EndByte, typeNode));
                RegisterTypeLookup(typeNodesByLookup, typeNode, structInfo.Name, structInfo.QualifiedName);
                AddDeriveEdges(edges, typeNode.Id, structInfo.Derives, documentId, now);
                AddDeriveAnnotations(
                    annotations,
                    spans,
                    structInfo.Attributes,
                    document,
                    documentId,
                    now);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to materialize Rust struct {TypeName}", structInfo.Name);
            }
        }

        foreach (var enumInfo in state.Surface.Enums.OrderBy(e => e.ByteRange.StartByte))
        {
            try
            {
                var typeNode = CreateEnumNode(
                    enumInfo,
                    document,
                    artifact.Id,
                    documentId,
                    now,
                    out var span);
                nodes.Add(typeNode);
                spans.Add(span);
                AddDocumentChild(documentChildren, documentChildIds, enumInfo.ByteRange.StartByte, typeNode);
                decoratedNodes.Add(new DecoratedNodeEntry(enumInfo.ByteRange.StartByte, enumInfo.ByteRange.EndByte, typeNode));
                RegisterTypeLookup(typeNodesByLookup, typeNode, enumInfo.Name, enumInfo.QualifiedName);
                AddDeriveEdges(edges, typeNode.Id, enumInfo.Derives, documentId, now);
                AddDeriveAnnotations(
                    annotations,
                    spans,
                    enumInfo.Attributes,
                    document,
                    documentId,
                    now);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to materialize Rust enum {TypeName}", enumInfo.Name);
            }
        }

        foreach (var traitInfo in state.Surface.Traits.OrderBy(t => t.ByteRange.StartByte))
        {
            try
            {
                var typeNode = CreateTraitNode(
                    traitInfo,
                    document,
                    artifact.Id,
                    documentId,
                    now,
                    out var span);
                nodes.Add(typeNode);
                spans.Add(span);
                AddDocumentChild(documentChildren, documentChildIds, traitInfo.ByteRange.StartByte, typeNode);
                decoratedNodes.Add(new DecoratedNodeEntry(traitInfo.ByteRange.StartByte, traitInfo.ByteRange.EndByte, typeNode));
                RegisterTypeLookup(typeNodesByLookup, typeNode, traitInfo.Name, traitInfo.QualifiedName);

                foreach (var supertrait in ExtractTargets(traitInfo.Supertraits, '+'))
                {
                    edges.Add(CreateReferenceEdge(
                        typeNode.Id,
                        RustEdgeTypes.Extends,
                        supertrait,
                        documentId,
                        now));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to materialize Rust trait {TypeName}", traitInfo.Name);
            }
        }

        foreach (var unionInfo in state.Surface.Unions.OrderBy(u => u.ByteRange.StartByte))
        {
            try
            {
                var typeNode = CreateUnionNode(
                    unionInfo,
                    document,
                    artifact.Id,
                    documentId,
                    now,
                    out var span);
                nodes.Add(typeNode);
                spans.Add(span);
                AddDocumentChild(documentChildren, documentChildIds, unionInfo.ByteRange.StartByte, typeNode);
                decoratedNodes.Add(new DecoratedNodeEntry(unionInfo.ByteRange.StartByte, unionInfo.ByteRange.EndByte, typeNode));
                RegisterTypeLookup(typeNodesByLookup, typeNode, unionInfo.Name, unionInfo.QualifiedName);
                AddDeriveEdges(edges, typeNode.Id, unionInfo.Derives, documentId, now);
                AddDeriveAnnotations(
                    annotations,
                    spans,
                    unionInfo.Attributes,
                    document,
                    documentId,
                    now);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to materialize Rust union {TypeName}", unionInfo.Name);
            }
        }

        foreach (var aliasInfo in state.Surface.TypeAliases.OrderBy(a => a.ByteRange.StartByte))
        {
            try
            {
                var typeNode = CreateTypeAliasNode(
                    aliasInfo,
                    document,
                    artifact.Id,
                    documentId,
                    now,
                    out var span);
                nodes.Add(typeNode);
                spans.Add(span);
                AddDocumentChild(documentChildren, documentChildIds, aliasInfo.ByteRange.StartByte, typeNode);
                decoratedNodes.Add(new DecoratedNodeEntry(aliasInfo.ByteRange.StartByte, aliasInfo.ByteRange.EndByte, typeNode));
                RegisterTypeLookup(typeNodesByLookup, typeNode, aliasInfo.Name, aliasInfo.QualifiedName);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to materialize Rust type alias {TypeName}", aliasInfo.Name);
            }
        }

        foreach (var function in state.Surface.Functions.OrderBy(f => f.ByteRange.StartByte))
        {
            try
            {
                var functionNode = CreateFunctionNode(
                    function,
                    document,
                    artifact.Id,
                    documentId,
                    now,
                    out var span);
                nodes.Add(functionNode);
                spans.Add(span);
                AddDocumentChild(documentChildren, documentChildIds, function.ByteRange.StartByte, functionNode);
                decoratedNodes.Add(new DecoratedNodeEntry(function.ByteRange.StartByte, function.ByteRange.EndByte, functionNode));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to materialize Rust function {FunctionName}", function.Name);
            }
        }

        foreach (var module in state.Surface.Modules.OrderBy(m => m.ByteRange.StartByte))
        {
            try
            {
                var moduleNode = CreateModuleNode(
                    module,
                    document,
                    artifact.Id,
                    documentId,
                    now,
                    out var span);
                nodes.Add(moduleNode);
                spans.Add(span);
                AddDocumentChild(documentChildren, documentChildIds, module.ByteRange.StartByte, moduleNode);
                decoratedNodes.Add(new DecoratedNodeEntry(module.ByteRange.StartByte, module.ByteRange.EndByte, moduleNode));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to materialize Rust module {ModuleName}", module.Name);
            }
        }

        foreach (var implBlock in state.Surface.ImplBlocks.OrderBy(i => i.ByteRange.StartByte))
        {
            try
            {
                var targetTypeName = NormalizeTypeName(implBlock.TargetType);
                if (string.IsNullOrWhiteSpace(targetTypeName))
                {
                    targetTypeName = NormalizeWhitespace(implBlock.TargetType);
                }

                var owner = ResolveImplOwner(typeNodesByLookup, targetTypeName);
                if (owner is null)
                {
                    owner = CreateStubTypeNode(
                        targetTypeName,
                        implBlock.ByteRange,
                        document,
                        artifact.Id,
                        documentId,
                        now,
                        out var stubSpan);
                    nodes.Add(owner);
                    spans.Add(stubSpan);
                    AddDocumentChild(documentChildren, documentChildIds, implBlock.ByteRange.StartByte, owner);
                    RegisterTypeLookup(
                        typeNodesByLookup,
                        owner,
                        owner.Props[RustPropertyKeys.Name]?.ToString(),
                        owner.Props[RustPropertyKeys.QualifiedName]?.ToString());
                }

                var declaringType = owner.Props[RustPropertyKeys.QualifiedName]?.ToString() ?? targetTypeName;
                var implTrait = NormalizeOptionalTarget(implBlock.TraitName);
                if (!string.IsNullOrWhiteSpace(implTrait))
                {
                    edges.Add(CreateReferenceEdge(
                        owner.Id,
                        RustEdgeTypes.Implements,
                        implTrait,
                        documentId,
                        now,
                        isUnsafe: implBlock.IsUnsafe ? "true" : "false"));
                    AddToStringArray(owner.Props, RustPropertyKeys.Implements, implTrait);
                }

                foreach (var method in implBlock.Methods.OrderBy(m => m.ByteRange.StartByte))
                {
                    try
                    {
                        var memberNode = CreateMethodNode(
                            declaringType,
                            method,
                            implTrait,
                            document,
                            artifact.Id,
                            documentId,
                            now,
                            out var span);

                        nodes.Add(memberNode);
                        spans.Add(span);
                        decoratedNodes.Add(new DecoratedNodeEntry(method.ByteRange.StartByte, method.ByteRange.EndByte, memberNode));

                        if (!memberChildrenByOwner.TryGetValue(owner.Id, out var ownerMembers))
                        {
                            ownerMembers = [];
                            memberChildrenByOwner[owner.Id] = ownerMembers;
                        }

                        ownerMembers.Add(new ChildEntry(method.ByteRange.StartByte, memberNode));
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(
                            ex,
                            "Failed to materialize Rust impl method {MethodName} on {DeclaringType}",
                            method.Name,
                            declaringType);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to materialize Rust impl block for {TargetType}", implBlock.TargetType);
            }
        }

        AddImportEdges(edges, state.Surface.UseDeclarations, documentId, now);

        foreach (var macroDef in state.Surface.MacroDefs.OrderBy(m => m.ByteRange.StartByte))
        {
            try
            {
                var qualifiedName = BuildMacroQualifiedName(macroDef, state.Surface.Modules);
                var macroNode = CreateMacroNode(
                    macroDef,
                    qualifiedName,
                    document,
                    artifact.Id,
                    documentId,
                    now,
                    out var span);

                nodes.Add(macroNode);
                spans.Add(span);
                AddDocumentChild(documentChildren, documentChildIds, macroDef.ByteRange.StartByte, macroNode);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to materialize Rust macro {MacroName}", macroDef.Name);
            }
        }

        AddMacroInvocationAnnotations(
            annotations,
            spans,
            state.Surface.MacroInvocations,
            document,
            documentId,
            now);

        ApplyStructuredAttributesAndProcMacroAnnotations(
            decoratedNodes,
            state.Surface.Attributes,
            document,
            spans,
            annotations,
            documentId,
            now);

        var ordinal = 0;
        foreach (var child in documentChildren.OrderBy(c => c.StartByte))
        {
            edges.Add(CreateComposition(documentId, child.Node.Id, ordinal++, documentId, now));
        }

        foreach (var owner in memberChildrenByOwner)
        {
            var memberOrdinal = 0;
            foreach (var child in owner.Value.OrderBy(c => c.StartByte))
            {
                edges.Add(CreateComposition(owner.Key, child.Node.Id, memberOrdinal++, documentId, now));
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
        yield return new FormatSqlScript("rust_views", RustViewsSql.Value);
    }

    public void Dispose()
    {
        _client.Dispose();
    }

    private static RustDocumentSurface CreateEmptySurface(string text)
    {
        var lineCount = text.Length == 0 ? 0 : text.Count(ch => ch == '\n') + 1;
        return new RustDocumentSurface(
            Structs: [],
            Enums: [],
            Traits: [],
            ImplBlocks: [],
            Functions: [],
            Modules: [],
            Constants: [],
            Statics: [],
            TypeAliases: [],
            Unions: [],
            MacroDefs: [],
            MacroInvocations: [],
            UseDeclarations: [],
            Attributes: [],
            ExternBlocks: [],
            Stats: new RustParseStats(0, 0, 0, 0, 0, lineCount),
            ErrorNodeCount: 0);
    }

    private static Node CreateStructNode(
        RustStructInfo info,
        DocumentModel document,
        Guid artifactId,
        Guid documentId,
        DateTimeOffset now,
        out Span span)
    {
        var declaration = BuildStructDeclaration(info);
        return CreateTypeNode(
            name: info.Name,
            qualifiedName: info.QualifiedName,
            kind: "struct",
            accessibility: info.Visibility,
            generics: info.Generics,
            whereClause: info.WhereClause,
            derives: info.Derives,
            extends: null,
            isAuto: null,
            isUnsafe: null,
            isStub: false,
            fields: BuildFieldArray(info.Fields),
            variants: new JsonArray(),
            associatedTypes: new JsonArray(),
            associatedConsts: new JsonArray(),
            range: info.ByteRange,
            declaration: declaration,
            document,
            artifactId,
            documentId,
            now,
            out span);
    }

    private static Node CreateEnumNode(
        RustEnumInfo info,
        DocumentModel document,
        Guid artifactId,
        Guid documentId,
        DateTimeOffset now,
        out Span span)
    {
        var declaration = BuildEnumDeclaration(info);
        return CreateTypeNode(
            name: info.Name,
            qualifiedName: info.QualifiedName,
            kind: "enum",
            accessibility: info.Visibility,
            generics: info.Generics,
            whereClause: info.WhereClause,
            derives: info.Derives,
            extends: null,
            isAuto: null,
            isUnsafe: null,
            isStub: false,
            fields: new JsonArray(),
            variants: BuildVariantArray(info.Variants),
            associatedTypes: new JsonArray(),
            associatedConsts: new JsonArray(),
            range: info.ByteRange,
            declaration: declaration,
            document,
            artifactId,
            documentId,
            now,
            out span);
    }

    private static Node CreateTraitNode(
        RustTraitInfo info,
        DocumentModel document,
        Guid artifactId,
        Guid documentId,
        DateTimeOffset now,
        out Span span)
    {
        var declaration = BuildTraitDeclaration(info);
        return CreateTypeNode(
            name: info.Name,
            qualifiedName: info.QualifiedName,
            kind: "trait",
            accessibility: info.Visibility,
            generics: info.Generics,
            whereClause: info.WhereClause,
            derives: null,
            extends: info.Supertraits,
            isAuto: info.IsAuto,
            isUnsafe: info.IsUnsafe,
            isStub: false,
            fields: new JsonArray(),
            variants: new JsonArray(),
            associatedTypes: BuildAssociatedTypesArray(info.AssociatedTypes),
            associatedConsts: BuildAssociatedConstsArray(info.AssociatedConsts),
            range: info.ByteRange,
            declaration: declaration,
            document,
            artifactId,
            documentId,
            now,
            out span);
    }

    private static Node CreateUnionNode(
        RustUnionInfo info,
        DocumentModel document,
        Guid artifactId,
        Guid documentId,
        DateTimeOffset now,
        out Span span)
    {
        var declaration = BuildUnionDeclaration(info);
        return CreateTypeNode(
            name: info.Name,
            qualifiedName: info.QualifiedName,
            kind: "union",
            accessibility: info.Visibility,
            generics: info.Generics,
            whereClause: null,
            derives: info.Derives,
            extends: null,
            isAuto: null,
            isUnsafe: null,
            isStub: false,
            fields: BuildFieldArray(info.Fields),
            variants: new JsonArray(),
            associatedTypes: new JsonArray(),
            associatedConsts: new JsonArray(),
            range: info.ByteRange,
            declaration: declaration,
            document,
            artifactId,
            documentId,
            now,
            out span);
    }

    private static Node CreateTypeAliasNode(
        RustTypeAliasInfo info,
        DocumentModel document,
        Guid artifactId,
        Guid documentId,
        DateTimeOffset now,
        out Span span)
    {
        var declaration = BuildTypeAliasDeclaration(info);
        return CreateTypeNode(
            name: info.Name,
            qualifiedName: info.QualifiedName,
            kind: "type_alias",
            accessibility: info.Visibility,
            generics: info.Generics,
            whereClause: null,
            derives: null,
            extends: null,
            isAuto: null,
            isUnsafe: null,
            isStub: false,
            fields: new JsonArray(),
            variants: new JsonArray(),
            associatedTypes: new JsonArray(),
            associatedConsts: new JsonArray(),
            range: info.ByteRange,
            declaration: declaration,
            document,
            artifactId,
            documentId,
            now,
            out span);
    }

    private static Node CreateStubTypeNode(
        string targetTypeName,
        RustByteRange range,
        DocumentModel document,
        Guid artifactId,
        Guid documentId,
        DateTimeOffset now,
        out Span span)
    {
        var normalizedQualifiedName = NormalizeTypeName(targetTypeName);
        if (string.IsNullOrWhiteSpace(normalizedQualifiedName))
        {
            normalizedQualifiedName = NormalizeWhitespace(targetTypeName);
        }

        if (string.IsNullOrWhiteSpace(normalizedQualifiedName))
        {
            normalizedQualifiedName = "_";
        }

        var name = ExtractSimpleTypeName(normalizedQualifiedName);
        var declaration = $"struct {name}";

        return CreateTypeNode(
            name: name,
            qualifiedName: normalizedQualifiedName,
            kind: "struct",
            accessibility: "private",
            generics: null,
            whereClause: null,
            derives: null,
            extends: null,
            isAuto: null,
            isUnsafe: null,
            isStub: true,
            fields: new JsonArray(),
            variants: new JsonArray(),
            associatedTypes: new JsonArray(),
            associatedConsts: new JsonArray(),
            range: range,
            declaration: declaration,
            document,
            artifactId,
            documentId,
            now,
            out span);
    }

    private static void AddDocumentChild(
        ICollection<ChildEntry> documentChildren,
        ISet<Guid> documentChildIds,
        int startByte,
        Node childNode)
    {
        if (documentChildIds.Add(childNode.Id))
        {
            documentChildren.Add(new ChildEntry(startByte, childNode));
        }
    }

    private static void AddImportEdges(
        ICollection<Edge> edges,
        IEnumerable<RustUseDeclarationInfo> useDeclarations,
        Guid documentId,
        DateTimeOffset now)
    {
        foreach (var useDeclaration in useDeclarations.OrderBy(u => u.ByteRange.StartByte))
        {
            foreach (var import in ExpandUseDeclaration(useDeclaration))
            {
                edges.Add(CreateImportEdge(
                    documentId,
                    import.Path,
                    import.Alias,
                    import.IsGlob,
                    useDeclaration.IsPub,
                    documentId,
                    now));
            }
        }
    }

    private static IReadOnlyList<ImportEntry> ExpandUseDeclaration(RustUseDeclarationInfo useDeclaration)
    {
        var expanded = new List<ImportEntry>();
        ExpandUsePathRecursive(useDeclaration.Path, expanded);
        if (expanded.Count == 0)
        {
            expanded.Add(ParseUseLeaf(useDeclaration.Path));
        }

        var includeFallbackAlias = expanded.Count == 1;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var results = new List<ImportEntry>();

        foreach (var item in expanded)
        {
            var path = NormalizeUsePath(item.Path);
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            var alias = string.IsNullOrWhiteSpace(item.Alias) && includeFallbackAlias
                ? useDeclaration.Alias
                : item.Alias;
            var isGlob = item.IsGlob || useDeclaration.IsGlob || path.Contains('*', StringComparison.Ordinal);
            var key = $"{path}|{alias}|{isGlob}";
            if (seen.Add(key))
            {
                results.Add(new ImportEntry(path, alias, isGlob));
            }
        }

        return results;
    }

    private static void ExpandUsePathRecursive(string pathExpression, ICollection<ImportEntry> results)
    {
        var normalized = NormalizeUsePath(pathExpression);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        var openBrace = normalized.IndexOf('{', StringComparison.Ordinal);
        if (openBrace < 0)
        {
            results.Add(ParseUseLeaf(normalized));
            return;
        }

        var closeBrace = FindMatchingBrace(normalized, openBrace);
        if (closeBrace < 0)
        {
            results.Add(ParseUseLeaf(normalized));
            return;
        }

        var prefix = normalized[..openBrace].Trim();
        if (prefix.EndsWith("::", StringComparison.Ordinal))
        {
            prefix = prefix[..^2];
        }

        var leaves = normalized[(openBrace + 1)..closeBrace];
        foreach (var leaf in SplitDelimitedAtTopLevel(leaves, ','))
        {
            var branch = leaf.Trim();
            if (branch.Length == 0)
            {
                continue;
            }

            string combined;
            if (string.IsNullOrWhiteSpace(prefix))
            {
                combined = branch;
            }
            else if (string.Equals(branch, "self", StringComparison.Ordinal)
                     || branch.StartsWith("self as ", StringComparison.Ordinal))
            {
                combined = $"{prefix}{branch["self".Length..]}";
            }
            else
            {
                combined = $"{prefix}::{branch}";
            }

            ExpandUsePathRecursive(combined, results);
        }
    }

    private static ImportEntry ParseUseLeaf(string pathExpression)
    {
        var normalized = NormalizeUsePath(pathExpression);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return new ImportEntry(string.Empty, null, false);
        }

        string? alias = null;
        const string aliasKeyword = " as ";
        var aliasIndex = normalized.LastIndexOf(aliasKeyword, StringComparison.Ordinal);
        if (aliasIndex > 0 && aliasIndex + aliasKeyword.Length < normalized.Length)
        {
            alias = normalized[(aliasIndex + aliasKeyword.Length)..].Trim();
            normalized = normalized[..aliasIndex].Trim();
        }

        if (string.Equals(normalized, "self", StringComparison.Ordinal))
        {
            normalized = string.Empty;
        }

        return new ImportEntry(
            NormalizeUsePath(normalized),
            string.IsNullOrWhiteSpace(alias) ? null : alias,
            normalized.Contains('*', StringComparison.Ordinal));
    }

    private static int FindMatchingBrace(string value, int openBraceIndex)
    {
        if (openBraceIndex < 0 || openBraceIndex >= value.Length || value[openBraceIndex] != '{')
        {
            return -1;
        }

        var depth = 0;
        for (var i = openBraceIndex; i < value.Length; i++)
        {
            if (value[i] == '{')
            {
                depth++;
                continue;
            }

            if (value[i] != '}')
            {
                continue;
            }

            depth--;
            if (depth == 0)
            {
                return i;
            }
        }

        return -1;
    }

    private static string NormalizeUsePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return NormalizeWhitespace(value)
            .Replace(" :: ", "::", StringComparison.Ordinal)
            .Replace(":: ", "::", StringComparison.Ordinal)
            .Replace(" ::", "::", StringComparison.Ordinal)
            .Replace("{ ", "{", StringComparison.Ordinal)
            .Replace(" }", "}", StringComparison.Ordinal)
            .Replace(", ", ",", StringComparison.Ordinal)
            .Trim();
    }

    private static Edge CreateImportEdge(
        Guid srcId,
        string path,
        string? alias,
        bool isGlob,
        bool isPub,
        Guid scopeDocId,
        DateTimeOffset now)
    {
        var props = new JsonObject
        {
            [RustPropertyKeys.Path] = path,
            [RustPropertyKeys.IsGlob] = isGlob ? "true" : "false",
            [RustPropertyKeys.IsPub] = isPub ? "true" : "false"
        };

        if (!string.IsNullOrWhiteSpace(alias))
        {
            props[RustPropertyKeys.Alias] = alias;
        }

        return new Edge
        {
            Id = Guid.NewGuid(),
            SrcId = srcId,
            DstId = null,
            Type = RustEdgeTypes.Imports,
            IsComposition = false,
            ScopeDocumentId = scopeDocId,
            Props = props,
            CreatedAt = now
        };
    }

    private static string BuildMacroQualifiedName(RustMacroDefInfo macroDef, IReadOnlyList<RustModuleInfo> modules)
    {
        var container = modules
            .Where(m =>
                m.ByteRange.StartByte <= macroDef.ByteRange.StartByte
                && m.ByteRange.EndByte >= macroDef.ByteRange.EndByte)
            .OrderByDescending(m => m.ByteRange.StartByte)
            .FirstOrDefault();

        return container is null
            ? macroDef.Name
            : $"{container.QualifiedName}::{macroDef.Name}";
    }

    private static Node CreateMacroNode(
        RustMacroDefInfo macroDef,
        string qualifiedName,
        DocumentModel document,
        Guid artifactId,
        Guid documentId,
        DateTimeOffset now,
        out Span span)
    {
        span = CreateSpan(macroDef.ByteRange, document, documentId);
        var declaration = $"macro_rules! {macroDef.Name}";

        return new Node
        {
            Id = Guid.NewGuid(),
            Kind = RustNodeKinds.Macro,
            SpanId = span.Id,
            Uri = RepoUri.FromSymbol(document.Uri.Container, qualifiedName, span.StartLine, span.EndLine),
            ArtifactId = artifactId,
            Props = new JsonObject
            {
                [RustPropertyKeys.Name] = macroDef.Name,
                [RustPropertyKeys.QualifiedName] = qualifiedName,
                [RustPropertyKeys.Accessibility] = macroDef.Visibility
            },
            Headline = declaration,
            Structure = $"{VisibilitySymbol(macroDef.Visibility)}{declaration}",
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static void AddMacroInvocationAnnotations(
        ICollection<Annotation> annotations,
        ICollection<Span> spans,
        IEnumerable<RustMacroInvocationInfo> macroInvocations,
        DocumentModel document,
        Guid documentId,
        DateTimeOffset now)
    {
        foreach (var invocation in macroInvocations.OrderBy(m => m.ByteRange.StartByte))
        {
            if (string.IsNullOrWhiteSpace(invocation.MacroName)
                || RustMacroFilters.NonStructuralMacroInvocations.Contains(invocation.MacroName))
            {
                continue;
            }

            var span = CreateSpan(invocation.ByteRange, document, documentId);
            spans.Add(span);
            annotations.Add(CreateMacroExpansionAnnotation(
                documentId,
                span.Id,
                invocation.MacroName,
                $"Macro invocation '{invocation.MacroName}!' may generate items; expansion is not captured.",
                now));
        }
    }

    private static void ApplyStructuredAttributesAndProcMacroAnnotations(
        IReadOnlyList<DecoratedNodeEntry> decoratedNodes,
        IReadOnlyList<RustAttributeInfo> attributes,
        DocumentModel document,
        ICollection<Span> spans,
        ICollection<Annotation> annotations,
        Guid documentId,
        DateTimeOffset now)
    {
        if (attributes.Count == 0)
        {
            return;
        }

        var attributesByNode = MapAttributesToDecoratedNodes(decoratedNodes, attributes, document);
        foreach (var decoratedNode in decoratedNodes)
        {
            if (attributesByNode.TryGetValue(decoratedNode.Node.Id, out var nodeAttributes)
                && nodeAttributes.Count > 0)
            {
                ApplyStructuredAttributeProperties(decoratedNode.Node.Props, nodeAttributes);
            }
        }

        foreach (var attribute in attributes.OrderBy(a => a.ByteRange.StartByte))
        {
            if (!ShouldAnnotateProcMacroAttribute(attribute.Name))
            {
                continue;
            }

            var span = CreateSpan(attribute.ByteRange, document, documentId);
            spans.Add(span);
            annotations.Add(CreateMacroExpansionAnnotation(
                documentId,
                span.Id,
                attribute.Name,
                $"Attribute '#[{attribute.Name}]' may invoke a proc-macro; generated items are not captured.",
                now));
        }
    }

    private static Dictionary<Guid, List<RustAttributeInfo>> MapAttributesToDecoratedNodes(
        IReadOnlyList<DecoratedNodeEntry> decoratedNodes,
        IReadOnlyList<RustAttributeInfo> attributes,
        DocumentModel document)
    {
        var result = new Dictionary<Guid, List<RustAttributeInfo>>();
        if (decoratedNodes.Count == 0 || attributes.Count == 0)
        {
            return result;
        }

        var orderedNodes = decoratedNodes
            .OrderBy(n => n.StartByte)
            .ThenBy(n => n.EndByte)
            .ToArray();
        var orderedAttributes = attributes
            .OrderBy(a => a.ByteRange.StartByte)
            .ToArray();
        var consumed = new bool[orderedAttributes.Length];

        foreach (var node in orderedNodes)
        {
            var cursor = orderedAttributes.Length - 1;
            while (cursor >= 0 && orderedAttributes[cursor].ByteRange.EndByte > node.StartByte)
            {
                cursor--;
            }

            var currentStart = node.StartByte;
            var nodeAttributes = new List<RustAttributeInfo>();

            while (cursor >= 0)
            {
                if (consumed[cursor])
                {
                    cursor--;
                    continue;
                }

                var candidate = orderedAttributes[cursor];
                if (candidate.ByteRange.EndByte > currentStart)
                {
                    cursor--;
                    continue;
                }

                if (!ContainsOnlyWhitespaceBetweenBytes(candidate.ByteRange.EndByte, currentStart, document))
                {
                    break;
                }

                nodeAttributes.Add(candidate);
                consumed[cursor] = true;
                currentStart = candidate.ByteRange.StartByte;
                cursor--;
            }

            if (nodeAttributes.Count == 0)
            {
                continue;
            }

            nodeAttributes.Reverse();
            result[node.Node.Id] = nodeAttributes;
        }

        return result;
    }

    private static bool ContainsOnlyWhitespaceBetweenBytes(int startByte, int endByte, DocumentModel document)
    {
        if (endByte <= startByte)
        {
            return true;
        }

        var range = document.LineMap.GetSpan(startByte, endByte);
        var start = Math.Clamp(range.StartChar, 0, document.Text.Length);
        var end = Math.Clamp(range.EndChar, start, document.Text.Length);

        for (var i = start; i < end; i++)
        {
            if (!char.IsWhiteSpace(document.Text[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static void ApplyStructuredAttributeProperties(JsonObject props, IReadOnlyList<RustAttributeInfo> attributes)
    {
        var attributeArray = new JsonArray();
        foreach (var attribute in attributes.OrderBy(a => a.ByteRange.StartByte))
        {
            var item = new JsonObject
            {
                [RustPropertyKeys.Name] = attribute.Name
            };

            if (!string.IsNullOrWhiteSpace(attribute.Arguments))
            {
                item["arguments"] = attribute.Arguments;
            }

            attributeArray.Add(item);

            if (string.Equals(attribute.Name, "test", StringComparison.Ordinal))
            {
                props[RustPropertyKeys.IsTest] = true;
                continue;
            }

            if (string.Equals(attribute.Name, "cfg", StringComparison.Ordinal))
            {
                var predicate = ExtractAttributePredicate(attribute.Arguments);
                if (!string.IsNullOrWhiteSpace(predicate))
                {
                    props[RustPropertyKeys.Cfg] = predicate;
                }

                continue;
            }

            if (string.Equals(attribute.Name, "inline", StringComparison.Ordinal))
            {
                props[RustPropertyKeys.IsInline] = true;
                continue;
            }

            if (string.Equals(attribute.Name, "must_use", StringComparison.Ordinal))
            {
                props[RustPropertyKeys.MustUse] = true;
                continue;
            }

            if (string.Equals(attribute.Name, "deprecated", StringComparison.Ordinal))
            {
                props[RustPropertyKeys.IsDeprecated] = true;
            }
        }

        props[RustPropertyKeys.Attributes] = attributeArray;
    }

    private static string? ExtractAttributePredicate(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
        {
            return null;
        }

        var text = NormalizeWhitespace(arguments);
        if (text.StartsWith('(') && text.EndsWith(')') && text.Length > 2)
        {
            text = text[1..^1].Trim();
        }

        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static bool ShouldAnnotateProcMacroAttribute(string? attributeName)
    {
        if (string.IsNullOrWhiteSpace(attributeName))
        {
            return false;
        }

        return !RustMacroFilters.BuiltInNonGenerativeAttributes.Contains(attributeName);
    }

    private static void AddDeriveEdges(
        ICollection<Edge> edges,
        Guid typeNodeId,
        string? derives,
        Guid scopeDocumentId,
        DateTimeOffset now)
    {
        foreach (var derivedTrait in ExtractTargets(derives, ','))
        {
            edges.Add(CreateReferenceEdge(
                typeNodeId,
                RustEdgeTypes.Derives,
                derivedTrait,
                scopeDocumentId,
                now));
        }
    }

    private static void AddDeriveAnnotations(
        ICollection<Annotation> annotations,
        ICollection<Span> spans,
        IEnumerable<RustAttributeInfo> attributes,
        DocumentModel document,
        Guid documentId,
        DateTimeOffset now)
    {
        foreach (var attribute in attributes.Where(a => string.Equals(a.Name, "derive", StringComparison.Ordinal)))
        {
            var deriveTraits = ExtractTargets(attribute.Arguments, ',');
            if (deriveTraits.Count == 0)
            {
                continue;
            }

            var span = CreateSpan(attribute.ByteRange, document, documentId);
            spans.Add(span);
            annotations.Add(CreateDeriveAnnotation(documentId, span.Id, deriveTraits, now));
        }
    }

    private static Annotation CreateDeriveAnnotation(
        Guid documentId,
        Guid targetSpanId,
        IReadOnlyCollection<string> deriveTraits,
        DateTimeOffset now)
    {
        var message = $"derive({string.Join(", ", deriveTraits)}) applied; generated impl blocks are not captured.";
        return CreateMacroExpansionAnnotation(
            documentId,
            targetSpanId,
            RustAnnotationRuleIds.Derive,
            message,
            now);
    }

    private static Annotation CreateMacroExpansionAnnotation(
        Guid documentId,
        Guid targetSpanId,
        string ruleId,
        string message,
        DateTimeOffset now)
    {
        return new Annotation
        {
            Kind = RustAnnotationKinds.MacroExpansion,
            Severity = "info",
            Source = RustAnnotationSources.RustLoader,
            RuleId = ruleId,
            Message = message,
            ScopeDocumentId = documentId,
            TargetSpanId = targetSpanId,
            CreatedAt = now
        };
    }

    private static void AddToStringArray(JsonObject props, string key, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (props[key] is not JsonArray array)
        {
            array = [];
            props[key] = array;
        }

        if (array.Any(item => string.Equals(item?.ToString(), value, StringComparison.Ordinal)))
        {
            return;
        }

        array.Add(value);
    }

    private static IReadOnlyList<string> ExtractTargets(string? rawTargets, char separator)
    {
        if (string.IsNullOrWhiteSpace(rawTargets))
        {
            return [];
        }

        var text = rawTargets.Trim();
        if (text.Length == 0)
        {
            return [];
        }

        if (text.StartsWith('(') && text.EndsWith(')') && text.Length > 2)
        {
            text = text[1..^1];
        }

        return SplitDelimitedAtTopLevel(text, separator)
            .Select(NormalizeWhitespace)
            .Select(item => separator == '+' ? TrimLeadingColon(item) : item)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static string TrimLeadingColon(string value)
    {
        var trimmed = value.Trim();
        return trimmed.StartsWith(':') ? trimmed[1..].TrimStart() : trimmed;
    }

    private static IEnumerable<string> SplitDelimitedAtTopLevel(string value, char delimiter)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            yield break;
        }

        var current = new StringBuilder();
        var angleDepth = 0;
        var parenDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;

        foreach (var ch in value)
        {
            switch (ch)
            {
                case '<':
                    angleDepth++;
                    current.Append(ch);
                    continue;
                case '>':
                    angleDepth = Math.Max(0, angleDepth - 1);
                    current.Append(ch);
                    continue;
                case '(':
                    parenDepth++;
                    current.Append(ch);
                    continue;
                case ')':
                    parenDepth = Math.Max(0, parenDepth - 1);
                    current.Append(ch);
                    continue;
                case '[':
                    bracketDepth++;
                    current.Append(ch);
                    continue;
                case ']':
                    bracketDepth = Math.Max(0, bracketDepth - 1);
                    current.Append(ch);
                    continue;
                case '{':
                    braceDepth++;
                    current.Append(ch);
                    continue;
                case '}':
                    braceDepth = Math.Max(0, braceDepth - 1);
                    current.Append(ch);
                    continue;
            }

            if (ch == delimiter
                && angleDepth == 0
                && parenDepth == 0
                && bracketDepth == 0
                && braceDepth == 0)
            {
                var token = current.ToString().Trim();
                if (token.Length > 0)
                {
                    yield return token;
                }

                current.Clear();
                continue;
            }

            current.Append(ch);
        }

        var finalToken = current.ToString().Trim();
        if (finalToken.Length > 0)
        {
            yield return finalToken;
        }
    }

    private static Node CreateTypeNode(
        string name,
        string qualifiedName,
        string kind,
        string accessibility,
        string? generics,
        string? whereClause,
        string? derives,
        string? extends,
        bool? isAuto,
        bool? isUnsafe,
        bool isStub,
        JsonArray fields,
        JsonArray variants,
        JsonArray associatedTypes,
        JsonArray associatedConsts,
        RustByteRange range,
        string declaration,
        DocumentModel document,
        Guid artifactId,
        Guid documentId,
        DateTimeOffset now,
        out Span span)
    {
        span = CreateSpan(range, document, documentId);

        var props = new JsonObject
        {
            [RustPropertyKeys.Name] = name,
            [RustPropertyKeys.QualifiedName] = qualifiedName,
            [RustPropertyKeys.Kind] = kind,
            [RustPropertyKeys.Accessibility] = accessibility,
            [RustPropertyKeys.Fields] = fields,
            [RustPropertyKeys.Variants] = variants,
            [RustPropertyKeys.AssociatedTypes] = associatedTypes,
            [RustPropertyKeys.AssociatedConsts] = associatedConsts,
            [RustPropertyKeys.Implements] = new JsonArray(),
            [RustPropertyKeys.IsStub] = isStub
        };

        SetIfNotEmpty(props, RustPropertyKeys.Generics, generics);
        SetIfNotEmpty(props, RustPropertyKeys.WhereClause, whereClause);
        SetIfNotEmpty(props, RustPropertyKeys.Derives, derives);
        SetIfNotEmpty(props, RustPropertyKeys.Extends, extends);
        if (isAuto.HasValue)
            props[RustPropertyKeys.IsAuto] = isAuto.Value;
        if (isUnsafe.HasValue)
            props[RustPropertyKeys.IsUnsafe] = isUnsafe.Value;

        return new Node
        {
            Id = Guid.NewGuid(),
            Kind = RustNodeKinds.Type,
            SpanId = span.Id,
            Uri = RepoUri.FromSymbol(document.Uri.Container, qualifiedName, span.StartLine, span.EndLine),
            ArtifactId = artifactId,
            Props = props,
            Headline = declaration,
            Structure = $"{VisibilitySymbol(accessibility)}{declaration}",
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static Node CreateMethodNode(
        string declaringType,
        RustMethodInfo method,
        string? implTrait,
        DocumentModel document,
        Guid artifactId,
        Guid documentId,
        DateTimeOffset now,
        out Span span)
    {
        span = CreateSpan(method.ByteRange, document, documentId);
        var qualifiedName = BuildQualifiedName(declaringType, method.Name);
        var isStatic = string.Equals(method.SelfKind, "none", StringComparison.Ordinal);
        var signature = BuildMethodSignature(method, includeVisibilityKeyword: true);

        var props = new JsonObject
        {
            [RustPropertyKeys.Name] = method.Name,
            [RustPropertyKeys.QualifiedName] = qualifiedName,
            [RustPropertyKeys.Kind] = "method",
            [RustPropertyKeys.DeclaringType] = declaringType,
            [RustPropertyKeys.Accessibility] = method.Visibility,
            [RustPropertyKeys.IsAsync] = method.IsAsync,
            [RustPropertyKeys.IsUnsafe] = method.IsUnsafe,
            [RustPropertyKeys.IsConst] = method.IsConst,
            [RustPropertyKeys.IsStatic] = isStatic,
            [RustPropertyKeys.SelfKind] = method.SelfKind
        };

        SetIfNotEmpty(props, RustPropertyKeys.Parameters, method.Parameters);
        SetIfNotEmpty(props, RustPropertyKeys.ReturnType, method.ReturnType);
        SetIfNotEmpty(props, RustPropertyKeys.ImplTrait, implTrait);

        return new Node
        {
            Id = Guid.NewGuid(),
            Kind = RustNodeKinds.Member,
            SpanId = span.Id,
            Uri = RepoUri.FromSymbol(document.Uri.Container, qualifiedName, span.StartLine, span.EndLine),
            ArtifactId = artifactId,
            Props = props,
            Headline = signature,
            Structure = $"{VisibilitySymbol(method.Visibility)}{BuildMethodSignature(method, includeVisibilityKeyword: false)}",
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static Node CreateFunctionNode(
        RustFunctionInfo function,
        DocumentModel document,
        Guid artifactId,
        Guid documentId,
        DateTimeOffset now,
        out Span span)
    {
        span = CreateSpan(function.ByteRange, document, documentId);
        var signature = BuildFunctionSignature(function, includeVisibilityKeyword: true);

        var props = new JsonObject
        {
            [RustPropertyKeys.Name] = function.Name,
            [RustPropertyKeys.QualifiedName] = function.QualifiedName,
            [RustPropertyKeys.Kind] = "function",
            [RustPropertyKeys.Accessibility] = function.Visibility,
            [RustPropertyKeys.IsAsync] = function.IsAsync,
            [RustPropertyKeys.IsUnsafe] = function.IsUnsafe,
            [RustPropertyKeys.IsConst] = function.IsConst,
            [RustPropertyKeys.IsStatic] = true,
            [RustPropertyKeys.IsTest] = function.IsTest
        };

        SetIfNotEmpty(props, RustPropertyKeys.Generics, function.Generics);
        SetIfNotEmpty(props, RustPropertyKeys.Parameters, function.Parameters);
        SetIfNotEmpty(props, RustPropertyKeys.ReturnType, function.ReturnType);

        return new Node
        {
            Id = Guid.NewGuid(),
            Kind = RustNodeKinds.Function,
            SpanId = span.Id,
            Uri = RepoUri.FromSymbol(document.Uri.Container, function.QualifiedName, span.StartLine, span.EndLine),
            ArtifactId = artifactId,
            Props = props,
            Headline = signature,
            Structure = $"{VisibilitySymbol(function.Visibility)}{BuildFunctionSignature(function, includeVisibilityKeyword: false)}",
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static Node CreateModuleNode(
        RustModuleInfo module,
        DocumentModel document,
        Guid artifactId,
        Guid documentId,
        DateTimeOffset now,
        out Span span)
    {
        span = CreateSpan(module.ByteRange, document, documentId);
        var declaration = BuildModuleDeclaration(module);

        return new Node
        {
            Id = Guid.NewGuid(),
            Kind = RustNodeKinds.Module,
            SpanId = span.Id,
            Uri = RepoUri.FromSymbol(document.Uri.Container, module.QualifiedName, span.StartLine, span.EndLine),
            ArtifactId = artifactId,
            Props = new JsonObject
            {
                [RustPropertyKeys.Name] = module.Name,
                [RustPropertyKeys.QualifiedName] = module.QualifiedName,
                [RustPropertyKeys.Accessibility] = module.Visibility,
                [RustPropertyKeys.IsInline] = module.IsInline
            },
            Headline = declaration,
            Structure = $"{VisibilitySymbol(module.Visibility)}{declaration}",
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
            Type = RustEdgeTypes.HasPart,
            IsComposition = true,
            Ordinal = ordinal,
            ScopeDocumentId = scopeDocId,
            CreatedAt = now
        };
    }

    private static Edge CreateReferenceEdge(
        Guid srcId,
        string edgeType,
        string target,
        Guid scopeDocId,
        DateTimeOffset now,
        string? isUnsafe = null)
    {
        var props = new JsonObject
        {
            [RustPropertyKeys.Target] = target
        };

        if (!string.IsNullOrWhiteSpace(isUnsafe))
        {
            props[RustPropertyKeys.IsUnsafe] = isUnsafe;
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

    private static Span CreateSpan(RustByteRange range, DocumentModel document, Guid documentId)
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

    private static Node? ResolveImplOwner(IReadOnlyDictionary<string, Node> typeNodesByLookup, string targetType)
    {
        foreach (var key in BuildLookupKeys(targetType))
        {
            if (typeNodesByLookup.TryGetValue(key, out var owner))
            {
                return owner;
            }
        }

        return null;
    }

    private static void RegisterTypeLookup(
        IDictionary<string, Node> map,
        Node node,
        string? simpleName,
        string? qualifiedName)
    {
        foreach (var key in BuildLookupKeys(simpleName))
        {
            map.TryAdd(key, node);
        }

        foreach (var key in BuildLookupKeys(qualifiedName))
        {
            map.TryAdd(key, node);
        }
    }

    private static IEnumerable<string> BuildLookupKeys(string? raw)
    {
        var normalized = NormalizeTypeName(raw);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            yield break;
        }

        yield return normalized;

        var simple = ExtractSimpleTypeName(normalized);
        if (!string.Equals(simple, normalized, StringComparison.Ordinal))
        {
            yield return simple;
        }
    }

    private static string BuildHeadline(DocumentModel document, RustDocumentSurface surface, int? tokenCount)
    {
        var fileName = GetFileName(document.Uri);
        var primaryDeclaration = BuildPrimaryDeclaration(surface);
        var keyMembers = BuildKeyMembers(surface);
        var sizePart = $"{document.LineMap.LineCount} ln";
        if (tokenCount.HasValue)
        {
            sizePart = $"{sizePart}, ~{tokenCount.Value} tok";
        }

        return string.Join(
            " | ",
            new[] { fileName, primaryDeclaration, keyMembers, sizePart }
                .Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    private static string BuildPrimaryDeclaration(RustDocumentSurface surface)
    {
        var typeCount = surface.Structs.Count
                        + surface.Enums.Count
                        + surface.Traits.Count
                        + surface.Unions.Count
                        + surface.TypeAliases.Count;

        var declarationCount = typeCount + surface.Functions.Count + surface.Modules.Count;
        if (declarationCount == 0)
        {
            return "rust file";
        }

        if (declarationCount == 1)
        {
            if (surface.Structs.Count == 1)
                return BuildStructDeclaration(surface.Structs[0]);
            if (surface.Enums.Count == 1)
                return BuildEnumDeclaration(surface.Enums[0]);
            if (surface.Traits.Count == 1)
                return BuildTraitDeclaration(surface.Traits[0]);
            if (surface.Unions.Count == 1)
                return BuildUnionDeclaration(surface.Unions[0]);
            if (surface.TypeAliases.Count == 1)
                return BuildTypeAliasDeclaration(surface.TypeAliases[0]);
            if (surface.Functions.Count == 1)
                return BuildFunctionSignature(surface.Functions[0], includeVisibilityKeyword: false);
            if (surface.Modules.Count == 1)
                return BuildModuleDeclaration(surface.Modules[0]);
        }

        if (surface.Modules.Count > 0 && typeCount == 0 && surface.Functions.Count == 0)
        {
            var names = surface.Modules.Select(m => $"mod {m.Name}").ToList();
            return names.Count <= 4
                ? string.Join(", ", names)
                : $"{string.Join(", ", names.Take(4))}, +{names.Count - 4}";
        }

        return $"{declarationCount} declarations";
    }

    private static string? BuildKeyMembers(RustDocumentSurface surface)
    {
        var candidates = new List<(int StartByte, string Name)>();
        candidates.AddRange(surface.Functions
            .Where(f => IsPublic(f.Visibility))
            .Select(f => (f.ByteRange.StartByte, f.Name)));
        candidates.AddRange(surface.ImplBlocks
            .SelectMany(i => i.Methods)
            .Where(m => IsPublic(m.Visibility))
            .Select(m => (m.ByteRange.StartByte, m.Name)));

        var names = candidates
            .OrderBy(c => c.StartByte)
            .Select(c => c.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.Ordinal)
            .Take(8)
            .ToList();

        return names.Count == 0 ? null : string.Join(", ", names);
    }

    private static string BuildStructure(RustDocumentSurface surface)
    {
        var lines = new List<string>();

        var typeCanonicalByLookup = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var typeEntry in EnumerateTypes(surface))
        {
            RegisterCanonical(typeCanonicalByLookup, typeEntry.CanonicalKey, typeEntry.Name, typeEntry.QualifiedName);
        }

        var implsByCanonical = new Dictionary<string, List<RustImplBlockInfo>>(StringComparer.Ordinal);
        var crossFileImpls = new List<RustImplBlockInfo>();
        foreach (var implBlock in surface.ImplBlocks.OrderBy(i => i.ByteRange.StartByte))
        {
            var canonical = ResolveCanonicalType(typeCanonicalByLookup, implBlock.TargetType);
            if (canonical is null)
            {
                crossFileImpls.Add(implBlock);
                continue;
            }

            if (!implsByCanonical.TryGetValue(canonical, out var items))
            {
                items = [];
                implsByCanonical[canonical] = items;
            }

            items.Add(implBlock);
        }

        var roots = new List<StructureRootEntry>();
        roots.AddRange(surface.Structs.Select(s => new StructureRootEntry(s.ByteRange.StartByte, Struct: s)));
        roots.AddRange(surface.Enums.Select(e => new StructureRootEntry(e.ByteRange.StartByte, Enum: e)));
        roots.AddRange(surface.Traits.Select(t => new StructureRootEntry(t.ByteRange.StartByte, Trait: t)));
        roots.AddRange(surface.Unions.Select(u => new StructureRootEntry(u.ByteRange.StartByte, Union: u)));
        roots.AddRange(surface.TypeAliases.Select(a => new StructureRootEntry(a.ByteRange.StartByte, TypeAlias: a)));
        roots.AddRange(surface.Modules.Select(m => new StructureRootEntry(m.ByteRange.StartByte, Module: m)));
        roots.AddRange(surface.Functions.Select(f => new StructureRootEntry(f.ByteRange.StartByte, Function: f)));
        roots.AddRange(surface.Constants.Select(c => new StructureRootEntry(c.ByteRange.StartByte, Constant: c)));
        roots.AddRange(surface.Statics.Select(s => new StructureRootEntry(s.ByteRange.StartByte, Static: s)));
        roots.AddRange(crossFileImpls.Select(i => new StructureRootEntry(i.ByteRange.StartByte, Impl: i)));

        foreach (var root in roots.OrderBy(r => r.StartByte))
        {
            if (root.Struct is not null)
            {
                AppendStruct(lines, root.Struct, implsByCanonical);
                continue;
            }

            if (root.Enum is not null)
            {
                AppendEnum(lines, root.Enum, implsByCanonical);
                continue;
            }

            if (root.Trait is not null)
            {
                AppendTrait(lines, root.Trait, implsByCanonical);
                continue;
            }

            if (root.Union is not null)
            {
                AppendUnion(lines, root.Union, implsByCanonical);
                continue;
            }

            if (root.TypeAlias is not null)
            {
                AppendTypeAlias(lines, root.TypeAlias, implsByCanonical);
                continue;
            }

            if (root.Module is not null)
            {
                AppendDocComment(lines, string.Empty, root.Module.DocComment);
                lines.Add($"{VisibilitySymbol(root.Module.Visibility)}{BuildModuleDeclaration(root.Module)}    #symbol={root.Module.QualifiedName}");
                continue;
            }

            if (root.Function is not null)
            {
                AppendDocComment(lines, string.Empty, root.Function.DocComment);
                lines.Add($"{VisibilitySymbol(root.Function.Visibility)}{BuildFunctionSignature(root.Function, includeVisibilityKeyword: false)}    #symbol={root.Function.QualifiedName}");
                continue;
            }

            if (root.Constant is not null)
            {
                AppendDocComment(lines, string.Empty, root.Constant.DocComment);
                var constType = string.IsNullOrWhiteSpace(root.Constant.ConstType) ? "_" : root.Constant.ConstType;
                lines.Add($"{VisibilitySymbol(root.Constant.Visibility)}const {root.Constant.Name}: {constType}    #symbol={root.Constant.Name}");
                continue;
            }

            if (root.Static is not null)
            {
                AppendDocComment(lines, string.Empty, root.Static.DocComment);
                var staticType = string.IsNullOrWhiteSpace(root.Static.StaticType) ? "_" : root.Static.StaticType;
                var mutability = root.Static.IsMutable ? "mut " : string.Empty;
                lines.Add($"{VisibilitySymbol(root.Static.Visibility)}static {mutability}{root.Static.Name}: {staticType}    #symbol={root.Static.Name}");
                continue;
            }

            if (root.Impl is not null)
            {
                AppendImplBlock(lines, root.Impl, NormalizeTypeName(root.Impl.TargetType), indent: string.Empty, includeHeader: true);
            }
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static void AppendStruct(
        List<string> lines,
        RustStructInfo info,
        IReadOnlyDictionary<string, List<RustImplBlockInfo>> implsByCanonical)
    {
        AppendDocComment(lines, string.Empty, info.DocComment);
        lines.Add($"{VisibilitySymbol(info.Visibility)}{BuildStructDeclaration(info)}    #symbol={info.QualifiedName}");
        if (!string.IsNullOrWhiteSpace(info.Derives))
        {
            lines.Add($"  derives: {info.Derives}");
        }

        foreach (var field in info.Fields.OrderBy(f => f.ByteRange.StartByte))
        {
            AppendDocComment(lines, "  ", field.DocComment);
            var fieldType = string.IsNullOrWhiteSpace(field.FieldType) ? "_" : field.FieldType;
            lines.Add($"  {VisibilitySymbol(field.Visibility)}{field.Name}: {fieldType}    #symbol={info.QualifiedName}.{field.Name}");
        }

        AppendImplBlocksForType(lines, info.QualifiedName, info.Name, implsByCanonical);
    }

    private static void AppendEnum(
        List<string> lines,
        RustEnumInfo info,
        IReadOnlyDictionary<string, List<RustImplBlockInfo>> implsByCanonical)
    {
        AppendDocComment(lines, string.Empty, info.DocComment);
        lines.Add($"{VisibilitySymbol(info.Visibility)}{BuildEnumDeclaration(info)}    #symbol={info.QualifiedName}");
        if (!string.IsNullOrWhiteSpace(info.Derives))
        {
            lines.Add($"  derives: {info.Derives}");
        }

        foreach (var variant in info.Variants.OrderBy(v => v.ByteRange.StartByte))
        {
            AppendDocComment(lines, "  ", variant.DocComment);
            lines.Add($"  +{BuildVariantSignature(variant)}    #symbol={info.QualifiedName}.{variant.Name}");
        }

        AppendImplBlocksForType(lines, info.QualifiedName, info.Name, implsByCanonical);
    }

    private static void AppendTrait(
        List<string> lines,
        RustTraitInfo info,
        IReadOnlyDictionary<string, List<RustImplBlockInfo>> implsByCanonical)
    {
        AppendDocComment(lines, string.Empty, info.DocComment);
        lines.Add($"{VisibilitySymbol(info.Visibility)}{BuildTraitDeclaration(info)}    #symbol={info.QualifiedName}");

        foreach (var associatedType in info.AssociatedTypes.OrderBy(t => t.ByteRange.StartByte))
        {
            var bounds = string.IsNullOrWhiteSpace(associatedType.Bounds) ? string.Empty : $": {associatedType.Bounds}";
            var defaultType = string.IsNullOrWhiteSpace(associatedType.DefaultType) ? string.Empty : $" = {associatedType.DefaultType}";
            lines.Add($"  type {associatedType.Name}{bounds}{defaultType}");
        }

        foreach (var associatedConst in info.AssociatedConsts.OrderBy(c => c.ByteRange.StartByte))
        {
            var constType = string.IsNullOrWhiteSpace(associatedConst.ConstType) ? string.Empty : $": {associatedConst.ConstType}";
            var hasDefault = associatedConst.HasDefault ? " = <default>" : string.Empty;
            lines.Add($"  const {associatedConst.Name}{constType}{hasDefault}");
        }

        foreach (var method in info.Methods.OrderBy(m => m.ByteRange.StartByte))
        {
            AppendDocComment(lines, "  ", method.DocComment);
            lines.Add($"  {VisibilitySymbol(method.Visibility)}{BuildMethodSignature(method, includeVisibilityKeyword: false)}    #symbol={info.QualifiedName}.{method.Name}");
        }

        AppendImplBlocksForType(lines, info.QualifiedName, info.Name, implsByCanonical);
    }

    private static void AppendUnion(
        List<string> lines,
        RustUnionInfo info,
        IReadOnlyDictionary<string, List<RustImplBlockInfo>> implsByCanonical)
    {
        AppendDocComment(lines, string.Empty, info.DocComment);
        lines.Add($"{VisibilitySymbol(info.Visibility)}{BuildUnionDeclaration(info)}    #symbol={info.QualifiedName}");
        if (!string.IsNullOrWhiteSpace(info.Derives))
        {
            lines.Add($"  derives: {info.Derives}");
        }

        foreach (var field in info.Fields.OrderBy(f => f.ByteRange.StartByte))
        {
            AppendDocComment(lines, "  ", field.DocComment);
            var fieldType = string.IsNullOrWhiteSpace(field.FieldType) ? "_" : field.FieldType;
            lines.Add($"  {VisibilitySymbol(field.Visibility)}{field.Name}: {fieldType}    #symbol={info.QualifiedName}.{field.Name}");
        }

        AppendImplBlocksForType(lines, info.QualifiedName, info.Name, implsByCanonical);
    }

    private static void AppendTypeAlias(
        List<string> lines,
        RustTypeAliasInfo info,
        IReadOnlyDictionary<string, List<RustImplBlockInfo>> implsByCanonical)
    {
        lines.Add($"{VisibilitySymbol(info.Visibility)}{BuildTypeAliasDeclaration(info)}    #symbol={info.QualifiedName}");
        AppendImplBlocksForType(lines, info.QualifiedName, info.Name, implsByCanonical);
    }

    private static void AppendImplBlocksForType(
        List<string> lines,
        string qualifiedName,
        string simpleName,
        IReadOnlyDictionary<string, List<RustImplBlockInfo>> implsByCanonical)
    {
        var canonical = string.IsNullOrWhiteSpace(qualifiedName) ? simpleName : qualifiedName;
        if (!implsByCanonical.TryGetValue(canonical, out var implBlocks))
        {
            return;
        }

        foreach (var implBlock in implBlocks.OrderBy(i => i.ByteRange.StartByte))
        {
            AppendImplBlock(lines, implBlock, qualifiedName, indent: "  ", includeHeader: !string.IsNullOrWhiteSpace(implBlock.TraitName));
        }
    }

    private static void AppendImplBlock(
        List<string> lines,
        RustImplBlockInfo implBlock,
        string declaringType,
        string indent,
        bool includeHeader)
    {
        var traitName = NormalizeOptionalTypeName(implBlock.TraitName);
        var memberIndent = indent;

        if (includeHeader)
        {
            var traitLabel = string.IsNullOrWhiteSpace(traitName) ? "(unknown)" : traitName;
            lines.Add($"{indent}impl {traitLabel}    #symbol={declaringType}::{traitLabel}");
            memberIndent = $"{indent}  ";
        }
        else if (string.IsNullOrWhiteSpace(traitName) && string.IsNullOrWhiteSpace(indent))
        {
            var targetType = NormalizeTypeName(implBlock.TargetType);
            lines.Add($"impl {targetType}    #symbol={targetType}");
            memberIndent = "  ";
        }
        else if (!string.IsNullOrWhiteSpace(traitName) && string.IsNullOrWhiteSpace(indent))
        {
            var targetType = NormalizeTypeName(implBlock.TargetType);
            lines.Add($"impl {traitName} for {targetType}    #symbol={targetType}::{traitName}");
            memberIndent = "  ";
        }

        foreach (var associatedType in implBlock.AssociatedTypes.OrderBy(t => t.ByteRange.StartByte))
        {
            var bounds = string.IsNullOrWhiteSpace(associatedType.Bounds) ? string.Empty : $": {associatedType.Bounds}";
            var defaultType = string.IsNullOrWhiteSpace(associatedType.DefaultType) ? string.Empty : $" = {associatedType.DefaultType}";
            lines.Add($"{memberIndent}type {associatedType.Name}{bounds}{defaultType}");
        }

        foreach (var associatedConst in implBlock.AssociatedConsts.OrderBy(c => c.ByteRange.StartByte))
        {
            var constType = string.IsNullOrWhiteSpace(associatedConst.ConstType) ? string.Empty : $": {associatedConst.ConstType}";
            var hasDefault = associatedConst.HasDefault ? " = <default>" : string.Empty;
            lines.Add($"{memberIndent}const {associatedConst.Name}{constType}{hasDefault}");
        }

        foreach (var method in implBlock.Methods.OrderBy(m => m.ByteRange.StartByte))
        {
            AppendDocComment(lines, memberIndent, method.DocComment);
            lines.Add($"{memberIndent}{VisibilitySymbol(method.Visibility)}{BuildMethodSignature(method, includeVisibilityKeyword: false)}    #symbol={declaringType}.{method.Name}");
        }
    }

    private static IEnumerable<TypeEntry> EnumerateTypes(RustDocumentSurface surface)
    {
        foreach (var item in surface.Structs)
        {
            var canonical = string.IsNullOrWhiteSpace(item.QualifiedName) ? item.Name : item.QualifiedName;
            yield return new TypeEntry(canonical, item.Name, item.QualifiedName);
        }

        foreach (var item in surface.Enums)
        {
            var canonical = string.IsNullOrWhiteSpace(item.QualifiedName) ? item.Name : item.QualifiedName;
            yield return new TypeEntry(canonical, item.Name, item.QualifiedName);
        }

        foreach (var item in surface.Traits)
        {
            var canonical = string.IsNullOrWhiteSpace(item.QualifiedName) ? item.Name : item.QualifiedName;
            yield return new TypeEntry(canonical, item.Name, item.QualifiedName);
        }

        foreach (var item in surface.Unions)
        {
            var canonical = string.IsNullOrWhiteSpace(item.QualifiedName) ? item.Name : item.QualifiedName;
            yield return new TypeEntry(canonical, item.Name, item.QualifiedName);
        }

        foreach (var item in surface.TypeAliases)
        {
            var canonical = string.IsNullOrWhiteSpace(item.QualifiedName) ? item.Name : item.QualifiedName;
            yield return new TypeEntry(canonical, item.Name, item.QualifiedName);
        }
    }

    private static void RegisterCanonical(
        IDictionary<string, string> map,
        string canonical,
        string? simpleName,
        string? qualifiedName)
    {
        foreach (var key in BuildLookupKeys(simpleName))
        {
            map.TryAdd(key, canonical);
        }

        foreach (var key in BuildLookupKeys(qualifiedName))
        {
            map.TryAdd(key, canonical);
        }
    }

    private static string? ResolveCanonicalType(
        IReadOnlyDictionary<string, string> lookup,
        string targetType)
    {
        foreach (var key in BuildLookupKeys(targetType))
        {
            if (lookup.TryGetValue(key, out var canonical))
            {
                return canonical;
            }
        }

        return null;
    }

    private static JsonArray BuildFieldArray(IReadOnlyList<RustFieldInfo> fields)
    {
        var array = new JsonArray();
        foreach (var field in fields)
        {
            var item = new JsonObject
            {
                ["name"] = field.Name,
                ["type"] = field.FieldType,
                ["accessibility"] = field.Visibility
            };

            if (!string.IsNullOrWhiteSpace(field.DocComment))
            {
                item["doc"] = field.DocComment;
            }

            array.Add(item);
        }

        return array;
    }

    private static JsonArray BuildVariantArray(IReadOnlyList<RustEnumVariantInfo> variants)
    {
        var array = new JsonArray();
        foreach (var variant in variants)
        {
            var fields = new JsonArray();
            foreach (var field in variant.Fields)
            {
                fields.Add(new JsonObject
                {
                    ["name"] = field.Name,
                    ["type"] = field.FieldType,
                    ["accessibility"] = field.Visibility
                });
            }

            var item = new JsonObject
            {
                ["name"] = variant.Name,
                ["variant_kind"] = variant.VariantKind,
                ["fields"] = fields
            };

            if (!string.IsNullOrWhiteSpace(variant.Discriminant))
            {
                item["discriminant"] = variant.Discriminant;
            }

            if (!string.IsNullOrWhiteSpace(variant.DocComment))
            {
                item["doc"] = variant.DocComment;
            }

            array.Add(item);
        }

        return array;
    }

    private static JsonArray BuildAssociatedTypesArray(IReadOnlyList<RustAssociatedTypeInfo> associatedTypes)
    {
        var array = new JsonArray();
        foreach (var associatedType in associatedTypes)
        {
            var item = new JsonObject
            {
                ["name"] = associatedType.Name
            };

            if (!string.IsNullOrWhiteSpace(associatedType.Bounds))
            {
                item["bounds"] = associatedType.Bounds;
            }

            if (!string.IsNullOrWhiteSpace(associatedType.DefaultType))
            {
                item["default_type"] = associatedType.DefaultType;
            }

            array.Add(item);
        }

        return array;
    }

    private static JsonArray BuildAssociatedConstsArray(IReadOnlyList<RustAssociatedConstInfo> associatedConsts)
    {
        var array = new JsonArray();
        foreach (var associatedConst in associatedConsts)
        {
            var item = new JsonObject
            {
                ["name"] = associatedConst.Name,
                ["has_default"] = associatedConst.HasDefault
            };

            if (!string.IsNullOrWhiteSpace(associatedConst.ConstType))
            {
                item["const_type"] = associatedConst.ConstType;
            }

            array.Add(item);
        }

        return array;
    }

    private static bool IsPublic(string? accessibility)
        => string.Equals(accessibility, "public", StringComparison.Ordinal);

    private static char VisibilitySymbol(string? accessibility)
    {
        if (string.IsNullOrWhiteSpace(accessibility))
        {
            return '-';
        }

        return accessibility switch
        {
            "public" => '+',
            "pub_crate" => '~',
            "pub_super" => '#',
            _ when accessibility.StartsWith("pub_in:", StringComparison.Ordinal) => '#',
            _ => '-'
        };
    }

    private static string? VisibilityKeyword(string? accessibility)
    {
        if (string.IsNullOrWhiteSpace(accessibility))
        {
            return null;
        }

        if (string.Equals(accessibility, "public", StringComparison.Ordinal))
        {
            return "pub";
        }

        if (string.Equals(accessibility, "pub_crate", StringComparison.Ordinal))
        {
            return "pub(crate)";
        }

        if (string.Equals(accessibility, "pub_super", StringComparison.Ordinal))
        {
            return "pub(super)";
        }

        if (accessibility.StartsWith("pub_in:", StringComparison.Ordinal))
        {
            return $"pub(in {accessibility["pub_in:".Length..]})";
        }

        return null;
    }

    private static void AppendDocComment(List<string> lines, string indent, string? docComment)
    {
        if (string.IsNullOrWhiteSpace(docComment))
        {
            return;
        }

        foreach (var rawLine in docComment.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            lines.Add($"{indent}/// {line}");
        }
    }

    private static string BuildStructDeclaration(RustStructInfo info)
    {
        var generics = string.IsNullOrWhiteSpace(info.Generics) ? string.Empty : info.Generics;
        var whereClause = string.IsNullOrWhiteSpace(info.WhereClause) ? string.Empty : $" {info.WhereClause}";
        return $"struct {info.Name}{generics}{whereClause}";
    }

    private static string BuildEnumDeclaration(RustEnumInfo info)
    {
        var generics = string.IsNullOrWhiteSpace(info.Generics) ? string.Empty : info.Generics;
        var whereClause = string.IsNullOrWhiteSpace(info.WhereClause) ? string.Empty : $" {info.WhereClause}";
        return $"enum {info.Name}{generics}{whereClause}";
    }

    private static string BuildTraitDeclaration(RustTraitInfo info)
    {
        var prefixes = new List<string>();
        if (info.IsUnsafe)
            prefixes.Add("unsafe");
        if (info.IsAuto)
            prefixes.Add("auto");
        prefixes.Add("trait");

        var generics = string.IsNullOrWhiteSpace(info.Generics) ? string.Empty : info.Generics;
        var supertraits = string.IsNullOrWhiteSpace(info.Supertraits) ? string.Empty : $": {info.Supertraits}";
        var whereClause = string.IsNullOrWhiteSpace(info.WhereClause) ? string.Empty : $" {info.WhereClause}";
        return $"{string.Join(" ", prefixes)} {info.Name}{generics}{supertraits}{whereClause}";
    }

    private static string BuildUnionDeclaration(RustUnionInfo info)
    {
        var generics = string.IsNullOrWhiteSpace(info.Generics) ? string.Empty : info.Generics;
        return $"union {info.Name}{generics}";
    }

    private static string BuildTypeAliasDeclaration(RustTypeAliasInfo info)
    {
        var generics = string.IsNullOrWhiteSpace(info.Generics) ? string.Empty : info.Generics;
        var aliasedType = string.IsNullOrWhiteSpace(info.AliasedType) ? "_" : info.AliasedType;
        return $"type {info.Name}{generics} = {aliasedType}";
    }

    private static string BuildVariantSignature(RustEnumVariantInfo variant)
    {
        string rendered;
        if (string.Equals(variant.VariantKind, "tuple", StringComparison.Ordinal))
        {
            var tupleTypes = variant.Fields
                .Select(f => string.IsNullOrWhiteSpace(f.FieldType) ? "_" : f.FieldType)
                .ToArray();
            rendered = $"{variant.Name}({string.Join(", ", tupleTypes)})";
        }
        else if (string.Equals(variant.VariantKind, "struct", StringComparison.Ordinal))
        {
            var fields = variant.Fields
                .Select(f =>
                {
                    var fieldType = string.IsNullOrWhiteSpace(f.FieldType) ? "_" : f.FieldType;
                    return $"{f.Name}: {fieldType}";
                })
                .ToArray();
            rendered = $"{variant.Name} {{ {string.Join(", ", fields)} }}";
        }
        else
        {
            rendered = variant.Name;
        }

        if (!string.IsNullOrWhiteSpace(variant.Discriminant))
        {
            rendered = $"{rendered} = {variant.Discriminant}";
        }

        return rendered;
    }

    private static string BuildMethodSignature(RustMethodInfo method, bool includeVisibilityKeyword)
    {
        var modifiers = new List<string>();
        if (includeVisibilityKeyword)
        {
            var visibilityKeyword = VisibilityKeyword(method.Visibility);
            if (!string.IsNullOrWhiteSpace(visibilityKeyword))
            {
                modifiers.Add(visibilityKeyword);
            }
        }

        if (method.IsAsync)
            modifiers.Add("async");
        if (method.IsUnsafe)
            modifiers.Add("unsafe");
        if (method.IsConst)
            modifiers.Add("const");

        var modifierPart = modifiers.Count == 0 ? string.Empty : $"{string.Join(" ", modifiers)} ";
        var parameters = NormalizeParameters(method.Parameters);
        var returnType = NormalizeReturnType(method.ReturnType);
        return string.IsNullOrWhiteSpace(returnType)
            ? $"{modifierPart}fn {method.Name}{parameters}"
            : $"{modifierPart}fn {method.Name}{parameters} {returnType}";
    }

    private static string BuildFunctionSignature(RustFunctionInfo function, bool includeVisibilityKeyword)
    {
        var modifiers = new List<string>();
        if (includeVisibilityKeyword)
        {
            var visibilityKeyword = VisibilityKeyword(function.Visibility);
            if (!string.IsNullOrWhiteSpace(visibilityKeyword))
            {
                modifiers.Add(visibilityKeyword);
            }
        }

        if (function.IsAsync)
            modifiers.Add("async");
        if (function.IsUnsafe)
            modifiers.Add("unsafe");
        if (function.IsConst)
            modifiers.Add("const");

        var modifierPart = modifiers.Count == 0 ? string.Empty : $"{string.Join(" ", modifiers)} ";
        var generics = string.IsNullOrWhiteSpace(function.Generics) ? string.Empty : function.Generics;
        var parameters = NormalizeParameters(function.Parameters);
        var returnType = NormalizeReturnType(function.ReturnType);
        return string.IsNullOrWhiteSpace(returnType)
            ? $"{modifierPart}fn {function.Name}{generics}{parameters}"
            : $"{modifierPart}fn {function.Name}{generics}{parameters} {returnType}";
    }

    private static string BuildModuleDeclaration(RustModuleInfo module)
        => module.IsInline ? $"mod {module.Name}" : $"mod {module.Name};";

    private static string NormalizeParameters(string? parameters)
    {
        var text = parameters?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return "()";
        }

        if (text.StartsWith('(') && text.EndsWith(')'))
        {
            return text;
        }

        return $"({text})";
    }

    private static string? NormalizeReturnType(string? returnType)
    {
        if (string.IsNullOrWhiteSpace(returnType))
        {
            return null;
        }

        var normalized = NormalizeWhitespace(returnType);
        return normalized.StartsWith("->", StringComparison.Ordinal) ? normalized : $"-> {normalized}";
    }

    private static string BuildQualifiedName(string declaringType, string memberName)
    {
        if (string.IsNullOrWhiteSpace(declaringType))
        {
            return memberName;
        }

        return $"{declaringType}.{memberName}";
    }

    private static string ExtractSimpleTypeName(string qualifiedName)
    {
        var value = qualifiedName;
        var lastPath = value.LastIndexOf("::", StringComparison.Ordinal);
        if (lastPath >= 0 && lastPath + 2 < value.Length)
        {
            return value[(lastPath + 2)..];
        }

        var lastDot = value.LastIndexOf('.');
        return lastDot >= 0 && lastDot + 1 < value.Length ? value[(lastDot + 1)..] : value;
    }

    private static string NormalizeTypeName(string? rawType)
    {
        if (string.IsNullOrWhiteSpace(rawType))
        {
            return string.Empty;
        }

        var normalized = NormalizeWhitespace(rawType);
        while (normalized.StartsWith('&'))
        {
            normalized = normalized[1..].TrimStart();
        }

        if (normalized.StartsWith("mut ", StringComparison.Ordinal))
        {
            normalized = normalized[4..].TrimStart();
        }

        if (normalized.StartsWith("dyn ", StringComparison.Ordinal))
        {
            normalized = normalized[4..].TrimStart();
        }

        normalized = StripGenericArguments(normalized);
        normalized = StripWrappingParentheses(normalized);
        if (normalized.StartsWith("crate::", StringComparison.Ordinal))
        {
            normalized = normalized["crate::".Length..];
        }

        return NormalizeWhitespace(normalized);
    }

    private static string? NormalizeOptionalTypeName(string? rawType)
    {
        var normalized = NormalizeTypeName(rawType);
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static string? NormalizeOptionalTarget(string? rawTarget)
    {
        if (string.IsNullOrWhiteSpace(rawTarget))
        {
            return null;
        }

        var normalized = NormalizeWhitespace(rawTarget);
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static string StripGenericArguments(string value)
    {
        if (value.IndexOf('<') < 0)
        {
            return value;
        }

        var builder = new StringBuilder(value.Length);
        var depth = 0;
        foreach (var ch in value)
        {
            if (ch == '<')
            {
                depth++;
                continue;
            }

            if (ch == '>')
            {
                depth = Math.Max(0, depth - 1);
                continue;
            }

            if (depth == 0)
            {
                builder.Append(ch);
            }
        }

        return builder.ToString();
    }

    private static string StripWrappingParentheses(string value)
    {
        var current = value.Trim();
        while (current.Length > 1
               && current.StartsWith('(')
               && current.EndsWith(')'))
        {
            current = current[1..^1].Trim();
        }

        return current;
    }

    private static string NormalizeWhitespace(string value)
        => string.Join(" ", value.Split(default(string[]), StringSplitOptions.RemoveEmptyEntries));

    private static void SetIfNotEmpty(JsonObject props, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            props[key] = value;
        }
    }

    private static string ReadEmbeddedResource(string resourceName)
    {
        using var stream = typeof(RustLoader).Assembly.GetManifestResourceStream(resourceName)
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

    private sealed record ChildEntry(int StartByte, Node Node);

    private sealed record ImportEntry(string Path, string? Alias, bool IsGlob);

    private sealed record DecoratedNodeEntry(int StartByte, int EndByte, Node Node);

    private sealed record TypeEntry(string CanonicalKey, string Name, string QualifiedName);

    private sealed record StructureRootEntry(
        int StartByte,
        RustStructInfo? Struct = null,
        RustEnumInfo? Enum = null,
        RustTraitInfo? Trait = null,
        RustUnionInfo? Union = null,
        RustTypeAliasInfo? TypeAlias = null,
        RustFunctionInfo? Function = null,
        RustModuleInfo? Module = null,
        RustConstantInfo? Constant = null,
        RustStaticInfo? Static = null,
        RustImplBlockInfo? Impl = null);
}
