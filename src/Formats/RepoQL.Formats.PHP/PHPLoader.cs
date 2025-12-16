using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RepoQL.Contracts;
using RepoQL.Contracts.Models;
using RepoQL.Templating;

namespace RepoQL.Formats.PHP;

public sealed partial class PHPLoader : IFormatLoader, IFormatMaterializer, IDisposable
{
    private ILogger<PHPLoader> Logger { get; }
    private readonly ITemplateRenderer? _renderer;
    private readonly PHPTreeSitterClient _client;

    private const string StateMetadataKey = "php.state";

    public PHPLoader(ITemplateRenderer? renderer = null, ILogger<PHPLoader>? logger = null)
    {
        Logger = logger ?? NullLogger<PHPLoader>.Instance;
        _renderer = renderer ?? new LiquidTemplateRenderer(
            assembly: typeof(PHPLoader).Assembly,
            resourceRoot: "RepoQL.Formats.PHP.Templates");
        _client = new PHPTreeSitterClient();
    }

    public bool Supports(SemanticMediaType mediaType)
    {
        ArgumentNullException.ThrowIfNull(mediaType);

        return string.Equals(mediaType.Kind, PHPMediaTypes.PHP.Kind, StringComparison.OrdinalIgnoreCase)
               || string.Equals(mediaType.Kind, PHPMediaTypes.PHPTemplate.Kind, StringComparison.OrdinalIgnoreCase)
               || (string.Equals(mediaType.Type, "text", StringComparison.OrdinalIgnoreCase)
                   && string.Equals(mediaType.Subtype, "x-php", StringComparison.OrdinalIgnoreCase));
    }

    public async Task<bool> CanLoadAsync(DiscoveredArtifact artifact, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);

        var name = artifact.File.Name.ToLowerInvariant();
        var extension = Path.GetExtension(name);

        if (PHPMediaTypes.TryResolve(extension, out var mediaType))
        {
            artifact.MediaType = mediaType;
            return true;
        }

        return false;
    }

    public async Task<DocumentModel> LoadAsync(DiscoveredArtifact artifact, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        if (artifact.RepoUri is null)
            throw new InvalidOperationException("RepoUri required to load PHP files.");

        var loaded = await FileContentReader.ReadAllTextWithDigestAsync(
            artifact.File,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var text = loaded.Text;
        var digest = loaded.Digest;

        var parseResult = _client.Parse(text);

        var state = new PHPDocumentState
        {
            DocumentId = Guid.NewGuid(),
            ParseResult = parseResult,
            Digest = digest,
            Size = loaded.ByteLength,
            MediaType = artifact.MediaType ?? PHPMediaTypes.PHP,
            StoreUri = artifact.RepoUri.ToString()
        };

        var metadata = new Dictionary<string, object?>
        {
            [StateMetadataKey] = state
        };

        return new DocumentModel(artifact.RepoUri, state.MediaType, text, parseResult, metadata);
    }

    public Records Materialize(DocumentModel document)
    {
        var state = document.GetMetadataOrDefault<PHPDocumentState>(StateMetadataKey)
            ?? throw new InvalidOperationException("PHP document missing state metadata.");

        var parseResult = state.ParseResult;

        // Generate X-ray summaries
        string? headline = null;
        string? summary = null;
        string? structure = null;

        try
        {
            if (_renderer is not null)
            {
                var model = BuildTemplateModel(document, state, parseResult);
                headline = _renderer.RenderAsync("xray/headline", model).GetAwaiter().GetResult();
                summary = _renderer.RenderAsync("xray/summary", model).GetAwaiter().GetResult();
                structure = _renderer.RenderAsync("xray/structure", model).GetAwaiter().GetResult();
            }
        }
        catch (Exception ex)
        {
            LogTemplateRenderError(Logger, ex);
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
            Structure = structure
        };

        var docNode = new Node
        {
            Id = state.DocumentId,
            Kind = PHPNodeKinds.Document,
            Uri = document.Uri,
            ArtifactId = artifact.Id,
            Props = new JsonObject
            {
                [PHPPropertyKeys.Language] = PHPValues.LanguageName,
                [PHPPropertyKeys.ByteSize] = artifact.Size,
                [PHPPropertyKeys.LineCount] = document.LineMap.LineCount
            },
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        var nodes = new List<Node> { docNode };
        var spans = new List<Span>();
        var edges = new List<Edge>();
        var now = DateTimeOffset.UtcNow;
        var ordinal = 0;

        // Materialize classes
        foreach (var classInfo in parseResult.Classes)
        {
            var classNode = CreateClassNode(classInfo, state.DocumentId, artifact.Id, document, now);
            nodes.Add(classNode);
            edges.Add(CreateComposition(docNode.Id, classNode.Id, ordinal++, state.DocumentId, now));

            var classSpan = CreateSpan(classInfo.Span, state.DocumentId, document);
            spans.Add(classSpan);

            // Add method nodes
            var memberOrdinal = 0;
            foreach (var method in classInfo.Methods)
            {
                var methodNode = CreateMethodNode(method, state.DocumentId, artifact.Id, document, now);
                nodes.Add(methodNode);
                edges.Add(CreateComposition(classNode.Id, methodNode.Id, memberOrdinal++, state.DocumentId, now));
                spans.Add(CreateSpan(method.Span, state.DocumentId, document));
            }

            // Add property nodes
            foreach (var prop in classInfo.Properties)
            {
                var propNode = CreatePropertyNode(prop, state.DocumentId, artifact.Id, document, now);
                nodes.Add(propNode);
                edges.Add(CreateComposition(classNode.Id, propNode.Id, memberOrdinal++, state.DocumentId, now));
                spans.Add(CreateSpan(prop.Span, state.DocumentId, document));
            }

            // Add EXTENDS edge
            if (!string.IsNullOrEmpty(classInfo.Extends))
            {
                edges.Add(CreateReferenceEdge(classNode.Id, PHPEdgeTypes.Extends, classInfo.Extends, state.DocumentId, now));
            }

            // Add IMPLEMENTS edges
            foreach (var iface in classInfo.Implements)
            {
                edges.Add(CreateReferenceEdge(classNode.Id, PHPEdgeTypes.Implements, iface, state.DocumentId, now));
            }

            // Add USES_TRAIT edges
            foreach (var trait in classInfo.UsesTraits)
            {
                edges.Add(CreateReferenceEdge(classNode.Id, PHPEdgeTypes.UsesTrait, trait, state.DocumentId, now));
            }
        }

        // Materialize interfaces
        foreach (var ifaceInfo in parseResult.Interfaces)
        {
            var ifaceNode = CreateInterfaceNode(ifaceInfo, state.DocumentId, artifact.Id, document, now);
            nodes.Add(ifaceNode);
            edges.Add(CreateComposition(docNode.Id, ifaceNode.Id, ordinal++, state.DocumentId, now));
            spans.Add(CreateSpan(ifaceInfo.Span, state.DocumentId, document));

            var memberOrdinal = 0;
            foreach (var method in ifaceInfo.Methods)
            {
                var methodNode = CreateMethodNode(method, state.DocumentId, artifact.Id, document, now);
                nodes.Add(methodNode);
                edges.Add(CreateComposition(ifaceNode.Id, methodNode.Id, memberOrdinal++, state.DocumentId, now));
                spans.Add(CreateSpan(method.Span, state.DocumentId, document));
            }

            foreach (var baseIface in ifaceInfo.Extends)
            {
                edges.Add(CreateReferenceEdge(ifaceNode.Id, PHPEdgeTypes.Extends, baseIface, state.DocumentId, now));
            }
        }

        // Materialize traits
        foreach (var traitInfo in parseResult.Traits)
        {
            var traitNode = CreateTraitNode(traitInfo, state.DocumentId, artifact.Id, document, now);
            nodes.Add(traitNode);
            edges.Add(CreateComposition(docNode.Id, traitNode.Id, ordinal++, state.DocumentId, now));
            spans.Add(CreateSpan(traitInfo.Span, state.DocumentId, document));

            var memberOrdinal = 0;
            foreach (var method in traitInfo.Methods)
            {
                var methodNode = CreateMethodNode(method, state.DocumentId, artifact.Id, document, now);
                nodes.Add(methodNode);
                edges.Add(CreateComposition(traitNode.Id, methodNode.Id, memberOrdinal++, state.DocumentId, now));
                spans.Add(CreateSpan(method.Span, state.DocumentId, document));
            }
        }

        // Materialize enums
        foreach (var enumInfo in parseResult.Enums)
        {
            var enumNode = CreateEnumNode(enumInfo, state.DocumentId, artifact.Id, document, now);
            nodes.Add(enumNode);
            edges.Add(CreateComposition(docNode.Id, enumNode.Id, ordinal++, state.DocumentId, now));
            spans.Add(CreateSpan(enumInfo.Span, state.DocumentId, document));

            var memberOrdinal = 0;
            foreach (var caseInfo in enumInfo.Cases)
            {
                var caseNode = CreateEnumCaseNode(caseInfo, state.DocumentId, artifact.Id, document, now);
                nodes.Add(caseNode);
                edges.Add(CreateComposition(enumNode.Id, caseNode.Id, memberOrdinal++, state.DocumentId, now));
                spans.Add(CreateSpan(caseInfo.Span, state.DocumentId, document));
            }
        }

        // Materialize standalone functions
        foreach (var funcInfo in parseResult.Functions)
        {
            var funcNode = CreateFunctionNode(funcInfo, state.DocumentId, artifact.Id, document, now);
            nodes.Add(funcNode);
            edges.Add(CreateComposition(docNode.Id, funcNode.Id, ordinal++, state.DocumentId, now));
            spans.Add(CreateSpan(funcInfo.Span, state.DocumentId, document));
        }

        return new Records
        {
            Artifacts = [artifact],
            Nodes = nodes.ToArray(),
            Spans = spans.ToArray(),
            Edges = edges.ToArray()
        };
    }

    private static Dictionary<string, object?> BuildTemplateModel(DocumentModel document, PHPDocumentState state, PHPParseResult parseResult)
    {
        var fileName = GetFileName(document.Uri);
        var allMethods = parseResult.Classes.SelectMany(c => c.Methods.Select(m => m.Name))
            .Concat(parseResult.Interfaces.SelectMany(i => i.Methods.Select(m => m.Name)))
            .Concat(parseResult.Traits.SelectMany(t => t.Methods.Select(m => m.Name)))
            .Concat(parseResult.Functions.Select(f => f.Name))
            .ToList();

        var allProperties = parseResult.Classes.SelectMany(c => c.Properties.Select(p => p.Name))
            .Concat(parseResult.Traits.SelectMany(t => t.Properties.Select(p => p.Name)))
            .ToList();

        return new Dictionary<string, object?>
        {
            ["file_name"] = fileName,
            ["media_kind"] = state.MediaType.Kind ?? string.Empty,
            ["size_bytes"] = state.Size,
            ["line_count"] = document.LineMap.LineCount,
            ["namespace"] = parseResult.Namespace ?? "(global)",
            ["class_count"] = parseResult.Classes.Count,
            ["interface_count"] = parseResult.Interfaces.Count,
            ["trait_count"] = parseResult.Traits.Count,
            ["enum_count"] = parseResult.Enums.Count,
            ["function_count"] = parseResult.Functions.Count,
            ["use_count"] = parseResult.UseStatements.Count,
            ["classes"] = parseResult.Classes.Select(c => new Dictionary<string, object?>
            {
                ["name"] = c.Name,
                ["accessibility"] = c.Accessibility ?? "public",
                ["is_abstract"] = c.IsAbstract,
                ["is_final"] = c.IsFinal,
                ["extends"] = c.Extends,
                ["implements"] = c.Implements,
                ["methods"] = c.Methods.Select(m => new Dictionary<string, object?>
                {
                    ["name"] = m.Name,
                    ["accessibility"] = m.Accessibility ?? "public",
                    ["is_static"] = m.IsStatic,
                    ["return_type"] = m.ReturnType,
                    ["parameters"] = m.Parameters
                }).ToList(),
                ["properties"] = c.Properties.Select(p => new Dictionary<string, object?>
                {
                    ["name"] = p.Name,
                    ["accessibility"] = p.Accessibility ?? "public",
                    ["type"] = p.Type
                }).ToList()
            }).ToList(),
            ["interfaces"] = parseResult.Interfaces.Select(i => new Dictionary<string, object?>
            {
                ["name"] = i.Name,
                ["extends"] = i.Extends,
                ["methods"] = i.Methods.Select(m => m.Name).ToList()
            }).ToList(),
            ["traits"] = parseResult.Traits.Select(t => new Dictionary<string, object?>
            {
                ["name"] = t.Name,
                ["methods"] = t.Methods.Select(m => m.Name).ToList()
            }).ToList(),
            ["enums"] = parseResult.Enums.Select(e => new Dictionary<string, object?>
            {
                ["name"] = e.Name,
                ["backed_type"] = e.BackedType,
                ["cases"] = e.Cases.Select(c => c.Name).ToList()
            }).ToList(),
            ["functions"] = parseResult.Functions.Select(f => new Dictionary<string, object?>
            {
                ["name"] = f.Name,
                ["return_type"] = f.ReturnType,
                ["parameters"] = f.Parameters
            }).ToList(),
            ["uses"] = parseResult.UseStatements.Select(u => u.Name).ToList(),
            ["all_methods"] = allMethods,
            ["all_properties"] = allProperties
        };
    }

    private static Node CreateClassNode(PHPClassInfo classInfo, Guid docId, Guid artifactId, DocumentModel document, DateTimeOffset now)
    {
        var props = new JsonObject
        {
            [PHPPropertyKeys.Name] = classInfo.Name
        };

        if (!string.IsNullOrEmpty(classInfo.Namespace))
            props[PHPPropertyKeys.QualifiedName] = $"{classInfo.Namespace}\\{classInfo.Name}";
        if (!string.IsNullOrEmpty(classInfo.Accessibility))
            props[PHPPropertyKeys.Accessibility] = classInfo.Accessibility;
        if (classInfo.IsAbstract)
            props[PHPPropertyKeys.IsAbstract] = true;
        if (classInfo.IsFinal)
            props[PHPPropertyKeys.IsFinal] = true;
        if (!string.IsNullOrEmpty(classInfo.Extends))
            props[PHPPropertyKeys.BaseClass] = classInfo.Extends;
        if (classInfo.Implements.Count > 0)
            props[PHPPropertyKeys.Interfaces] = new JsonArray(classInfo.Implements.Select(i => JsonValue.Create(i)).ToArray());

        return new Node
        {
            Id = Guid.NewGuid(),
            Kind = PHPNodeKinds.Class,
            ArtifactId = artifactId,
            Props = props,
            Headline = $"class {classInfo.Name}",
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static Node CreateInterfaceNode(PHPInterfaceInfo ifaceInfo, Guid docId, Guid artifactId, DocumentModel document, DateTimeOffset now)
    {
        var props = new JsonObject
        {
            [PHPPropertyKeys.Name] = ifaceInfo.Name
        };

        if (!string.IsNullOrEmpty(ifaceInfo.Namespace))
            props[PHPPropertyKeys.QualifiedName] = $"{ifaceInfo.Namespace}\\{ifaceInfo.Name}";

        return new Node
        {
            Id = Guid.NewGuid(),
            Kind = PHPNodeKinds.Interface,
            ArtifactId = artifactId,
            Props = props,
            Headline = $"interface {ifaceInfo.Name}",
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static Node CreateTraitNode(PHPTraitInfo traitInfo, Guid docId, Guid artifactId, DocumentModel document, DateTimeOffset now)
    {
        var props = new JsonObject
        {
            [PHPPropertyKeys.Name] = traitInfo.Name
        };

        if (!string.IsNullOrEmpty(traitInfo.Namespace))
            props[PHPPropertyKeys.QualifiedName] = $"{traitInfo.Namespace}\\{traitInfo.Name}";

        return new Node
        {
            Id = Guid.NewGuid(),
            Kind = PHPNodeKinds.Trait,
            ArtifactId = artifactId,
            Props = props,
            Headline = $"trait {traitInfo.Name}",
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static Node CreateEnumNode(PHPEnumInfo enumInfo, Guid docId, Guid artifactId, DocumentModel document, DateTimeOffset now)
    {
        var props = new JsonObject
        {
            [PHPPropertyKeys.Name] = enumInfo.Name
        };

        if (!string.IsNullOrEmpty(enumInfo.Namespace))
            props[PHPPropertyKeys.QualifiedName] = $"{enumInfo.Namespace}\\{enumInfo.Name}";
        if (!string.IsNullOrEmpty(enumInfo.BackedType))
            props[PHPPropertyKeys.BackedType] = enumInfo.BackedType;

        return new Node
        {
            Id = Guid.NewGuid(),
            Kind = PHPNodeKinds.Enum,
            ArtifactId = artifactId,
            Props = props,
            Headline = $"enum {enumInfo.Name}",
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static Node CreateEnumCaseNode(PHPEnumCaseInfo caseInfo, Guid docId, Guid artifactId, DocumentModel document, DateTimeOffset now)
    {
        var props = new JsonObject
        {
            [PHPPropertyKeys.Name] = caseInfo.Name
        };

        return new Node
        {
            Id = Guid.NewGuid(),
            Kind = PHPNodeKinds.EnumCase,
            ArtifactId = artifactId,
            Props = props,
            Headline = $"case {caseInfo.Name}",
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static Node CreateFunctionNode(PHPFunctionInfo funcInfo, Guid docId, Guid artifactId, DocumentModel document, DateTimeOffset now)
    {
        var props = new JsonObject
        {
            [PHPPropertyKeys.Name] = funcInfo.Name
        };

        if (!string.IsNullOrEmpty(funcInfo.ReturnType))
            props[PHPPropertyKeys.ReturnType] = funcInfo.ReturnType;
        if (funcInfo.Parameters.Count > 0)
            props[PHPPropertyKeys.Parameters] = new JsonArray(funcInfo.Parameters.Select(p => JsonValue.Create(p)).ToArray());

        var sig = BuildFunctionSignature(funcInfo.Name, funcInfo.Parameters, funcInfo.ReturnType);

        return new Node
        {
            Id = Guid.NewGuid(),
            Kind = PHPNodeKinds.Function,
            ArtifactId = artifactId,
            Props = props,
            Headline = sig,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static Node CreateMethodNode(PHPMethodInfo method, Guid docId, Guid artifactId, DocumentModel document, DateTimeOffset now)
    {
        var props = new JsonObject
        {
            [PHPPropertyKeys.Name] = method.Name
        };

        if (!string.IsNullOrEmpty(method.Accessibility))
            props[PHPPropertyKeys.Accessibility] = method.Accessibility;
        if (method.IsStatic)
            props[PHPPropertyKeys.IsStatic] = true;
        if (method.IsAbstract)
            props[PHPPropertyKeys.IsAbstract] = true;
        if (!string.IsNullOrEmpty(method.ReturnType))
            props[PHPPropertyKeys.ReturnType] = method.ReturnType;
        if (method.Parameters.Count > 0)
            props[PHPPropertyKeys.Parameters] = new JsonArray(method.Parameters.Select(p => JsonValue.Create(p)).ToArray());

        var sig = BuildMethodSignature(method);

        return new Node
        {
            Id = Guid.NewGuid(),
            Kind = PHPNodeKinds.Method,
            ArtifactId = artifactId,
            Props = props,
            Headline = sig,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static Node CreatePropertyNode(PHPPropertyInfo prop, Guid docId, Guid artifactId, DocumentModel document, DateTimeOffset now)
    {
        var props = new JsonObject
        {
            [PHPPropertyKeys.Name] = prop.Name
        };

        if (!string.IsNullOrEmpty(prop.Accessibility))
            props[PHPPropertyKeys.Accessibility] = prop.Accessibility;
        if (prop.IsStatic)
            props[PHPPropertyKeys.IsStatic] = true;
        if (!string.IsNullOrEmpty(prop.Type))
            props[PHPPropertyKeys.Type] = prop.Type;
        if (prop.HasDefault)
            props[PHPPropertyKeys.HasDefault] = true;

        var headline = prop.Type is not null ? $"{prop.Type} {prop.Name}" : prop.Name;

        return new Node
        {
            Id = Guid.NewGuid(),
            Kind = PHPNodeKinds.Property,
            ArtifactId = artifactId,
            Props = props,
            Headline = headline,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static string BuildMethodSignature(PHPMethodInfo method)
    {
        var prefix = method.Accessibility ?? "public";
        if (method.IsStatic) prefix += " static";
        if (method.IsAbstract) prefix += " abstract";

        var paramStr = string.Join(", ", method.Parameters.Take(3));
        if (method.Parameters.Count > 3) paramStr += "...";

        var sig = $"{prefix} function {method.Name}({paramStr})";
        if (!string.IsNullOrEmpty(method.ReturnType))
            sig += $": {method.ReturnType}";

        return sig;
    }

    private static string BuildFunctionSignature(string name, List<string> parameters, string? returnType)
    {
        var paramStr = string.Join(", ", parameters.Take(3));
        if (parameters.Count > 3) paramStr += "...";

        var sig = $"function {name}({paramStr})";
        if (!string.IsNullOrEmpty(returnType))
            sig += $": {returnType}";

        return sig;
    }

    private static Span CreateSpan(PHPSpan phpSpan, Guid docId, DocumentModel document)
    {
        var start = Math.Clamp(phpSpan.Start, 0, document.Text.Length);
        var end = Math.Clamp(phpSpan.End, start, document.Text.Length);
        var mapped = document.LineMap.GetSpan(start, end);

        return new Span
        {
            Id = Guid.NewGuid(),
            DocumentId = docId,
            StartByte = mapped.StartChar,
            EndByte = mapped.EndChar,
            StartLine = mapped.StartLine,
            StartColumn = mapped.StartColumn,
            EndLine = mapped.EndLine,
            EndColumn = mapped.EndColumn
        };
    }

    private static Edge CreateComposition(Guid parentId, Guid childId, int ordinal, Guid scopeDocId, DateTimeOffset now)
        => new()
        {
            Id = Guid.NewGuid(),
            SrcId = parentId,
            DstId = childId,
            Type = PHPEdgeTypes.HasPart,
            IsComposition = true,
            Ordinal = ordinal,
            ScopeDocumentId = scopeDocId,
            CreatedAt = now
        };

    private static Edge CreateReferenceEdge(Guid srcId, string edgeType, string targetName, Guid scopeDocId, DateTimeOffset now)
        => new()
        {
            Id = Guid.NewGuid(),
            SrcId = srcId,
            DstId = null,
            Type = edgeType,
            IsComposition = false,
            ScopeDocumentId = scopeDocId,
            Props = new JsonObject
            {
                ["target"] = targetName
            },
            CreatedAt = now
        };

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
            // ignore
        }
        var ap = Uri.UnescapeDataString(uri.AbsolutePath);
        var slash = ap.LastIndexOf('/') >= 0 ? ap[(ap.LastIndexOf('/') + 1)..] : ap;
        return string.IsNullOrEmpty(slash) ? uri.AbsoluteUri : slash;
    }

    public void Dispose()
    {
        _client.Dispose();
    }

    [LoggerMessage(LogLevel.Warning, "Failed to render X-ray template")]
    static partial void LogTemplateRenderError(ILogger<PHPLoader> logger, Exception ex);
}
