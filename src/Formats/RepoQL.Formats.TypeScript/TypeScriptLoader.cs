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

        var artifact = new Artifact
        {
            Id = state.ArtifactId,
            Digest = state.Digest,
            Size = state.Size,
            MediaType = state.MediaType,
            Text = document.Text,
            StoreUri = state.StoreUri,
            Headline = BuildHeadline(document, state),
            Summary = BuildSummary(document, state),
            Structure = BuildStructure(document, state)
        };

        var now = DateTimeOffset.UtcNow;
        var docNode = new Node
        {
            Id = state.DocumentId,
            Kind = "document",
            Uri = document.Uri,
            ArtifactId = artifact.Id,
            Props = new JsonObject
            {
                ["media_type"] = state.MediaType.ToString(),
                ["script_kind"] = state.Parse.ScriptKind
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

        foreach (var import in state.Parse.Imports)
        {
            var spanId = Guid.NewGuid();
            var span = ToSpan(document, state.DocumentId, spanId, import.Span);
            spans.Add(span);

            var nodeId = Guid.NewGuid();
            nodes.Add(new Node
            {
                Id = nodeId,
                Kind = "ts_import",
                SpanId = spanId,
                Props = new JsonObject
                {
                    ["specifier"] = import.Specifier,
                    ["import_kind"] = import.ImportKind,
                    ["import_style"] = import.ImportStyle
                },
                CreatedAt = now,
                UpdatedAt = now
            });

            edges.Add(CreateHasPart(docNode.Id, nodeId, docNode.Id, ordinal++, now));
        }

        foreach (var decl in state.Parse.Declarations)
        {
            var spanId = Guid.NewGuid();
            var span = ToSpan(document, state.DocumentId, spanId, decl.Span);
            spans.Add(span);

            var declNodeId = Guid.NewGuid();
            nodes.Add(new Node
            {
                Id = declNodeId,
                Kind = $"ts_decl_{decl.DeclKind}",
                SpanId = spanId,
                Props = new JsonObject
                {
                    ["name"] = decl.Name ?? string.Empty,
                    ["decl_kind"] = decl.DeclKind,
                    ["is_exported"] = decl.IsExported,
                    ["export_kind"] = decl.ExportKind,
                    ["is_component"] = decl.IsComponent
                },
                CreatedAt = now,
                UpdatedAt = now
            });

            edges.Add(CreateHasPart(docNode.Id, declNodeId, docNode.Id, ordinal++, now));

            if (decl.Members.Count > 0)
            {
                var memberOrdinal = 0;
                foreach (var member in decl.Members)
                {
                    var memberSpanId = Guid.NewGuid();
                    var memberSpan = ToSpan(document, state.DocumentId, memberSpanId, member.Span);
                    spans.Add(memberSpan);

                    var memberNodeId = Guid.NewGuid();
                    nodes.Add(new Node
                    {
                        Id = memberNodeId,
                        Kind = $"ts_member_{member.MemberKind}",
                        SpanId = memberSpanId,
                        Props = new JsonObject
                        {
                            ["name"] = member.Name,
                            ["member_kind"] = member.MemberKind
                        },
                        CreatedAt = now,
                        UpdatedAt = now
                    });

                    edges.Add(CreateHasPart(declNodeId, memberNodeId, docNode.Id, memberOrdinal++, now));
                }
            }
        }

        return new Records
        {
            Artifacts = [artifact],
            Nodes = [.. nodes],
            Spans = [.. spans],
            Edges = [.. edges]
        };
    }

    public IEnumerable<FormatSqlScript> GetSchemaScripts()
    {
        yield break;
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

    private static string BuildHeadline(DocumentModel document, TypeScriptDocumentState state)
    {
        var fileName = SafeFileName(document.Uri);
        var exports = state.Parse.Declarations.Where(d => d.IsExported && !string.IsNullOrWhiteSpace(d.Name))
            .Select(d => d.Name!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var exportText = exports.Count == 0 ? "exports: -" : $"exports: {string.Join(", ", exports.Take(4))}";
        var importText = $"imports: {state.Parse.Imports.Count}";
        var diag = state.Parse.Diagnostics.Count > 0 ? $"[⚠️ {state.Parse.Diagnostics.Count}]" : null;

        return string.Join(" | ", new[] { fileName, exportText, importText, diag }.Where(s => !string.IsNullOrWhiteSpace(s)));
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
        var decls = state.Parse.Declarations.Select(d => d.Name ?? $"<{d.DeclKind}>").ToList();
        var outline = decls.Count == 0
            ? "no declarations"
            : string.Join(" → ", decls.Take(6));

        return $"{fileName} → {outline}";
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
