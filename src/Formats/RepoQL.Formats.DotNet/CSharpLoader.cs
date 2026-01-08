using System.Text;
using System.Text.Json.Nodes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RepoQL.Contracts;
using RepoQL.Contracts.Models;

namespace RepoQL.Formats.DotNet;

/// <summary>
/// Loads and materializes C# source files into RepoQL's graph structure.
/// Performs both syntactic and semantic analysis using Roslyn.
/// </summary>
/// <remarks>
/// <para>
/// This loader operates in two modes:
/// </para>
/// <list type="number">
/// <item><description>Project-aware: Uses MSBuildWorkspace for full semantic analysis when a .csproj file is found</description></item>
/// <item><description>Standalone: Falls back to basic compilation if no project context is available</description></item>
/// </list>
/// <para>
/// The loader extracts the following information from C# files:
/// - Namespace declarations and hierarchy
/// - Type declarations (classes, structs, interfaces, records, enums)
/// - Member declarations (methods, properties, fields, events, constructors)
/// - Symbol references (method calls, type usage, field access)
/// - Compiler diagnostics (errors, warnings)
/// - Source generator outputs (when project context available)
/// </para>
/// </remarks>
public sealed class CSharpLoader : IFormatLoader, IFormatMaterializer, IFormatSchemaProvider
{
    /// <summary>
    /// Metadata key for storing C# document state in DocumentModel metadata.
    /// </summary>
    public const string StateMetadataKey = "csharp.state";

    public const string MediaKind = "code.csharp";

    internal static readonly SemanticMediaType CSharpMediaType = SemanticMediaType
        .Create("text", "plain")
        .WithKind(MediaKind);

    private static readonly CSharpParseOptions ParseOptions = new(
        languageVersion: LanguageVersion.Preview,
        documentationMode: DocumentationMode.Parse,
        kind: SourceCodeKind.Regular);

    private static readonly Lazy<string> CSharpViewsSql = new(
        () => ReadEmbeddedResource("RepoQL.Formats.DotNet.Schema.csharp_views.sql"));

    private static readonly MetadataReference[] DefaultReferences = CreateDefaultReferences();
    private readonly CSharpWorkspaceHost _workspaceHost;
    private readonly ILogger<CSharpLoader> _logger;
    private readonly bool _analysisEnabled;

    /// <summary>
    /// Initializes a new instance of the <see cref="CSharpLoader"/> class with default settings.
    /// </summary>
    /// <remarks>
    /// Creates a new workspace host instance internally. For production use with dependency injection,
    /// prefer using the constructor that accepts CSharpWorkspaceHost to share a singleton instance.
    /// </remarks>
    public CSharpLoader()
        : this(null, null, null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="CSharpLoader"/> class with the specified workspace host.
    /// </summary>
    /// <param name="workspaceHost">Workspace host for project-aware analysis. If null, creates a new instance (not recommended for production).</param>
    public CSharpLoader(CSharpWorkspaceHost? workspaceHost)
        : this(workspaceHost, null, null)
    {
    }

    /// <summary>
    /// Initializes a new instance with a workspace host and configuration.
    /// </summary>
    public CSharpLoader(CSharpWorkspaceHost? workspaceHost, IConfiguration? configuration)
        : this(workspaceHost, configuration, null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="CSharpLoader"/> class with the specified workspace host and logger.
    /// </summary>
    /// <param name="workspaceHost">Workspace host for project-aware analysis. If null, creates a new instance (not recommended for production).</param>
    /// <param name="logger">Optional logger for diagnostic information. Uses null logger if null.</param>
    public CSharpLoader(CSharpWorkspaceHost? workspaceHost, IConfiguration? configuration, ILogger<CSharpLoader>? logger)
    {
        _workspaceHost = workspaceHost ?? new CSharpWorkspaceHost();
        _logger = logger ?? NullLogger<CSharpLoader>.Instance;
        _analysisEnabled = ResolveAnalysisEnabled(configuration);
    }

    /// <summary>
    /// Determines whether this loader supports the specified media type.
    /// </summary>
    /// <param name="mediaType">The media type to check.</param>
    /// <returns><c>true</c> if the media type is C# code (code.csharp); otherwise, <c>false</c>.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="mediaType"/> is null.</exception>
    public bool Supports(SemanticMediaType mediaType)
    {
        ArgumentNullException.ThrowIfNull(mediaType);
        return string.Equals(mediaType.Kind, CSharpMediaType.Kind, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Determines whether this loader can load the specified artifact.
    /// </summary>
    /// <param name="artifact">The artifact to check.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// <c>true</c> if the artifact has a .cs extension or C# media type; otherwise, <c>false</c>.
    /// </returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="artifact"/> is null.</exception>
    /// <remarks>
    /// This method also updates the artifact's MediaType property if it matches C# criteria.
    /// </remarks>
    public Task<bool> CanLoadAsync(DiscoveredArtifact artifact, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);

        var name = artifact.File.Name.ToLowerInvariant();
        if (name.EndsWith(".cs", StringComparison.Ordinal))
        {
            artifact.MediaType = CSharpMediaType;
            return Task.FromResult(true);
        }

        if (artifact.MediaType is not null &&
            string.Equals(artifact.MediaType.Kind, CSharpMediaType.Kind, StringComparison.OrdinalIgnoreCase))
        {
            artifact.MediaType = artifact.MediaType.WithKind(CSharpMediaType.Kind);
            return Task.FromResult(true);
        }

        return Task.FromResult(false);
    }

    /// <summary>
    /// Loads a C# document and performs syntax tree analysis.
    /// </summary>
    /// <param name="artifact">The source file to analyze.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// A document model containing parsed syntax and semantic information.
    /// Semantic information may be limited if project context is unavailable.
    /// </returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="artifact"/> is null.</exception>
    /// <exception cref="InvalidOperationException">Thrown if the artifact has no RepoUri.</exception>
    public async Task<DocumentModel> LoadAsync(DiscoveredArtifact artifact, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        if (artifact.RepoUri is null)
        {
            var errorMessage = $"RepoUri is required to load C# documents. Artifact: file={artifact.File?.Name ?? "unknown"}, mediaType={artifact.MediaType?.Kind ?? "null"}";
            _logger.LogError(errorMessage);
            throw new InvalidOperationException(errorMessage);
        }

        var loaded = await FileContentReader.ReadAllTextWithDigestAsync(
            artifact.File,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var text = loaded.Text;
        var mediaType = artifact.MediaType ?? CSharpMediaType;
        var lineMap = new TextLineMap(text);

        var syntaxTree = CSharpSyntaxTree.ParseText(
            text,
            ParseOptions,
            path: artifact.RepoUri.IsFile ? artifact.RepoUri.LocalPath : artifact.RepoUri.AbsoluteUri,
            encoding: Encoding.UTF8,
            cancellationToken: cancellationToken);

        var root = await syntaxTree.GetRootAsync(cancellationToken).ConfigureAwait(false);
        var documentId = CSharpIdFactory.CreateDocumentId(artifact.RepoUri);
        var walker = new CSharpInventoryWalker(documentId, lineMap);
        walker.Visit(root);

        var documentProps = BuildDocumentProperties(lineMap, walker, artifact.RepoUri);

        var surface = new CSharpDocumentSurface
        {
            DocumentId = documentId,
            DocumentProperties = documentProps,
            Namespaces = walker.Namespaces,
            Types = walker.Types,
            Members = walker.Members,
            Usings = walker.Usings
        };

        var filePath = TryGetPhysicalPath(artifact);
        var semantic = await AnnotateSemanticInfoAsync(filePath, surface, walker, syntaxTree, root, lineMap, cancellationToken).ConfigureAwait(false);

        var state = new CSharpDocumentState
        {
            DocumentId = documentId,
            Digest = loaded.Digest,
            Size = loaded.ByteLength,
            MediaType = mediaType,
            StoreUri = artifact.RepoUri.ToString(),
            Surface = surface,
            References = semantic.References,
            Diagnostics = semantic.Diagnostics,
            GeneratedDocuments = semantic.GeneratedDocuments
        };

        var metadata = new Dictionary<string, object?>
        {
            [StateMetadataKey] = state
        };

        return new DocumentModel(artifact.RepoUri, mediaType, text, syntaxTree, metadata);
    }

    /// <summary>
    /// Materializes a C# document model into RepoQL's graph records (artifacts, nodes, spans, edges).
    /// </summary>
    /// <param name="document">The document model containing C# syntax and semantic information.</param>
    /// <returns>
    /// A <see cref="Records"/> instance containing the materialized graph structure.
    /// </returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="document"/> is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown if the document does not contain C# loader state metadata.
    /// This typically means the document was not loaded by <see cref="LoadAsync"/>.
    /// </exception>
    public Records Materialize(DocumentModel document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var state = document.GetMetadataOrDefault<CSharpDocumentState>(StateMetadataKey)
                    ?? throw new InvalidOperationException(
                        $"C# materializer requires loader state metadata (key: {StateMetadataKey}). " +
                        $"Ensure the document was loaded using CSharpLoader.LoadAsync. " +
                        $"Document URI: {document.Uri}");
        var artifacts = new List<Artifact>();
        var nodes = new List<Node>();
        var spans = new List<Span>();
        var edges = new List<Edge>();
        var ordinals = new Dictionary<Guid, int>();
        var seenCompositionChildren = new HashSet<Guid>();
        var duplicateCompositionCount = 0;

        int NextOrdinal(Guid parentId)
        {
            if (!ordinals.TryGetValue(parentId, out var current))
            {
                ordinals[parentId] = 0;
                return 0;
            }
            current++;
            ordinals[parentId] = current;
            return current;
        }

        void AddComposition(Guid scopeDocumentId, Guid parentId, Guid childId)
        {
            // Each child can only have one parent - skip duplicates
            // This can happen with partial classes across main and generated documents
            if (!seenCompositionChildren.Add(childId))
            {
                duplicateCompositionCount++;
                return;
            }

            edges.Add(new Edge
            {
                SrcId = parentId,
                DstId = childId,
                Type = "HAS_PART",
                IsComposition = true,
                Ordinal = NextOrdinal(parentId),
                ScopeDocumentId = scopeDocumentId
            });
        }

        void EmitDocument(
            RepoUri uri,
            string text,
            SemanticMediaType mediaType,
            string digest,
            long size,
            CSharpDocumentSurface surface,
            IReadOnlyList<CSharpSymbolReference> references)
        {
            var tokenCount = TokenEstimator.EstimateTokensSafe(text);
            var artifact = new Artifact
            {
                Digest = digest,
                Size = size,
                MediaType = mediaType,
                Text = text,
                StoreUri = uri,
                TokenCount = tokenCount,
                Headline = BuildHeadline(uri, surface, tokenCount),
                Summary = BuildSummary(surface),
                Structure = BuildStructure(surface)
            };

            artifacts.Add(artifact);

            nodes.Add(new Node
            {
                Id = surface.DocumentId,
                Kind = "document",
                Uri = uri,
                ArtifactId = artifact.Id,
                Props = surface.DocumentProperties
            });

            // Build member lookup for type structure generation
            var membersByType = surface.Members
                .GroupBy(m => m.DeclaringTypeId)
                .ToDictionary(g => g.Key, g => g.ToList());

            foreach (var type in surface.Types)
            {
                spans.Add(CreateSpan(type.SpanId, type.Span, surface.DocumentId));
                var typeProps = new JsonObject
                {
                    ["name"] = type.Name,
                    ["qualified_name"] = type.QualifiedName,
                    ["kind"] = type.Kind,
                    ["namespace"] = type.Namespace ?? string.Empty,
                    ["accessibility"] = type.Accessibility,
                    ["is_partial"] = type.IsPartial,
                    ["is_static"] = type.IsStatic,
                    ["is_record"] = type.IsRecord
                };
                if (!string.IsNullOrWhiteSpace(type.BaseType))
                    typeProps["extends"] = type.BaseType;
                if (type.Interfaces.Count > 0)
                {
                    var implements = new JsonArray();
                    foreach (var iface in type.Interfaces)
                    {
                        implements.Add((JsonNode?)JsonValue.Create(iface));
                    }
                    typeProps["implements"] = implements;
                }
                if (!string.IsNullOrEmpty(type.SymbolKey))
                    typeProps["symbol_key"] = type.SymbolKey;

                var signature = BuildTypeHeadline(type);
                typeProps["signature"] = signature;

                nodes.Add(new Node
                {
                    Id = type.NodeId,
                    Kind = "csharp.type",
                    SpanId = type.SpanId,
                    Uri = RepoUri.FromSymbol(uri.Container, type.QualifiedName, type.Span.StartLine, type.Span.EndLine),
                    Props = typeProps,
                    Headline = signature,
                    Structure = BuildTypeStructure(type, membersByType)
                });

                // Nested types -> parent type, top-level types -> document
                var parent = type.ParentTypeId ?? surface.DocumentId;
                AddComposition(surface.DocumentId, parent, type.NodeId);
            }

            foreach (var member in surface.Members)
            {
                spans.Add(CreateSpan(member.SpanId, member.Span, surface.DocumentId));
                var memberProps = new JsonObject
                {
                    ["name"] = member.Name,
                    ["kind"] = member.Kind,
                    ["accessibility"] = member.Accessibility,
                    ["is_static"] = member.IsStatic,
                    ["is_async"] = member.IsAsync,
                    ["return_type"] = member.ReturnType ?? string.Empty,
                    ["declaring_type"] = member.DeclaringTypeDisplay ?? string.Empty
                };

                if (member.Parameters.Count > 0)
                {
                    var arr = new JsonArray();
                    foreach (var parameter in member.Parameters)
                    {
                        var parameterNode = new JsonObject
                        {
                            ["name"] = parameter.Name,
                            ["type"] = parameter.Type,
                            ["has_default"] = parameter.HasDefaultValue
                        };
                        arr.Add((JsonNode)parameterNode);
                    }
                    memberProps["parameters"] = arr;
                }
                if (!string.IsNullOrEmpty(member.SymbolKey))
                    memberProps["symbol_key"] = member.SymbolKey;

                var memberSymbol = string.IsNullOrEmpty(member.DeclaringTypeDisplay)
                    ? member.Name
                    : $"{member.DeclaringTypeDisplay}.{member.Name}";
                nodes.Add(new Node
                {
                    Id = member.NodeId,
                    Kind = "csharp.member",
                    SpanId = member.SpanId,
                    Uri = RepoUri.FromSymbol(uri.Container, memberSymbol, member.Span.StartLine, member.Span.EndLine),
                    Props = memberProps,
                    Headline = BuildMemberHeadline(member)
                });

                AddComposition(surface.DocumentId, member.DeclaringTypeId, member.NodeId);
            }

            foreach (var reference in references)
            {
                if (reference.TargetNodeId is null)
                    continue;

                // Create deterministic span ID based on position (not random GUID)
                var textSpan = new TextSpan(reference.Span.StartChar, reference.Span.Length);
                var spanId = CSharpIdFactory.CreateSpanId(surface.DocumentId, "symbol_reference", textSpan);
                spans.Add(CreateSpan(spanId, reference.Span, surface.DocumentId));
                var props = new JsonObject
                {
                    ["symbol_key"] = reference.SymbolKey,
                    ["symbol_kind"] = reference.SymbolKind ?? string.Empty,
                    ["status"] = "local"
                };

                edges.Add(new Edge
                {
                    SrcId = reference.SourceNodeId,
                    DstId = reference.TargetNodeId.Value,
                    Type = "USES_SYMBOL",
                    IsComposition = false,
                    ScopeDocumentId = surface.DocumentId,
                    SrcSpanId = spanId,
                    Props = props
                });
            }
        }

        EmitDocument(
            document.Uri,
            document.Text,
            state.MediaType,
            state.Digest,
            state.Size,
            state.Surface,
            state.References);

        foreach (var generated in state.GeneratedDocuments)
        {
            var generatedUri = RepoUri.Parse(generated.StoreUri);
            EmitDocument(
                generatedUri,
                generated.Text,
                generated.MediaType,
                generated.Digest,
                generated.Size,
                generated.Surface,
                generated.References);
        }

        if (duplicateCompositionCount > 0)
        {
            _logger.LogWarning("Skipped {Count} duplicate composition edges in {Uri} (same child with multiple parents)",
                duplicateCompositionCount, document.Uri);
        }

        return new Records
        {
            Artifacts = artifacts.ToArray(),
            Nodes = nodes.ToArray(),
            Spans = spans.ToArray(),
            Edges = edges.ToArray()
        };
    }

    private sealed record SemanticInfo(
        IReadOnlyList<CSharpSymbolReference> References,
        IReadOnlyList<CSharpDiagnostic> Diagnostics,
        IReadOnlyList<CSharpGeneratedDocumentState> GeneratedDocuments);

    private async Task<SemanticInfo> AnnotateSemanticInfoAsync(
        string? filePath,
        CSharpDocumentSurface surface,
        CSharpInventoryWalker walker,
        SyntaxTree syntaxTree,
        SyntaxNode root,
        TextLineMap lineMap,
        CancellationToken cancellationToken)
    {
        if (_analysisEnabled && !string.IsNullOrWhiteSpace(filePath))
        {
            var projectAnalysis = await _workspaceHost.TryAnalyzeAsync(filePath, surface, lineMap, cancellationToken).ConfigureAwait(false);
            if (projectAnalysis is not null)
                return new SemanticInfo(projectAnalysis.References, projectAnalysis.Diagnostics, projectAnalysis.GeneratedDocuments);
        }

        try
        {
            var compilation = CSharpCompilation.Create(
                $"RepoQL.CSharp.{Guid.NewGuid():N}",
                new[] { syntaxTree },
                DefaultReferences,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

            var semanticModel = compilation.GetSemanticModel(syntaxTree);

            if (surface.Types is List<CSharpTypeInfo> typeList)
            {
                for (var i = 0; i < typeList.Count; i++)
                {
                    if (!walker.TypeDeclarations.TryGetValue(typeList[i].NodeId, out var typeSyntax))
                        continue;
                    var symbol = semanticModel.GetDeclaredSymbol(typeSyntax, cancellationToken);
                    if (symbol is null) continue;
                    var key = CSharpSemanticUtilities.BuildSymbolKey(symbol);
                    typeList[i] = typeList[i] with { SymbolKey = key };
                }
            }

            if (surface.Members is List<CSharpMemberInfo> memberList)
            {
                for (var i = 0; i < memberList.Count; i++)
                {
                    if (!walker.MemberDeclarations.TryGetValue(memberList[i].NodeId, out var memberSyntax))
                        continue;
                    var symbol = semanticModel.GetDeclaredSymbol(memberSyntax, cancellationToken);
                    if (symbol is null) continue;
                    var key = CSharpSemanticUtilities.BuildSymbolKey(symbol);
                    memberList[i] = memberList[i] with { SymbolKey = key };
                }
            }

            var collector = new SymbolReferenceCollector(
                semanticModel,
                walker.DeclaredNodeIds,
                lineMap,
                surface.DocumentId);
            collector.Visit(root);
            var diagnostics = CollectDiagnostics(compilation, syntaxTree, lineMap, cancellationToken);
            if (!_analysisEnabled)
            {
                diagnostics = diagnostics
                    .Where(d => !SuppressedWhenAnalysisDisabled.Contains(d.Id, StringComparer.OrdinalIgnoreCase))
                    .ToArray();
            }

            return new SemanticInfo(collector.References, diagnostics, Array.Empty<CSharpGeneratedDocumentState>());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Standalone semantic analysis failed for document {DocumentId}. Returning empty semantic info. " +
                "This may occur when references are missing or code contains errors.",
                surface.DocumentId);
            return new SemanticInfo(
                Array.Empty<CSharpSymbolReference>(),
                Array.Empty<CSharpDiagnostic>(),
                Array.Empty<CSharpGeneratedDocumentState>());
        }
    }

    private static IReadOnlyList<CSharpDiagnostic> CollectDiagnostics(CSharpCompilation compilation, SyntaxTree syntaxTree, TextLineMap lineMap, CancellationToken cancellationToken)
    {
        var diagnostics = new List<CSharpDiagnostic>();
        foreach (var diag in compilation.GetDiagnostics(cancellationToken))
        {
            if (!diag.Location.IsInSource)
                continue;
            if (!ReferenceEquals(diag.Location.SourceTree, syntaxTree))
                continue;

            var span = diag.Location.SourceSpan;
            var docSpan = lineMap.GetSpan(span.Start, span.End);
            diagnostics.Add(new CSharpDiagnostic(
                Id: diag.Id,
                Message: diag.GetMessage(),
                Severity: diag.Severity.ToString(),
                Category: diag.Descriptor.Category ?? string.Empty,
                HelpLink: diag.Descriptor.HelpLinkUri,
                Span: docSpan));
        }
        return diagnostics;
    }

    /// <summary>
    /// Gets SQL scripts for creating C#-specific database views.
    /// </summary>
    /// <returns>
    /// A sequence of SQL scripts that create views over the graph data for C#-specific queries.
    /// </returns>
    /// <remarks>
    /// These views provide convenient SQL access to C# constructs like namespaces, types, and members.
    /// </remarks>
    public IEnumerable<FormatSqlScript> GetSchemaScripts()
    {
        yield return new FormatSqlScript("csharp_views", CSharpViewsSql.Value);
    }

    private static string? TryGetPhysicalPath(DiscoveredArtifact artifact)
    {
        if (!string.IsNullOrWhiteSpace(artifact.File.PhysicalPath))
            return artifact.File.PhysicalPath;
        if (artifact.RepoUri is not null && artifact.RepoUri.IsFile)
            return artifact.RepoUri.LocalPath;
        return null;
    }

    internal static RepoUri GetRepoUriFromPath(string filePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);

        // Check if it's already a file:// URI
        if (Uri.TryCreate(filePath, UriKind.Absolute, out var absolute) &&
            !string.IsNullOrEmpty(absolute.Scheme) &&
            absolute.Scheme.Equals("file", StringComparison.OrdinalIgnoreCase))
        {
            return RepoUri.Parse(absolute.AbsoluteUri);
        }

        // Handle rooted paths (Unix: /foo/bar or Windows: \foo\bar)
        if (filePath.StartsWith('/') || filePath.StartsWith('\\'))
        {
            var normalized = filePath.Replace('\\', '/');
            if (!normalized.StartsWith('/'))
                normalized = "/" + normalized;
            return RepoUri.Parse($"file://{normalized}");
        }

        // All other paths: convert to absolute file system path, then to file:// URI
        var fullPath = Path.GetFullPath(filePath);
        return RepoUri.Parse(new Uri(fullPath).AbsoluteUri);
    }

    internal static JsonObject BuildDocumentProperties(TextLineMap lineMap, CSharpInventoryWalker walker, RepoUri uri)
    {
        var docProps = new JsonObject
        {
            ["language"] = "csharp",
            ["file_name"] = GetFileName(uri),
            ["line_count"] = lineMap.LineCount,
            ["namespace_count"] = walker.Namespaces.Count,
            ["type_count"] = walker.Types.Count,
            ["member_count"] = walker.Members.Count,
            ["using_count"] = walker.Usings.Count,
            ["public_type_count"] = walker.Types.Count(t => string.Equals(t.Accessibility, "public", StringComparison.OrdinalIgnoreCase)),
            ["method_count"] = walker.Members.Count(m => string.Equals(m.Kind, "method", StringComparison.OrdinalIgnoreCase)),
            ["async_member_count"] = walker.Members.Count(m => m.IsAsync)
        };
        return docProps;
    }

    private static string BuildTypeHeadline(CSharpTypeInfo type)
    {
        // Format: "public class MyClass : BaseClass, IFoo"
        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(type.Accessibility))
            sb.Append(type.Accessibility).Append(' ');
        if (type.IsStatic)
            sb.Append("static ");
        if (type.IsPartial)
            sb.Append("partial ");
        sb.Append(type.Kind).Append(' ').Append(type.Name);

        if (!string.IsNullOrWhiteSpace(type.BaseType) || type.Interfaces.Count > 0)
        {
            sb.Append(" : ");
            var first = true;
            if (!string.IsNullOrWhiteSpace(type.BaseType))
            {
                sb.Append(type.BaseType);
                first = false;
            }
            foreach (var iface in type.Interfaces)
            {
                if (!first) sb.Append(", ");
                sb.Append(iface);
                first = false;
            }
        }

        return sb.ToString();
    }

    private static string BuildMemberHeadline(CSharpMemberInfo member)
    {
        // Format: "public async Task<string> GetDataAsync(int id, string name)"
        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(member.Accessibility))
            sb.Append(member.Accessibility).Append(' ');
        if (member.IsStatic)
            sb.Append("static ");
        if (member.IsAsync)
            sb.Append("async ");

        // Return type (skip for constructors)
        if (!string.Equals(member.Kind, "constructor", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(member.ReturnType))
        {
            sb.Append(member.ReturnType).Append(' ');
        }

        sb.Append(member.Name);

        // Parameters for methods/constructors/indexers
        if (string.Equals(member.Kind, "method", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(member.Kind, "constructor", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(member.Kind, "indexer", StringComparison.OrdinalIgnoreCase))
        {
            var bracket = string.Equals(member.Kind, "indexer", StringComparison.OrdinalIgnoreCase) ? '[' : '(';
            var closeBracket = bracket == '[' ? ']' : ')';
            sb.Append(bracket);
            for (var i = 0; i < member.Parameters.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(member.Parameters[i].Type).Append(' ').Append(member.Parameters[i].Name);
            }
            sb.Append(closeBracket);
        }

        return sb.ToString();
    }

    private static string BuildHeadline(RepoUri uri, CSharpDocumentSurface surface, int? tokenCount)
    {
        var fileName = GetFileName(uri);

        // Show actual type names instead of counts
        var topTypes = surface.Types
            .Take(3)
            .Select(t => $"{t.Kind} {t.Name}")
            .ToArray();

        var tokenPart = tokenCount.HasValue ? $" | {tokenCount.Value} tokens" : string.Empty;

        if (topTypes.Length == 0)
            return $"{fileName} | (empty){tokenPart}";

        var typePart = string.Join(", ", topTypes);
        if (surface.Types.Count > 3)
            typePart += $" (+{surface.Types.Count - 3} more)";

        return $"{fileName} | {typePart}{tokenPart}";
    }

    private static string BuildSummary(CSharpDocumentSurface surface)
    {
        var sb = new StringBuilder();

        // Compact counts on single line
        var publicCount = surface.Types.Count(t => string.Equals(t.Accessibility, CSharpValues.Public, StringComparison.OrdinalIgnoreCase));
        var asyncCount = surface.Members.Count(m => m.IsAsync);
        sb.Append($"ns:{surface.Namespaces.Count} types:{surface.Types.Count}");
        if (publicCount > 0) sb.Append($" pub:{publicCount}");
        sb.Append($" members:{surface.Members.Count}");
        if (asyncCount > 0) sb.Append($" async:{asyncCount}");
        sb.AppendLine();

        // Public types - short names only (FQN visible in structure)
        var topTypes = surface.Types
            .Where(t => string.Equals(t.Accessibility, CSharpValues.Public, StringComparison.OrdinalIgnoreCase))
            .OrderBy(t => t.QualifiedName)
            .Take(CSharpLoaderConstants.MaxPublicTypesInSummary)
            .ToArray();

        if (topTypes.Length > 0)
        {
            sb.AppendLine("Public API:");
            foreach (var type in topTypes)
            {
                var inheritance = !string.IsNullOrWhiteSpace(type.BaseType)
                    ? $" : {type.BaseType}"
                    : (type.Interfaces.Count > 0 ? $" : {string.Join(", ", type.Interfaces)}" : string.Empty);
                // Use short name - namespace shown in structure
                sb.AppendLine($"  {type.Kind} {type.Name}{inheritance}");
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static string BuildStructure(CSharpDocumentSurface surface)
    {
        // Symbolic notation: + public, # protected, ~ internal, - private
        // Types: +ClassName : Base, Interfaces
        // Members: +Method(params) → ReturnType, +Property → Type, -_field
        var sb = new StringBuilder();
        var membersByType = surface.Members
            .GroupBy(m => m.DeclaringTypeId)
            .ToDictionary(g => g.Key, g => g.ToList());

        static char AccessibilitySymbol(string? accessibility) => accessibility?.ToLowerInvariant() switch
        {
            "public" => '+',
            "protected" => '#',
            "internal" => '~',
            "private" => '-',
            "protected internal" => '#',
            "private protected" => '-',
            _ => ' '
        };

        void AppendType(CSharpTypeInfo type, string indent)
        {
            var symbol = AccessibilitySymbol(type.Accessibility);
            var inheritance = !string.IsNullOrWhiteSpace(type.BaseType)
                ? $" : {type.BaseType}"
                : (type.Interfaces.Count > 0 ? $" : {string.Join(", ", type.Interfaces)}" : string.Empty);
            sb.AppendLine($"{indent}{symbol}{type.Kind} {type.Name}{inheritance}");

            if (membersByType.TryGetValue(type.NodeId, out var members))
            {
                foreach (var member in members.Take(CSharpLoaderConstants.MaxMembersInStructure))
                {
                    var memberSymbol = AccessibilitySymbol(member.Accessibility);
                    var returnPart = !string.IsNullOrWhiteSpace(member.ReturnType) && member.ReturnType != "void"
                        ? $" → {member.ReturnType}"
                        : string.Empty;

                    if (string.Equals(member.Kind, "method", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(member.Kind, "constructor", StringComparison.OrdinalIgnoreCase))
                    {
                        var paramText = member.Parameters.Count == 0
                            ? "()"
                            : $"({string.Join(", ", member.Parameters.Select(p => p.Type))})";
                        sb.AppendLine($"{indent}  {memberSymbol}{member.Name}{paramText}{returnPart}");
                    }
                    else if (string.Equals(member.Kind, "property", StringComparison.OrdinalIgnoreCase))
                    {
                        sb.AppendLine($"{indent}  {memberSymbol}{member.Name}{returnPart}");
                    }
                    else // field, event, etc.
                    {
                        var typePart = !string.IsNullOrWhiteSpace(member.ReturnType)
                            ? $" : {member.ReturnType}"
                            : string.Empty;
                        sb.AppendLine($"{indent}  {memberSymbol}{member.Name}{typePart}");
                    }
                }
            }
        }

        foreach (var ns in surface.Namespaces.Take(CSharpLoaderConstants.MaxNamespacesInStructure))
        {
            sb.AppendLine(ns.QualifiedName);
            foreach (var type in surface.Types.Where(t => t.NamespaceNodeId == ns.NodeId && t.ParentTypeId is null).Take(CSharpLoaderConstants.MaxTypesPerNamespaceInStructure))
            {
                AppendType(type, "  ");
            }
        }

        var globalTypes = surface.Types.Where(t => t.NamespaceNodeId is null && t.ParentTypeId is null).Take(CSharpLoaderConstants.MaxGlobalTypesInStructure).ToArray();
        if (globalTypes.Length > 0)
        {
            sb.AppendLine("<global>");
            foreach (var type in globalTypes)
            {
                AppendType(type, "  ");
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static string? BuildTypeStructure(CSharpTypeInfo type, Dictionary<Guid, List<CSharpMemberInfo>> membersByType)
    {
        if (!membersByType.TryGetValue(type.NodeId, out var members) || members.Count == 0)
            return null;

        var sb = new StringBuilder();

        static char AccessibilitySymbol(string? accessibility) => accessibility?.ToLowerInvariant() switch
        {
            "public" => '+',
            "protected" => '#',
            "internal" => '~',
            "private" => '-',
            "protected internal" => '#',
            "private protected" => '-',
            _ => ' '
        };

        foreach (var member in members.Take(CSharpLoaderConstants.MaxMembersInStructure))
        {
            var memberSymbol = AccessibilitySymbol(member.Accessibility);
            var returnPart = !string.IsNullOrWhiteSpace(member.ReturnType) && member.ReturnType != "void"
                ? $" → {member.ReturnType}"
                : string.Empty;

            if (string.Equals(member.Kind, "method", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(member.Kind, "constructor", StringComparison.OrdinalIgnoreCase))
            {
                var paramText = member.Parameters.Count == 0
                    ? "()"
                    : $"({string.Join(", ", member.Parameters.Select(p => p.Type))})";
                sb.AppendLine($"{memberSymbol}{member.Name}{paramText}{returnPart}");
            }
            else if (string.Equals(member.Kind, "property", StringComparison.OrdinalIgnoreCase))
            {
                sb.AppendLine($"{memberSymbol}{member.Name}{returnPart}");
            }
            else // field, event, etc.
            {
                var typePart = !string.IsNullOrWhiteSpace(member.ReturnType)
                    ? $" : {member.ReturnType}"
                    : string.Empty;
                sb.AppendLine($"{memberSymbol}{member.Name}{typePart}");
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static Span CreateSpan(Guid spanId, DocumentSpan span, Guid documentId)
    {
        return new Span
        {
            Id = spanId,
            DocumentId = documentId,
            StartByte = span.StartChar,
            EndByte = span.EndChar,
            StartLine = span.StartLine,
            StartColumn = span.StartColumn,
            EndLine = span.EndLine,
            EndColumn = span.EndColumn
        };
    }

    private static string GetFileName(RepoUri uri)
    {
        // Try to get filename from file path without using exceptions for control flow
        if (uri.IsFile)
        {
            var path = uri.LocalPath;
            if (!string.IsNullOrEmpty(path))
            {
                // Use Path.GetFileName only if the path appears valid
                var lastSeparator = Math.Max(path.LastIndexOf('\\'), path.LastIndexOf('/'));
                if (lastSeparator >= 0 && lastSeparator < path.Length - 1)
                    return path[(lastSeparator + 1)..];
                if (lastSeparator < 0 && path.Length > 0)
                    return path; // Entire path is the filename
            }
        }

        // Fall back to URI parsing
        var absolutePath = Uri.UnescapeDataString(uri.AbsolutePath);
        var slash = absolutePath.LastIndexOf('/');
        if (slash >= 0 && slash < absolutePath.Length - 1)
            return absolutePath[(slash + 1)..];

        return string.IsNullOrEmpty(absolutePath) ? uri.AbsoluteUri : absolutePath;
    }

    private static string ReadEmbeddedResource(string resourceName)
    {
        using var stream = typeof(CSharpLoader).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded SQL resource {resourceName} was not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static MetadataReference[] CreateDefaultReferences()
    {
        var assemblies = new[]
        {
            typeof(object).Assembly,
            typeof(Enumerable).Assembly,
            typeof(Uri).Assembly,
            typeof(Task).Assembly
        };

        return assemblies
            .Select(a => a.Location)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            // Some framework assemblies resolve to an empty path when running from single-file/self-contained publishes.
            // Skip those instead of letting Roslyn throw ArgumentException for an empty metadata path.
            .Select(path => MetadataReference.CreateFromFile(path))
            .GroupBy(r => (r as PortableExecutableReference)?.FilePath ?? string.Empty)
            .Select(g => g.First())
            .ToArray();
    }

    private static readonly string[] SuppressedWhenAnalysisDisabled =
    {
        "CS0234", // namespace missing
        "CS0246", // type or namespace missing
        "CS0518", // predefined type missing
        "CS1061", // type does not contain definition (depends on semantic reference)
        "CS0103", // name does not exist in current context
        "CS0012", // assembly not referenced
        "CS1674", // type must be implicitly convertible to IDisposable
        "CS0161", // not all code paths return a value
        "CS0126", // wrong return type
        "CS0019", // operator cannot be applied
        "CS1729", // constructor overload missing
        "CS8130", // cannot infer type of deconstruction variable
        "CS1503", // argument type conversion failure
        "CS1579", // foreach cannot operate (GetEnumerator missing)
        "CS8805", // top-level statements not supported (analysis fallback)
        "CS0021", // cannot apply indexing with []
        "CS0165", // use of unassigned variable
        "CS0119", // member reference invalid in this context
        "CS0535", // does not implement interface member
        "CS8795", // partial method must have implementation
        "CS0066", // event must be delegate type
        "CS0403", // cannot convert null to type parameter
        "CS4012", // ReadOnlySpan in async method (requires ref libs)
        "CS8821", // static anonymous function reference
        "CS1955", // non-invocable member used as method
        "CS0428", // cannot convert method group
        "CS0120", // object reference required
        "CS1660"  // cannot convert lambda expression
    };

    private static bool ResolveAnalysisEnabled(IConfiguration? configuration)
    {
        static bool? TryParse(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return null;
            if (bool.TryParse(raw, out var boolValue))
                return boolValue;
            if (int.TryParse(raw, out var numeric))
                return numeric != 0;
            return null;
        }

        bool? value = null;

        if (configuration is not null)
        {
            value = TryParse(configuration["REPOQL_DOTNET_ANALYSIS"]) ??
                    TryParse(configuration["RepoQL:DotNet:Analysis"]) ??
                    TryParse(configuration["repoql:dotnet:analysis"]);
        }

        value ??= TryParse(Environment.GetEnvironmentVariable("REPOQL_DOTNET_ANALYSIS"));
        return value ?? false;
    }

}
