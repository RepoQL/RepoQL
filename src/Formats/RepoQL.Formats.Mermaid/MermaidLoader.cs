using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RepoQL.Contracts;
using RepoQL.Contracts.Models;
using RepoQL.Templating;
using RepoQL.Templating.Filters;

namespace RepoQL.Formats.Mermaid;

public sealed partial class MermaidLoader(ITemplateRenderer? renderer, ILogger<MermaidLoader>? logger = null) : IFormatLoader, IFormatMaterializer
{
    private ILogger<MermaidLoader> Logger { get; } = logger ?? NullLogger<MermaidLoader>.Instance;

    private readonly ITemplateRenderer? _renderer = renderer ?? new LiquidTemplateRenderer(
        assembly: typeof(MermaidLoader).Assembly,
        resourceRoot: "RepoQL.Formats.Mermaid.Templates",
        configure: StandardFilters.RegisterAll);

    public MermaidLoader() : this(null) { }

    private const string StateMetadataKey = "mermaid.state";

    private static readonly string[] Extensions = [".mmd", ".mermaid"];
    private static readonly SemanticMediaType MermaidMediaType = SemanticMediaType
        .Create("text", "mermaid")
        .WithKind("mermaid.doc");

    public bool Supports(SemanticMediaType mediaType)
    {
        ArgumentNullException.ThrowIfNull(mediaType);

        if (string.Equals(mediaType.Kind, MermaidMediaType.Kind, StringComparison.OrdinalIgnoreCase))
            return true;

        return string.Equals(mediaType.Type, MermaidMediaType.Type, StringComparison.OrdinalIgnoreCase)
               && string.Equals(mediaType.Subtype, MermaidMediaType.Subtype, StringComparison.OrdinalIgnoreCase);
    }

    public async Task<bool> CanLoadAsync(DiscoveredArtifact artifact, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);

        var name = artifact.File.Name.ToLowerInvariant();
        if (Extensions.Any(name.EndsWith))
        {
            artifact.MediaType = MermaidMediaType;
            return true;
        }

        if (artifact.MediaType is not null &&
            (string.Equals(artifact.MediaType.Subtype, "mermaid", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(artifact.MediaType.Kind, "mermaid.doc", StringComparison.OrdinalIgnoreCase)))
        {
            artifact.MediaType = artifact.MediaType.WithKind("mermaid.doc");
            return true;
        }

        // Only apply content heuristic if the file doesn't already have a specific media type classification.
        // Don't override well-established types (e.g., code.javascript, code.python) based on content patterns.
        // Only check content for truly unclassified files (no media type, or generic text/plain without specific kind).
        if (artifact.MediaType is not null &&
            !string.IsNullOrEmpty(artifact.MediaType.Kind) &&
            !string.Equals(artifact.MediaType.Kind, "plain.document", StringComparison.OrdinalIgnoreCase))
        {
            // File already has a specific classification, don't override
            return false;
        }

        try
        {
            await using var stream = artifact.File.CreateReadStream();
            using var reader = new StreamReader(stream);
            string? line;
            while ((line = await reader.ReadLineAsync(cancellationToken)) is not null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("flowchart", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.StartsWith("graph", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.StartsWith("sequenceDiagram", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.StartsWith("pie", StringComparison.OrdinalIgnoreCase))
                {
                    artifact.MediaType = MermaidMediaType;
                    return true;
                }
                break;
            }
        }
#pragma warning disable CA1031
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogCouldNotLoadMermaidDiagram(Logger, ex);
        }

        return false;
    }

    public async Task<DocumentModel> LoadAsync(DiscoveredArtifact artifact, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        if (artifact.RepoUri is null)
            throw new InvalidOperationException("RepoUri required to load mermaid diagrams.");

        var loaded = await FileContentReader.ReadAllTextWithDigestAsync(
            artifact.File,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var text = loaded.Text;
        var digest = loaded.Digest;
        var lang = new MermaidLanguage();
        var tree = lang.Parse(text);
        var root = (MDocument)tree.Root;

        var state = new MermaidDocumentState
        {
            DocumentId = Guid.NewGuid(),
            Ast = root,
            Digest = digest,
            Size = loaded.ByteLength,
            MediaType = artifact.MediaType ?? MermaidMediaType,
            StoreUri = artifact.RepoUri.ToString()
        };

        var metadata = new Dictionary<string, object?>
        {
            [StateMetadataKey] = state
        };

        return new DocumentModel(artifact.RepoUri, state.MediaType, text, tree, metadata);
    }

    public Records Materialize(DocumentModel document)
    {
        var state = document.GetMetadataOrDefault<MermaidDocumentState>(StateMetadataKey)
            ?? throw new InvalidOperationException("Mermaid document missing state metadata.");

        // X-ray via Liquid: headline, summary, structure (best effort)
        string? headline = null;
        string? summary = null;
        string? structure = null;

        // Calculate token count for the text content
        var tokenCount = TokenEstimator.EstimateTokensSafe(document.Text);

        try
        {
            if (_renderer is not null)
            {
                var fileName = GetFileName(document.Uri);
                var root = state.Ast;
                var flowNodes = new List<Dictionary<string, object?>>();
                var flowEdges = new List<Dictionary<string, object?>>();
                var participants = new List<Dictionary<string, object?>>();
                var messages = new List<Dictionary<string, object?>>();
                var slices = new List<Dictionary<string, object?>>();

                foreach (var s in root.Statements)
                {
                    switch (s)
                    {
                        case FlowNodeDecl n:
                            flowNodes.Add(new()
                            {
                                ["id"] = n.Id,
                                ["label"] = n.Label,
                                ["shape"] = n.ShapeOpen.ToString()
                            });
                            break;
                        case FlowEdge e:
                            flowEdges.Add(new()
                            {
                                ["src"] = e.Src,
                                ["dst"] = e.Dst,
                                ["arrow"] = e.Arrow,
                                ["label"] = e.MidLabel ?? string.Empty
                            });
                            break;
                        case SeqParticipant p:
                            participants.Add(new()
                            {
                                ["name"] = p.Name,
                                ["alias"] = p.Alias ?? string.Empty
                            });
                            break;
                        case SeqMessage m:
                            messages.Add(new()
                            {
                                ["from"] = m.From,
                                ["to"] = m.To,
                                ["arrow"] = m.Arrow,
                                ["text"] = m.Text
                            });
                            break;
                        case PieEntry pie:
                            slices.Add(new()
                            {
                                ["label"] = pie.LabelRaw,
                                ["value"] = pie.Value
                            });
                            break;
                    }
                }

                var model = new Dictionary<string, object?>
                {
                    ["file_name"] = fileName,
                    ["media_kind"] = state.MediaType.Kind ?? string.Empty,
                    ["media_base"] = $"{state.MediaType.Type}/{state.MediaType.Subtype}",
                    ["size_bytes"] = state.Size,
                    ["line_count"] = document.LineMap.LineCount,
                    ["token_count"] = tokenCount ?? 0,
                    ["diagram_kind"] = root.DiagramKind,
                    ["node_count"] = flowNodes.Count,
                    ["edge_count"] = flowEdges.Count,
                    ["participant_count"] = participants.Count,
                    ["message_count"] = messages.Count,
                    ["pie_count"] = slices.Count,
                    ["flow_nodes"] = flowNodes,
                    ["flow_edges"] = flowEdges,
                    ["participants"] = participants,
                    ["messages"] = messages,
                    ["slices"] = slices
                };

                headline = _renderer.RenderAsync("explore/headline", model).GetAwaiter().GetResult();
                summary = _renderer.RenderAsync("explore/summary", model).GetAwaiter().GetResult();
                structure = _renderer.RenderAsync("explore/structure", model).GetAwaiter().GetResult();
            }
        }
        catch
        {
            // ignore
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
            TokenCount = tokenCount
        };

        var docNode = new Node
        {
            Id = state.DocumentId,
            Kind = "document",
            Uri = document.Uri,
            ArtifactId = artifact.Id,
            Props = new System.Text.Json.Nodes.JsonObject
            {
                ["media_type"] = artifact.MediaType?.ToString(),
                ["byte_size"] = artifact.Size
            },
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        var nodes = new List<Node> { docNode };
        var spans = Array.Empty<Span>();
        var edges = new List<Edge>();
        var now = DateTimeOffset.UtcNow;
        var ordinal = 0;

        foreach (var statement in state.Ast.Statements)
        {
            switch (statement)
            {
                case FlowNodeDecl node:
                    nodes.Add(new Node
                    {
                        Id = Guid.NewGuid(),
                        Kind = "mmd_node",
                        Props = new System.Text.Json.Nodes.JsonObject
                        {
                            ["id"] = node.Id,
                            ["label"] = node.Label,
                            ["shape"] = node.ShapeOpen.ToString()
                        },
                        Headline = string.IsNullOrEmpty(node.Label) ? $"node {node.Id}" : $"{node.Id}: {node.Label}",
                        CreatedAt = now,
                        UpdatedAt = now
                    });
                    edges.Add(CreateComposition(docNode.Id, nodes[^1].Id, ordinal++, now));
                    break;
                case FlowEdge edge:
                    edges.Add(new Edge
                    {
                        Id = Guid.NewGuid(),
                        SrcId = docNode.Id,
                        DstId = docNode.Id,
                        Type = "MMD_EDGE",
                        IsComposition = false,
                        ScopeDocumentId = docNode.Id,
                        Props = new System.Text.Json.Nodes.JsonObject
                        {
                            ["src"] = edge.Src,
                            ["dst"] = edge.Dst,
                            ["arrow"] = edge.Arrow,
                            ["label"] = edge.MidLabel
                        },
                        CreatedAt = now
                    });
                    break;
                case SeqParticipant participant:
                    var partHeadline = string.IsNullOrEmpty(participant.Alias)
                        ? $"participant {participant.Name}"
                        : $"participant {participant.Name} as {participant.Alias}";
                    nodes.Add(new Node
                    {
                        Id = Guid.NewGuid(),
                        Kind = "mmd_participant",
                        Props = new System.Text.Json.Nodes.JsonObject
                        {
                            ["name"] = participant.Name,
                            ["alias"] = participant.Alias
                        },
                        Headline = partHeadline,
                        CreatedAt = now,
                        UpdatedAt = now
                    });
                    edges.Add(CreateComposition(docNode.Id, nodes[^1].Id, ordinal++, now));
                    break;
                case SeqMessage message:
                    edges.Add(new Edge
                    {
                        Id = Guid.NewGuid(),
                        SrcId = docNode.Id,
                        DstId = docNode.Id,
                        Type = "MMD_MESSAGE",
                        IsComposition = false,
                        ScopeDocumentId = docNode.Id,
                        Props = new System.Text.Json.Nodes.JsonObject
                        {
                            ["from"] = message.From,
                            ["to"] = message.To,
                            ["arrow"] = message.Arrow,
                            ["text"] = message.Text
                        },
                        CreatedAt = now
                    });
                    break;
                case PieEntry entry:
                    nodes.Add(new Node
                    {
                        Id = Guid.NewGuid(),
                        Kind = "mmd_pie_entry",
                        Props = new System.Text.Json.Nodes.JsonObject
                        {
                            ["label"] = entry.LabelRaw,
                            ["value"] = entry.Value
                        },
                        Headline = $"{entry.LabelRaw}: {entry.Value}",
                        CreatedAt = now,
                        UpdatedAt = now
                    });
                    edges.Add(CreateComposition(docNode.Id, nodes[^1].Id, ordinal++, now));
                    break;
            }
        }

        return new Records
        {
            Artifacts = [artifact],
            Nodes = nodes.ToArray(),
            Spans = spans,
            Edges = edges.ToArray()
        };
    }

    private static Edge CreateComposition(Guid parentId, Guid childId, int ordinal, DateTimeOffset now)
        => new()
        {
            Id = Guid.NewGuid(),
            SrcId = parentId,
            DstId = childId,
            Type = "HAS_PART",
            IsComposition = true,
            Ordinal = ordinal,
            ScopeDocumentId = parentId,
            CreatedAt = now
        };

    private string GetFileName(RepoUri uri)
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
#pragma warning disable CA1031
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogCouldNotGetFileName(Logger, ex);
        }
        var ap = Uri.UnescapeDataString(uri.AbsolutePath);
        var slash = ap.LastIndexOf('/') >= 0 ? ap[(ap.LastIndexOf('/') + 1)..] : ap;
        return string.IsNullOrEmpty(slash) ? uri.AbsoluteUri : slash;
    }

    [LoggerMessage(LogLevel.Warning, "Could not load mermaid diagram.")]
    static partial void LogCouldNotLoadMermaidDiagram(ILogger<MermaidLoader> logger, Exception ex);

    [LoggerMessage(LogLevel.Warning, "Could not get file name")]
    static partial void LogCouldNotGetFileName(ILogger<MermaidLoader> logger, Exception ex);
}
