using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RepoQL.Contracts;
using RepoQL.Contracts.Models;

namespace RepoQL.Formats.TypeScript;

public sealed class TypeScriptLoader : IFormatLoader, IFormatMaterializer, IFormatSchemaProvider
{
    internal const string StateMetadataKey = "typescript.state";

    private readonly ILogger<TypeScriptLoader> _logger;
    private readonly TypeScriptNodeClient _nodeClient;
    private static readonly Lazy<string> TypeScriptViewsSql = new(
        () => ReadEmbeddedResource("RepoQL.Formats.TypeScript.Schema.typescript_views.sql"));

    public TypeScriptLoader(TypeScriptNodeClient nodeClient, ILogger<TypeScriptLoader>? logger = null)
    {
        _nodeClient = nodeClient;
        _logger = logger ?? NullLogger<TypeScriptLoader>.Instance;
    }

    public bool Supports(SemanticMediaType mediaType)
    {
        ArgumentNullException.ThrowIfNull(mediaType);

        return mediaType.Kind switch
        {
            "code.typescript" => true,
            "code.typescript.react" => true,
            "code.javascript" => true,
            "code.javascript.react" => true,
            _ => false
        };
    }

    public async Task<bool> CanLoadAsync(DiscoveredArtifact artifact, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);

        var extension = Path.GetExtension(artifact.File.Name).ToLowerInvariant();
        if (TypeScriptMediaTypes.TryResolve(extension, out var resolved))
        {
            artifact.MediaType = resolved;
            return true;
        }

        if (artifact.MediaType is not null && Supports(artifact.MediaType))
        {
            artifact.MediaType = NormalizeKind(artifact.MediaType);
            return true;
        }

        return await Task.FromResult(false);
    }

    public async Task<DocumentModel> LoadAsync(DiscoveredArtifact artifact, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        if (artifact.RepoUri is null)
            throw new InvalidOperationException("RepoUri required to load TypeScript/JavaScript.");

        var mediaType = artifact.MediaType
                        ?? (TypeScriptMediaTypes.TryResolve(Path.GetExtension(artifact.File.Name).ToLowerInvariant(),
                            out var resolved)
                            ? resolved
                            : throw new InvalidOperationException("Media type could not be resolved for TypeScript/JavaScript file."));

        var loaded = await FileContentReader.ReadAllTextWithDigestAsync(artifact.File, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var parse = await _nodeClient.ParseAsync(
            artifact.File.Name,
            mediaType.Kind ?? string.Empty,
            loaded.Text,
            cancellationToken).ConfigureAwait(false);

        var state = new TypeScriptDocumentState
        {
            DocumentId = Guid.NewGuid(),
            ArtifactId = Guid.NewGuid(),
            Digest = loaded.Digest,
            Size = loaded.ByteLength,
            MediaType = mediaType,
            StoreUri = artifact.RepoUri.ToString(),
            Parse = parse,
            LineMap = new TextLineMap(loaded.Text)
        };

        var metadata = new Dictionary<string, object?>
        {
            [StateMetadataKey] = state,
            ["ts.parse.diagnostics"] = parse.Diagnostics
        };

        return new DocumentModel(
            artifact.RepoUri,
            mediaType,
            loaded.Text,
            syntaxTree: parse,
            metadata: metadata);
    }

    public Records Materialize(DocumentModel document)
    {
        if (document.GetMetadataOrDefault<TypeScriptDocumentState>(StateMetadataKey) is not { } state)
            throw new InvalidOperationException("TypeScript document missing state metadata.");

        var tokenCount = TokenEstimator.EstimateTokensSafe(document.Text);
        var artifact = new Artifact
        {
            Id = state.ArtifactId,
            Digest = state.Digest,
            Size = state.Size,
            MediaType = state.MediaType,
            Text = document.Text,
            StoreUri = state.StoreUri,
            TokenCount = tokenCount,
            Headline = BuildHeadline(document, state, tokenCount),
            Summary = BuildSummary(document, state),
            Structure = BuildStructure(document, state)
        };

        var now = DateTimeOffset.UtcNow;
        var imports = state.Parse.Imports
            .Select(i => new JsonObject
            {
                ["specifier"] = i.Specifier,
                ["kind"] = i.ImportKind,
                ["style"] = i.ImportStyle
            })
            .ToArray();

        var docNode = new Node
        {
            Id = state.DocumentId,
            Kind = "document",
            Uri = document.Uri,
            ArtifactId = artifact.Id,
            Props = new JsonObject
            {
                ["media_type"] = state.MediaType.ToString(),
                ["script_kind"] = state.Parse.ScriptKind,
                ["imports"] = new JsonArray(imports)
            },
            Headline = artifact.Headline,
            Structure = artifact.Structure,
            CreatedAt = now,
            UpdatedAt = now
        };

        var nodes = new List<Node> { docNode };
        var spans = new List<Span>();
        var edges = new List<Edge>();
        var ordinal = 0;

        foreach (var decl in state.Parse.Declarations)
        {
            var spanId = Guid.NewGuid();
            var span = ToSpan(document, state.DocumentId, spanId, decl.Span);
            spans.Add(span);

            var declName = decl.Name ?? string.Empty;
            var declNodeId = Guid.NewGuid();
            var declHeadline = BuildDeclHeadline(decl);
            var declStructure = BuildDeclStructure(decl);

            // Use typescript.type for class/interface/type/enum, typescript.function for functions
            var isType = decl.DeclKind is "class" or "interface" or "type" or "enum";
            var isFunction = decl.DeclKind is "function";
            var nodeKind = isType ? "typescript.type" : isFunction ? "typescript.function" : $"ts_decl_{decl.DeclKind}";

            var props = new JsonObject
            {
                ["name"] = declName,
                ["kind"] = decl.DeclKind
            };

            if (isType)
            {
                // Standard type properties for cross-language compatibility
                props["qualified_name"] = declName;
                props["namespace"] = string.Empty;  // TS uses modules, not namespaces
                props["accessibility"] = decl.IsExported ? "export" : "internal";
                props["signature"] = declHeadline;
                if (!string.IsNullOrEmpty(decl.Extends))
                    props["extends"] = decl.Extends;
                if (decl.Implements.Count > 0)
                    props["implements"] = ToJsonArray(decl.Implements);
            }
            else
            {
                props["decl_kind"] = decl.DeclKind;
                props["is_exported"] = decl.IsExported;
                props["export_kind"] = decl.ExportKind;

                if (isFunction)
                {
                    props["accessibility"] = decl.IsExported ? "export" : "internal";
                    props["signature"] = declHeadline;
                    if (!string.IsNullOrEmpty(decl.ReturnType))
                        props["return_type"] = decl.ReturnType;
                    if (decl.Parameters.Count > 0)
                        props["parameters"] = FormatParameters(decl.Parameters);
                }
            }

            // Optional properties
            if (decl.IsComponent)
                props["is_component"] = true;
            if (decl.IsComponent && decl.Hooks.Count > 0)
                props["hooks"] = ToJsonArray(decl.Hooks);

            nodes.Add(new Node
            {
                Id = declNodeId,
                Kind = nodeKind,
                SpanId = spanId,
                Uri = string.IsNullOrEmpty(declName)
                    ? RepoUri.FromLines(document.Uri.Container, span.StartLine, span.EndLine)
                    : RepoUri.FromSymbol(document.Uri.Container, declName, span.StartLine, span.EndLine),
                Props = props,
                Headline = declHeadline,
                Structure = declStructure,
                CreatedAt = now,
                UpdatedAt = now
            });

            edges.Add(CreateHasPart(docNode.Id, declNodeId, docNode.Id, ordinal++, now));
            if (isType)
            {
                if (!string.IsNullOrWhiteSpace(decl.Extends))
                {
                    edges.Add(CreateReferenceEdge(
                        declNodeId,
                        "EXTENDS",
                        decl.Extends!,
                        docNode.Id,
                        now));
                }

                foreach (var implementedType in decl.Implements.Where(i => !string.IsNullOrWhiteSpace(i)))
                {
                    edges.Add(CreateReferenceEdge(
                        declNodeId,
                        "IMPLEMENTS",
                        implementedType,
                        docNode.Id,
                        now));
                }
            }

            if (decl.Members.Count > 0)
            {
                var memberOrdinal = 0;
                foreach (var member in decl.Members)
                {
                    var memberSpanId = Guid.NewGuid();
                    var memberSpan = ToSpan(document, state.DocumentId, memberSpanId, member.Span);
                    spans.Add(memberSpan);

                    var memberSymbol = string.IsNullOrEmpty(declName)
                        ? member.Name
                        : $"{declName}.{member.Name}";
                    var memberNodeId = Guid.NewGuid();
                    var memberProps = new JsonObject
                    {
                        ["name"] = member.Name,
                        ["kind"] = member.MemberKind,
                        ["declaring_type"] = declName
                    };
                    if (!string.IsNullOrEmpty(member.ReturnType))
                        memberProps["return_type"] = member.ReturnType;
                    if (!string.IsNullOrEmpty(member.Type))
                        memberProps["type"] = member.Type;
                    if (member.Parameters.Count > 0)
                        memberProps["parameters"] = FormatParameters(member.Parameters);

                    nodes.Add(new Node
                    {
                        Id = memberNodeId,
                        Kind = "typescript.member",
                        SpanId = memberSpanId,
                        Uri = RepoUri.FromSymbol(document.Uri.Container, memberSymbol, memberSpan.StartLine, memberSpan.EndLine),
                        Props = memberProps,
                        Headline = BuildMemberSignature(member),
                        CreatedAt = now,
                        UpdatedAt = now
                    });

                    edges.Add(CreateHasPart(declNodeId, memberNodeId, docNode.Id, memberOrdinal++, now));
                }
            }
        }

        var annotations = new List<Annotation>();
        foreach (var diag in state.Parse.Diagnostics)
        {
            annotations.Add(new Annotation
            {
                Id = Guid.NewGuid(),
                Kind = "diagnostic",
                Severity = "warning",
                Source = "repoql.formats.typescript",
                RuleId = "ts.parse_error",
                Message = diag.Message,
                ScopeDocumentId = docNode.Id,
                TargetNodeId = docNode.Id,
                CreatedAt = now
            });
        }

        return new Records
        {
            Artifacts = [artifact],
            Nodes = [.. nodes],
            Spans = [.. spans],
            Edges = [.. edges],
            Annotations = [.. annotations]
        };
    }

    public IEnumerable<FormatSqlScript> GetSchemaScripts()
    {
        yield return new FormatSqlScript("typescript_views", TypeScriptViewsSql.Value);
    }

    private static string ReadEmbeddedResource(string resourceName)
    {
        using var stream = typeof(TypeScriptLoader).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded SQL resource {resourceName} was not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static SemanticMediaType NormalizeKind(SemanticMediaType mediaType)
    {
        // Ensure kind is present and normalized to expected values.
        return mediaType.Kind switch
        {
            "code.typescript" => TypeScriptMediaTypes.TypeScript,
            "code.typescript.react" => TypeScriptMediaTypes.TypeScriptReact,
            "code.javascript" => TypeScriptMediaTypes.JavaScript,
            "code.javascript.react" => TypeScriptMediaTypes.JavaScriptReact,
            _ => mediaType
        };
    }

    private static Span ToSpan(DocumentModel document, Guid documentId, Guid spanId, TypeScriptSpan tsSpan)
    {
        var start = Math.Clamp(tsSpan.Start, 0, document.Text.Length);
        var end = Math.Clamp(tsSpan.End, start, document.Text.Length);
        var mapped = document.LineMap.GetSpan(start, end);

        return new Span
        {
            Id = spanId,
            DocumentId = documentId,
            StartLine = mapped.StartLine,
            StartColumn = mapped.StartColumn,
            EndLine = mapped.EndLine,
            EndColumn = mapped.EndColumn,
            StartByte = CalculateUtf8Bytes(document.Text, start),
            EndByte = CalculateUtf8Bytes(document.Text, end)
        };
    }

    private static long CalculateUtf8Bytes(string text, int chars)
        => Encoding.UTF8.GetByteCount(text.AsSpan(0, Math.Min(text.Length, chars)));

    private static Edge CreateHasPart(Guid documentId, Guid childId, Guid scopeDocumentId, int ordinal, DateTimeOffset timestamp)
        => new()
        {
            Id = Guid.NewGuid(),
            SrcId = documentId,
            DstId = childId,
            Type = "HAS_PART",
            IsComposition = true,
            Ordinal = ordinal,
            ScopeDocumentId = scopeDocumentId,
            CreatedAt = timestamp
        };

    private static Edge CreateReferenceEdge(Guid srcId, string edgeType, string targetName, Guid scopeDocumentId, DateTimeOffset timestamp)
        => new()
        {
            Id = Guid.NewGuid(),
            SrcId = srcId,
            DstId = null,
            Type = edgeType,
            IsComposition = false,
            ScopeDocumentId = scopeDocumentId,
            Props = new JsonObject
            {
                ["target"] = targetName
            },
            CreatedAt = timestamp
        };

    private static string BuildDeclHeadline(TypeScriptDeclaration decl)
    {
        var headline = BuildDeclarationSignature(decl, includeFunctionKeyword: true);
        if (decl.IsExported)
            headline = $"export {headline}";
        if (decl.IsComponent)
            headline = $"{headline} (component)";
        return headline;
    }

    private static string? BuildDeclStructure(TypeScriptDeclaration decl)
    {
        var sb = new StringBuilder();
        sb.AppendLine(BuildDeclarationSignature(decl, includeFunctionKeyword: true));

        foreach (var member in decl.Members)
        {
            sb.AppendLine($"  {BuildMemberSignature(member)}");
        }

        return sb.ToString().TrimEnd();
    }

    private static string BuildHeadline(DocumentModel document, TypeScriptDocumentState state, int? tokenCount)
    {
        var fileName = SafeFileName(document.Uri);
        var mediaTypeKind = string.IsNullOrWhiteSpace(state.MediaType.Kind) ? state.MediaType.ToString() : state.MediaType.Kind;
        var exports = state.Parse.Declarations.Where(d => d.IsExported && !string.IsNullOrWhiteSpace(d.Name))
            .Select(d => d.Name!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var exportText = exports.Count == 0 ? "exports: -" : $"exports: {string.Join(", ", exports)}";
        var importText = $"imports: {state.Parse.Imports.Count}";
        var sizeText = $"{document.LineMap.LineCount} ln";
        if (tokenCount.HasValue)
            sizeText = $"{sizeText}, {FormatTokenCount(tokenCount.Value)}";

        return string.Join(" | ", new[] { fileName, mediaTypeKind, sizeText, exportText, importText }.Where(s => !string.IsNullOrWhiteSpace(s)));
    }

    private static string BuildSummary(DocumentModel document, TypeScriptDocumentState state)
    {
        var declGroups = state.Parse.Declarations
            .GroupBy(d => d.DeclKind)
            .Select(g => $"{g.Key}:{g.Count()}")
            .OrderByDescending(s => s)
            .ToList();
        var components = state.Parse.Declarations.Count(d => d.IsComponent);

        var parts = new List<string>
        {
            $"size:{state.Size}b",
            $"lines:{document.LineMap.LineCount}",
            $"imports:{state.Parse.Imports.Count}",
            $"decls:{state.Parse.Declarations.Count}"
        };
        if (declGroups.Count > 0) parts.Add(string.Join(" ", declGroups));
        if (components > 0) parts.Add($"components:{components}");

        return string.Join(" | ", parts);
    }

    private static string BuildStructure(DocumentModel document, TypeScriptDocumentState state)
    {
        var fileName = SafeFileName(document.Uri);
        var mediaTypeKind = string.IsNullOrWhiteSpace(state.MediaType.Kind) ? state.MediaType.ToString() : state.MediaType.Kind;
        var sb = new StringBuilder();

        sb.AppendLine($"{fileName} ({mediaTypeKind})");

        sb.AppendLine("  Imports:");
        if (state.Parse.Imports.Count == 0)
        {
            sb.AppendLine("    (none)");
        }
        else
        {
            foreach (var import in state.Parse.Imports)
            {
                sb.AppendLine($"    '{import.Specifier}' ({import.ImportStyle})");
            }
        }

        var exportedDecls = state.Parse.Declarations.Where(d => d.IsExported).ToList();
        sb.AppendLine("  Exports:");
        if (exportedDecls.Count == 0)
        {
            sb.AppendLine("    (none)");
        }
        else
        {
            foreach (var decl in exportedDecls)
            {
                sb.Append("    +");
                sb.Append(BuildDeclarationSignature(decl, includeFunctionKeyword: false));
                if (!string.IsNullOrWhiteSpace(decl.Name))
                {
                    sb.Append(" #symbol=");
                    sb.Append(decl.Name);
                }

                sb.AppendLine();
            }
        }

        var internalDecls = state.Parse.Declarations.Where(d => !d.IsExported).ToList();
        sb.AppendLine("  Internal:");
        if (internalDecls.Count == 0)
        {
            sb.AppendLine("    (none)");
        }
        else
        {
            foreach (var decl in internalDecls)
            {
                sb.Append("    -");
                sb.Append(BuildDeclarationSignature(decl, includeFunctionKeyword: false));
                if (!string.IsNullOrWhiteSpace(decl.Name))
                {
                    sb.Append(" #symbol=");
                    sb.Append(decl.Name);
                }

                sb.AppendLine();
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static string BuildDeclarationSignature(TypeScriptDeclaration decl, bool includeFunctionKeyword)
    {
        var name = string.IsNullOrWhiteSpace(decl.Name) ? $"<{decl.DeclKind}>" : decl.Name!;
        var typeParameters = FormatTypeParameters(decl.TypeParameters);

        return decl.DeclKind switch
        {
            "function" => includeFunctionKeyword
                ? $"function {name}{typeParameters}{FormatParameters(decl.Parameters)}{FormatReturnType(decl.ReturnType)}"
                : $"{name}{typeParameters}{FormatParameters(decl.Parameters)}{FormatReturnType(decl.ReturnType)}",
            "class" => $"class {name}{typeParameters}{FormatHeritage(decl.Extends, decl.Implements)}",
            "interface" => $"interface {name}{typeParameters}{FormatHeritage(decl.Extends, decl.Implements)}",
            "type" => $"type {name}{typeParameters}",
            "enum" => $"enum {name}",
            "variable" => FormatVariableSignature(name, decl.ReturnType),
            "namespace" => $"namespace {name}",
            _ => $"{decl.DeclKind} {name}"
        };
    }

    private static string BuildMemberSignature(TypeScriptMember member)
    {
        return member.MemberKind switch
        {
            "constructor" => $"constructor{FormatParameters(member.Parameters)}",
            "method" => $"method {member.Name}{FormatParameters(member.Parameters)}{FormatReturnType(member.ReturnType)}",
            "field" => $"field {member.Name}{FormatTypeAnnotation(member.Type)}",
            "getter" => $"getter {member.Name}{FormatTypeAnnotation(member.ReturnType)}",
            "setter" => $"setter {member.Name}{FormatParameters(member.Parameters)}{FormatReturnType(member.ReturnType)}",
            "enumMember" => $"enumMember {member.Name}",
            _ => $"{member.MemberKind} {member.Name}"
        };
    }

    private static string FormatParameters(IReadOnlyList<TypeScriptParameter> parameters)
    {
        if (parameters.Count == 0) return "()";
        var parts = parameters.Select(p =>
        {
            var prefix = p.IsRest ? "..." : "";
            var suffix = p.IsOptional ? "?" : "";
            var type = p.Type != null ? $": {p.Type}" : "";
            return $"{prefix}{p.Name}{suffix}{type}";
        });
        return $"({string.Join(", ", parts)})";
    }

    private static string FormatTypeParameters(IReadOnlyList<string> typeParameters)
    {
        if (typeParameters.Count == 0)
            return string.Empty;

        return $"<{string.Join(", ", typeParameters)}>";
    }

    private static string FormatHeritage(string? extendsType, IReadOnlyList<string> implementedTypes)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(extendsType))
            parts.Add($"extends {extendsType}");

        var nonEmptyImplemented = implementedTypes.Where(i => !string.IsNullOrWhiteSpace(i)).ToList();
        if (nonEmptyImplemented.Count > 0)
            parts.Add($"implements {string.Join(", ", nonEmptyImplemented)}");

        return parts.Count == 0
            ? string.Empty
            : $" {string.Join(" ", parts)}";
    }

    private static string FormatVariableSignature(string name, string? type)
    {
        if (string.IsNullOrWhiteSpace(type))
            return name;

        return $"const {name}: {type}";
    }

    private static string FormatReturnType(string? returnType)
    {
        if (string.IsNullOrWhiteSpace(returnType))
            return string.Empty;

        return $": {returnType}";
    }

    private static string FormatTypeAnnotation(string? type)
    {
        if (string.IsNullOrWhiteSpace(type))
            return string.Empty;

        return $": {type}";
    }

    private static JsonArray ToJsonArray(IEnumerable<string> values)
        => new(values
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => (JsonNode)JsonValue.Create(v)!)
            .ToArray());

    private static string FormatTokenCount(int tokenCount)
    {
        if (tokenCount > 1000)
            return $"~{tokenCount / 1000d:0.#}k tok";

        return $"~{tokenCount} tok";
    }

    private static string SafeFileName(RepoUri uri)
    {
        try
        {
            if (uri.IsFile && !string.IsNullOrEmpty(uri.LocalPath))
                return Path.GetFileName(uri.LocalPath);
        }
        catch
        {
            // ignored
        }

        var ap = Uri.UnescapeDataString(uri.AbsolutePath);
        var slash = ap.LastIndexOf('/') >= 0 ? ap[(ap.LastIndexOf('/') + 1)..] : ap;
        return string.IsNullOrEmpty(slash) ? uri.AbsoluteUri : slash;
    }
}
