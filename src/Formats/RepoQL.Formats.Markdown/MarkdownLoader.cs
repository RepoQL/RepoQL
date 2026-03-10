using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Extensions.Yaml;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RepoQL.Contracts;
using RepoQL.Contracts.Models;
using RepoQL.Templating;
using RepoQL.Templating.Filters;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace RepoQL.Formats.Markdown;

public sealed partial class MarkdownLoader : IFormatLoader, IFormatMaterializer, IFormatSchemaProvider
{
    internal const string StateMetadataKey = "markdown.state";

    private readonly ILogger<MarkdownLoader> _logger;

    private static readonly SemanticMediaType MarkdownMediaType = SemanticMediaType
        .Create("text", "markdown")
        .WithKind("markdown.doc");

    private readonly MarkdownPipeline _pipeline;

    private readonly LiquidTemplateRenderer _renderer = new(
        assembly: typeof(MarkdownLoader).Assembly,
        resourceRoot: "RepoQL.Formats.Markdown.Templates",
        configure: StandardFilters.RegisterAll);

    private static readonly Lazy<string> MarkdownViewsSql = new(
        () => ReadEmbeddedResource("RepoQL.Formats.Markdown.Schema.markdown_views.sql"));

    private static readonly (string Pattern, string Label)[] TitleTypePatterns =
    [
        ("proposal", "proposal"),
        ("guide", "guide"),
        ("tutorial", "guide"),
        ("reference", "reference"),
        ("api", "reference"),
        ("checklist", "runbook"),
        ("runbook", "runbook"),
        ("architecture", "architecture"),
        ("design", "architecture")
    ];

    private static readonly Regex CapsuleHeadingPattern = new(
        @"^Capsule:\s*([A-Z][A-Za-z0-9]+)",
        RegexOptions.Compiled);

    private static readonly Regex SeeAlsoPattern = new(
        @"SeeAlso:\s*(.+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex CapsuleNamePattern = new(
        @"`?([A-Z][A-Za-z0-9]+)`?",
        RegexOptions.Compiled);

    public MarkdownLoader(ILogger<MarkdownLoader>? logger = null)
    {
        _logger = logger ?? NullLogger<MarkdownLoader>.Instance;
        _pipeline = new MarkdownPipelineBuilder()
            .UsePreciseSourceLocation()
            .UseYamlFrontMatter()
            .UsePipeTables()
            .UseGridTables()
            .UseAutoLinks()
            .UseTaskLists()
            .UseEmphasisExtras()
            .UseListExtras()
            .UseDefinitionLists()
            .UseMediaLinks()
            .Build();
    }

    public bool Supports(SemanticMediaType mediaType)
    {
        ArgumentNullException.ThrowIfNull(mediaType);

        if (string.Equals(mediaType.Kind, MarkdownMediaType.Kind, StringComparison.OrdinalIgnoreCase))
            return true;

        return string.Equals(mediaType.Type, MarkdownMediaType.Type, StringComparison.OrdinalIgnoreCase)
               && string.Equals(mediaType.Subtype, MarkdownMediaType.Subtype, StringComparison.OrdinalIgnoreCase);
    }

    public async Task<bool> CanLoadAsync(DiscoveredArtifact artifact, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);

        var name = artifact.File.Name.ToLowerInvariant();
        if (name.EndsWith(".md") || name.EndsWith(".markdown") || name.EndsWith(".mdown") || name.EndsWith(".mkd"))
        {
            artifact.MediaType = MarkdownMediaType;
            return await Task.FromResult(true);
        }

        if (artifact.MediaType is not null &&
            string.Equals(artifact.MediaType.Type, "text", StringComparison.OrdinalIgnoreCase) &&
            (string.Equals(artifact.MediaType.Subtype, "markdown", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(artifact.MediaType.Kind, "markdown.doc", StringComparison.OrdinalIgnoreCase)))
        {
            artifact.MediaType = artifact.MediaType.WithKind("markdown.doc");
            return true;
        }
        return false;
    }

    public async Task<DocumentModel> LoadAsync(DiscoveredArtifact artifact, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        if (artifact.RepoUri is null)
            throw new InvalidOperationException("RepoUri required to load markdown.");

        var mediaType = artifact.MediaType ?? MarkdownMediaType;

        var loaded = await FileContentReader.ReadAllTextWithDigestAsync(
            artifact.File,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var text = loaded.Text;
        var digest = loaded.Digest;
        var docId = Guid.NewGuid();
        var markdigDoc = Markdig.Markdown.Parse(text, _pipeline);

        var lineMap = new TextLineMap(text);
        var pendingHeadings = new List<PendingHeading>();
        var headings = new List<HeadingInfo>();
        var links = new List<LinkInfo>();
        var blocks = new List<CodeBlockInfo>();
        var headingBySlug = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);

        var documentProps = new JsonObject
        {
            ["media_type"] = mediaType.ToString(),
            ["byte_size"] = artifact.File.Length
        };

        foreach (var block in markdigDoc)
        {
            switch (block)
            {
                case YamlFrontMatterBlock yfm:
                    TryLoadFrontMatter(text, yfm, documentProps);
                    break;
                case HeadingBlock heading:
                {
                    var headingText = InlineText(heading.Inline).Trim();
                    if (!string.IsNullOrWhiteSpace(headingText))
                    {
                        var slug = MarkdownTextUtilities.Slug(headingText);
                        var span = ToDocumentSpan(lineMap, heading.Span);
                        var spanId = Guid.NewGuid();
                        var nodeId = Guid.NewGuid();
                        pendingHeadings.Add(new PendingHeading(nodeId, spanId, heading.Level, headingText, slug, span));
                        headingBySlug[slug] = nodeId;
                    }
                    break;
                }
                case FencedCodeBlock fenced:
                {
                    var span = ToDocumentSpan(lineMap, fenced.Span);
                    var spanId = Guid.NewGuid();
                    var nodeId = Guid.NewGuid();
                    blocks.Add(new CodeBlockInfo(
                        nodeId,
                        spanId,
                        fenced.Info ?? string.Empty,
                        true,
                        fenced.Lines.Count,
                        fenced.Arguments?.ToString() ?? string.Empty,
                        span));
                    break;
                }
                case CodeBlock indented:
                {
                    var span = ToDocumentSpan(lineMap, indented.Span);
                    var spanId = Guid.NewGuid();
                    var nodeId = Guid.NewGuid();
                    blocks.Add(new CodeBlockInfo(
                        nodeId,
                        spanId,
                        string.Empty,
                        false,
                        indented.Lines.Count,
                        string.Empty,
                        span));
                    break;
                }
            }
        }

        if (pendingHeadings.Count > 0)
        {
            for (var i = 0; i < pendingHeadings.Count; i++)
            {
                var current = pendingHeadings[i];
                // Find next heading at same level or higher (lower number = higher level)
                var sectionEnd = lineMap.TextLength;
                for (var j = i + 1; j < pendingHeadings.Count; j++)
                {
                    if (pendingHeadings[j].Level <= current.Level)
                    {
                        sectionEnd = pendingHeadings[j].HeadingSpan.StartChar;
                        break;
                    }
                }
                sectionEnd = Math.Max(sectionEnd, current.HeadingSpan.StartChar);
                var sectionSpan = lineMap.GetSpan(current.HeadingSpan.StartChar, sectionEnd);
                headings.Add(new HeadingInfo(
                    current.NodeId,
                    current.SpanId,
                    current.Level,
                    current.Text,
                    current.Slug,
                    current.HeadingSpan,
                    sectionSpan));
            }
        }

        foreach (var link in markdigDoc.Descendants<LinkInline>())
        {
            var span = ToDocumentSpan(lineMap, link.Span);
            var spanId = Guid.NewGuid();
            var nodeId = Guid.NewGuid();
            links.Add(new LinkInfo(nodeId, spanId, link.Url ?? string.Empty, link.Title ?? string.Empty, InlineText(link), span, link.IsImage));
        }
        var imagesCount = markdigDoc.Descendants<LinkInline>().Count(l => l.IsImage);
        var tablesCount = markdigDoc.Descendants<Table>().Count();

        // Extract capsules from headings matching "Capsule: Name" pattern
        var capsules = ExtractCapsules(headings, markdigDoc, text, lineMap);

        var surface = new MarkdownSurface
        {
            DocumentId = docId,
            DocumentProperties = documentProps,
            Headings = headings,
            Links = links,
            CodeBlocks = blocks,
            Capsules = capsules
        };

        var state = new MarkdownDocumentState
        {
            Surface = surface,
            Digest = digest,
            Size = loaded.ByteLength,
            MediaType = mediaType,
            StoreUri = artifact.RepoUri.ToString()
        };

        var metadata = new Dictionary<string, object?>
        {
            [StateMetadataKey] = state,
            ["markdown.headingIndex"] = headingBySlug
        };

        return new DocumentModel(artifact.RepoUri, mediaType, text, markdigDoc, metadata);
    }

    public Records Materialize(DocumentModel document)
    {
        if (document.GetMetadataOrDefault<MarkdownDocumentState>(StateMetadataKey) is not { } state)
            throw new InvalidOperationException("Markdown document missing state metadata.");

        // Compute x-ray fields via Liquid templates (best effort)
        string? headline = null;
        string? summary = null;
        string? structure = null;
        try
        {
            var fileName = GetFileName(document.Uri);
            // Compute additional counts from SyntaxTree when available
            var imagesCount = 0;
            var tablesCount = 0;
            try
            {
                if (document.SyntaxTree is MarkdownDocument mdDoc)
                {
                    imagesCount = mdDoc.Descendants<LinkInline>().Count(l => l.IsImage);
                    tablesCount = mdDoc.Descendants<Table>().Count();
                }
            }
            catch { }

            var langCounts = state.Surface.CodeBlocks
                .Select(cb => (cb.Language ?? string.Empty).Trim())
                .Where(s => s.Length > 0)
                .GroupBy(s => s, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count())
                .ToDictionary(g => g.Key, g => g.Count());

            var exploreMetadata = BuildExploreMetadata(state, fileName);

            string? topLang = null;
            if (langCounts.Count > 0)
            {
                topLang = langCounts
                    .OrderByDescending(kv => kv.Value)
                    .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                    .First().Key;
            }

            // Calculate token count for the text content
            var tokenCount = TokenEstimator.EstimateTokensSafe(document.Text);

            var model = new Dictionary<string, object?>
            {
                ["file_name"] = fileName,
                ["media_kind"] = state.MediaType.Kind ?? string.Empty,
                ["media_base"] = $"{state.MediaType.Type}/{state.MediaType.Subtype}",
                ["size_bytes"] = state.Size,
                ["line_count"] = document.LineMap.LineCount,
                ["token_count"] = tokenCount ?? 0,
                ["headings_count"] = state.Surface.Headings.Count,
                ["codeblocks_count"] = state.Surface.CodeBlocks.Count,
                ["links_count"] = state.Surface.Links.Count,
                ["images_count"] = imagesCount,
                ["tables_count"] = tablesCount,
                ["frontmatter_keys"] = exploreMetadata.FrontmatterPairs.Count,
                ["code_lang_counts"] = langCounts,
                ["title"] = exploreMetadata.Title,
                ["display_title"] = exploreMetadata.DisplayTitle,
                ["document_type_label"] = exploreMetadata.DocumentType,
                ["topics"] = exploreMetadata.Topics,
                ["top_lang"] = topLang,
                ["tags"] = exploreMetadata.TagsForHeadline,
                ["tags_or_headings"] = exploreMetadata.TagsOrHeadings,
                ["headline_uses_tags"] = exploreMetadata.TagsForHeadline.Count > 0,
                ["important_headings"] = exploreMetadata.ImportantHeadings,
                ["frontmatter_pairs"] = exploreMetadata.FrontmatterPairs,
                ["headings"] = state.Surface.Headings.Select(h => new Dictionary<string, object?>
                {
                    ["level"] = h.Level,
                    ["text"] = h.Text,
                    ["indent"] = new string(' ', Math.Max(0, (h.Level - 1) * 2))
                }).ToList(),
                ["capsules"] = state.Surface.Capsules.Select(c => new Dictionary<string, object?>
                {
                    ["name"] = c.Name,
                    ["invariant"] = c.Invariant
                }).ToList(),
                ["capsules_count"] = state.Surface.Capsules.Count
            };

            headline = _renderer.Render("explore/headline", model);
            summary = _renderer.Render("explore/summary", model);
            structure = _renderer.Render("explore/structure", model);
            }
            catch
            {
                // ignore templating errors; x-ray is best-effort
            }

        // Get token count from model (already calculated above, default to null if not calculated)
        var finalTokenCount = TokenEstimator.EstimateTokensSafe(document.Text);

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
            TokenCount = finalTokenCount
        };

        var nodes = new List<Node>();
        var spans = new List<Span>();
        var edges = new List<Edge>();
        var now = DateTimeOffset.UtcNow;

        var docNode = new Node
        {
            Id = state.Surface.DocumentId,
            Kind = "document",
            Uri = document.Uri,
            ArtifactId = artifact.Id,
            Props = state.Surface.DocumentProperties,
            CreatedAt = now,
            UpdatedAt = now
        };
        nodes.Add(docNode);

        var ordinal = 0;
        var assignedHeadingUris = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var heading in state.Surface.Headings)
        {
            var span = ToSpan(document, heading.SectionSpan, docNode.Id, heading.SpanId);
            spans.Add(span);

            RepoUri? headingUri = null;
            if (!string.IsNullOrWhiteSpace(heading.Slug))
            {
                var candidate = RepoUri.FromAnchor(new Uri(document.Uri.Container.AbsoluteUri), heading.Slug);
                if (assignedHeadingUris.Add(candidate.AbsoluteUri))
                    headingUri = candidate;
            }

            var node = new Node
            {
                Id = heading.NodeId,
                Kind = "md_heading",
                Uri = headingUri,
                SpanId = heading.SpanId,
                Props = new JsonObject
                {
                    ["level"] = heading.Level,
                    ["text"] = heading.Text,
                    ["slug"] = heading.Slug
                },
                Headline = BuildHeadingHeadline(heading),
                Structure = BuildHeadingStructure(heading, state.Surface),
                CreatedAt = now,
                UpdatedAt = now
            };
            nodes.Add(node);
            edges.Add(CreateHasPart(docNode.Id, node.Id, docNode.Id, ordinal++, now));
        }

        foreach (var block in state.Surface.CodeBlocks)
        {
            var span = ToSpan(document, block.Span, docNode.Id, block.SpanId);
            spans.Add(span);

            var node = new Node
            {
                Id = block.NodeId,
                Kind = "md_code_block",
                SpanId = block.SpanId,
                Props = new JsonObject
                {
                    ["language"] = block.Language,
                    ["fenced"] = block.IsFenced,
                    ["lines"] = block.LineCount,
                    ["info"] = block.Info
                },
                CreatedAt = now,
                UpdatedAt = now
            };
            nodes.Add(node);
            edges.Add(CreateHasPart(docNode.Id, node.Id, docNode.Id, ordinal++, now));
        }

        var headingIndex = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        foreach (var heading in state.Surface.Headings)
        {
            if (string.IsNullOrWhiteSpace(heading.Slug))
                continue;

            if (!headingIndex.ContainsKey(heading.Slug))
                headingIndex[heading.Slug] = heading.NodeId;
        }

        foreach (var link in state.Surface.Links)
        {
            var span = ToSpan(document, link.Span, docNode.Id, link.SpanId);
            spans.Add(span);

            var node = new Node
            {
                Id = link.NodeId,
                Kind = "md_link",
                SpanId = link.SpanId,
                Props = new JsonObject
                {
                    ["href"] = link.Href,
                    ["title"] = link.Title,
                    ["text"] = link.Text
                },
                CreatedAt = now,
                UpdatedAt = now
            };
            nodes.Add(node);
            edges.Add(CreateHasPart(docNode.Id, node.Id, docNode.Id, ordinal++, now));

            if (link.Href.StartsWith('#'))
            {
                var slug = MarkdownTextUtilities.Slug(link.Href.TrimStart('#'));
                if (headingIndex.TryGetValue(slug, out var headingId))
                {
                    edges.Add(new Edge
                    {
                        Id = Guid.NewGuid(),
                        SrcId = node.Id,
                        DstId = headingId,
                        Type = "REFERS_TO",
                        IsComposition = false,
                        Ordinal = null,
                        ScopeDocumentId = docNode.Id,
                        CreatedAt = now
                    });
                }
            }
        }

        // Build capsule index for REFERS_TO edges
        var capsuleIndex = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        foreach (var capsule in state.Surface.Capsules)
        {
            capsuleIndex[capsule.Name] = capsule.NodeId;
        }

        // Materialize capsules
        foreach (var capsule in state.Surface.Capsules)
        {
            var span = ToSpan(document, capsule.CapsuleSpan, docNode.Id, capsule.SpanId);
            spans.Add(span);

            var seeAlsoArray = new JsonArray();
            foreach (var refName in capsule.SeeAlso)
            {
                seeAlsoArray.Add(JsonValue.Create(refName));
            }

            var node = new Node
            {
                Id = capsule.NodeId,
                Kind = "md_capsule",
                SpanId = capsule.SpanId,
                Props = new JsonObject
                {
                    ["name"] = capsule.Name,
                    ["invariant"] = capsule.Invariant,
                    ["example"] = capsule.Example,
                    ["has_boundary"] = capsule.HasBoundary,
                    ["boundary_text"] = capsule.BoundaryText,
                    ["see_also"] = seeAlsoArray,
                    ["heading_level"] = capsule.HeadingLevel
                },
                Headline = BuildCapsuleHeadline(capsule),
                Structure = BuildCapsuleStructure(capsule),
                CreatedAt = now,
                UpdatedAt = now
            };
            nodes.Add(node);
            edges.Add(CreateHasPart(docNode.Id, node.Id, docNode.Id, ordinal++, now));

            // Create REFERS_TO edges for SeeAlso references
            foreach (var refName in capsule.SeeAlso)
            {
                if (capsuleIndex.TryGetValue(refName, out var targetId) && targetId != capsule.NodeId)
                {
                    edges.Add(new Edge
                    {
                        Id = Guid.NewGuid(),
                        SrcId = capsule.NodeId,
                        DstId = targetId,
                        Type = "REFERS_TO",
                        IsComposition = false,
                        Ordinal = null,
                        ScopeDocumentId = docNode.Id,
                        CreatedAt = now
                    });
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

    private static string GetFileName(RepoUri uri)
    {
        try
        {
            if (uri.IsFile)
            {
                var lp = uri.LocalPath;
                if (!string.IsNullOrEmpty(lp)) return Path.GetFileName(lp);
            }
        }
        catch { }
        var ap = Uri.UnescapeDataString(uri.AbsolutePath);
        var slash = ap.LastIndexOf('/') >= 0 ? ap[(ap.LastIndexOf('/') + 1)..] : ap;
        return string.IsNullOrEmpty(slash) ? uri.AbsoluteUri : slash;
    }

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

    private ExploreMetadata BuildExploreMetadata(MarkdownDocumentState state, string fileName)
    {
        var frontmatter = GetFrontmatter(state.Surface.DocumentProperties);
        var frontmatterPairs = BuildFrontmatterPairs(frontmatter);

        string? title = null;
        if (frontmatter?.TryGetPropertyValue("title", out var ft) == true && ft is not null)
        {
            title = ft.ToString();
        }
        title ??= state.Surface.Headings.FirstOrDefault(h => h.Level == 1)?.Text;
        title ??= fileName;

        var description = TryGetFrontmatterString(frontmatter, "description");
        var displayTitle = !string.IsNullOrWhiteSpace(description)
            ? description!.Trim()
            : title;
        if (string.IsNullOrWhiteSpace(displayTitle))
        {
            displayTitle = fileName;
        }

        var documentType = DetermineDocumentType(frontmatter, title, state.MediaType);

        var tags = ExtractFrontmatterTags(frontmatter)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(t => t.Trim())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Take(4)
            .ToList();

        var importantHeadings = SelectImportantHeadings(state.Surface.Headings, displayTitle);
        var tagsOrHeadings = tags.Count > 0
            ? tags
            : importantHeadings.Take(8).ToList();

        var topics = importantHeadings.Take(5).ToList();
        if (topics.Count == 0)
        {
            topics = state.Surface.Headings
                .Where(h => h.Level >= 2)
                .Select(h => h.Text?.Trim())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(5)
                .OfType<string>()
                .ToList();
        }

        return new ExploreMetadata(
            Title: title,
            DisplayTitle: displayTitle,
            DocumentType: documentType,
            TagsForHeadline: tags,
            TagsOrHeadings: tagsOrHeadings,
            ImportantHeadings: importantHeadings,
            Topics: topics,
            FrontmatterPairs: frontmatterPairs);
    }

    private static JsonObject? GetFrontmatter(JsonObject props)
    {
        try
        {
            if (props.TryGetPropertyValue("frontmatter", out var node) && node is JsonObject fm)
            {
                return fm;
            }
        }
        catch { }
        return null;
    }

    private static IReadOnlyList<string> BuildFrontmatterPairs(JsonObject? frontmatter)
    {
        if (frontmatter is null || frontmatter.Count == 0)
            return Array.Empty<string>();

        var pairs = new List<string>(frontmatter.Count);
        foreach (var kv in frontmatter)
        {
            var rendered = RenderFrontmatterValue(kv.Value);
            if (string.IsNullOrWhiteSpace(rendered))
                continue;
            pairs.Add($"{kv.Key}: {rendered}");
        }

        return pairs.Take(5).ToList();
    }

    private static string? RenderFrontmatterValue(JsonNode? node)
    {
        if (node is null) return null;
        switch (node)
        {
            case JsonValue value:
                return value.ToString();
            case JsonArray array:
                var items = array
                    .Select(RenderFrontmatterValue)
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Take(5)
                    .ToArray();
                return items.Length == 0 ? null : string.Join(", ", items);
            case JsonObject obj:
                var pairs = obj.Select(kvp => $"{kvp.Key}: {RenderFrontmatterValue(kvp.Value)}")
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Take(3)
                    .ToArray();
                return pairs.Length == 0 ? null : string.Join("; ", pairs);
            default:
                return node.ToString();
        }
    }

    private static string? TryGetFrontmatterString(JsonObject? frontmatter, string key)
    {
        if (frontmatter is null)
            return null;
        if (frontmatter.TryGetPropertyValue(key, out var value) && value is not null)
        {
            var text = value.ToString();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        return null;
    }

    private static string DetermineDocumentType(JsonObject? frontmatter, string? title, SemanticMediaType mediaType)
    {
        var fmType = TryGetFrontmatterString(frontmatter, "type");
        if (!string.IsNullOrWhiteSpace(fmType))
            return fmType!;

        if (!string.IsNullOrWhiteSpace(title))
        {
            var normalized = title.ToLowerInvariant();
            foreach (var (pattern, label) in TitleTypePatterns)
            {
                if (normalized.Contains(pattern))
                    return label;
            }
        }

        return mediaType.Kind ?? $"{mediaType.Type}/{mediaType.Subtype}";
    }

    private static List<string> ExtractFrontmatterTags(JsonObject? frontmatter)
    {
        var tags = new List<string>();
        if (frontmatter is null)
            return tags;

        if (frontmatter.TryGetPropertyValue("tags", out var tv) && tv is not null)
            tags.AddRange(ExtractTags(tv));
        if (tags.Count == 0 && frontmatter.TryGetPropertyValue("keywords", out var kv) && kv is not null)
            tags.AddRange(ExtractTags(kv));

        return tags;
    }

    private static IReadOnlyList<string> SelectImportantHeadings(IReadOnlyList<HeadingInfo> headings, string? queryText)
    {
        // Simple selection: return first 8 distinct H2+ headings in document order
        return headings
            .Where(h => h.Level >= 2)
            .Select(h => h.Text?.Trim())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .OfType<string>()
            .ToList();
    }

    private static string BuildHeadingHeadline(HeadingInfo heading)
    {
        var text = heading.Text;
        var prefix = $"H{heading.Level}";
        if (string.IsNullOrWhiteSpace(text))
            return prefix;
        return $"{prefix} · {text.Trim()}";
    }

    private static string BuildHeadingStructure(HeadingInfo heading, MarkdownSurface surface)
    {
        var parts = new List<string>();

        // Find direct child headings (one level deeper, within this section)
        var childHeadings = surface.Headings
            .Where(h => h.Level == heading.Level + 1 &&
                        h.SectionSpan.StartLine >= heading.SectionSpan.StartLine &&
                        h.SectionSpan.EndLine <= heading.SectionSpan.EndLine)
            .Select(h => h.Text.Trim())
            .ToList();

        if (childHeadings.Count > 0)
        {
            parts.Add(string.Join(", ", childHeadings));
        }

        // Count code blocks in this section
        var codeBlockCount = surface.CodeBlocks.Count(cb =>
            cb.Span.StartLine >= heading.SectionSpan.StartLine &&
            cb.Span.EndLine <= heading.SectionSpan.EndLine);
        if (codeBlockCount > 0)
            parts.Add($"{codeBlockCount} code");

        return string.Join(" | ", parts);
    }

    private sealed record ExploreMetadata(
        string Title,
        string DisplayTitle,
        string DocumentType,
        IReadOnlyList<string> TagsForHeadline,
        IReadOnlyList<string> TagsOrHeadings,
        IReadOnlyList<string> ImportantHeadings,
        IReadOnlyList<string> Topics,
        IReadOnlyList<string> FrontmatterPairs);

    private static Span ToSpan(DocumentModel document, DocumentSpan span, Guid documentId, Guid spanId)
        => new()
        {
            Id = spanId,
            DocumentId = documentId,
            StartLine = span.StartLine,
            StartColumn = span.StartColumn,
            EndLine = span.EndLine,
            EndColumn = span.EndColumn,
            StartByte = CalculateUtf8Bytes(document.Text, span.StartChar),
            EndByte = CalculateUtf8Bytes(document.Text, span.EndChar)
        };

    private static long CalculateUtf8Bytes(string text, int chars)
        => Encoding.UTF8.GetByteCount(text.AsSpan(0, Math.Min(text.Length, chars)));

    private static DocumentSpan ToDocumentSpan(TextLineMap map, Markdig.Syntax.SourceSpan span)
    {
        var start = Math.Max(0, span.Start);
        var end = span.End >= span.Start ? span.End + 1 : span.Start;
        end = Math.Max(end, start);
        end = Math.Min(end, map.TextLength);
        return map.GetSpan(start, end);
    }

    private sealed record PendingHeading(
        Guid NodeId,
        Guid SpanId,
        int Level,
        string Text,
        string Slug,
        DocumentSpan HeadingSpan);

    private static bool TryLoadFrontMatter(string text, YamlFrontMatterBlock block, JsonObject props)
    {
        try
        {
            var yaml = ExtractYamlText(text, block);
            if (string.IsNullOrWhiteSpace(yaml)) return false;
            var json = YamlToJson(yaml);
            if (json is not null) 
            {
                var clone = JsonNode.Parse(json.ToJsonString());
                if (clone is not null)
                    props["frontmatter"] = clone;
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("AOT", "IL3050", Justification = "YAML frontmatter deserialization uses reflection, not AOT-compatible by design")]
    internal static JsonNode? YamlToJson(string yaml)
    {
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
        var result = deserializer.Deserialize<object?>(yaml);
        var json = ToJsonNode(result);
        return NormalizeScalarNodes(json);
    }

    private static JsonNode? NormalizeScalarNodes(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return null;
            case JsonObject obj:
                foreach (var property in obj.ToList())
                {
                    var normalized = NormalizeScalarNodes(property.Value);
                    if (!ReferenceEquals(property.Value, normalized))
                    {
                        obj[property.Key] = normalized;
                    }
                }
                return obj;
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    var original = array[i];
                    var normalized = NormalizeScalarNodes(original);
                    if (!ReferenceEquals(original, normalized))
                    {
                        array[i] = normalized;
                    }
                }
                return array;
            case JsonValue value:
                if (value.TryGetValue<string>(out var str))
                {
                    if (bool.TryParse(str, out var boolValue))
                        return JsonValue.Create(boolValue);
                    if (long.TryParse(str, NumberStyles.Integer, CultureInfo.InvariantCulture, out var longValue))
                        return JsonValue.Create(longValue);
                    if (double.TryParse(str, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var doubleValue))
                        return JsonValue.Create(doubleValue);
                }

                return value;
            default:
                return node;
        }
    }

    private static JsonNode? ToJsonNode(object? obj)
    {
        if (obj is null) return null;
        switch (obj)
        {
            case string s: return JsonValue.Create(s);
            case bool b: return JsonValue.Create(b);
            case int i: return JsonValue.Create(i);
            case long l: return JsonValue.Create(l);
            case double d: return JsonValue.Create(d);
            case IEnumerable<object?> seq:
                var arr = new JsonArray();
                foreach (var item in seq)
                {
                    arr.Add(ToJsonNode(item));
                }
                return arr;
            case IDictionary<object, object?> map:
                var objNode = new JsonObject();
                foreach (var kv in map)
                {
                    objNode[kv.Key?.ToString() ?? string.Empty] = ToJsonNode(kv.Value);
                }
                return objNode;
            case IDictionary<string, object?> strMap:
                var strObj = new JsonObject();
                foreach (var kv in strMap)
                {
                    strObj[kv.Key] = ToJsonNode(kv.Value);
                }
                return strObj;
            default:
                var text = obj?.ToString() ?? string.Empty;
                if (bool.TryParse(text, out var boolValue))
                {
                    return JsonValue.Create(boolValue);
                }

                if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var longValue))
                {
                    return JsonValue.Create(longValue);
                }

                if (double.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var doubleValue))
                {
                    return JsonValue.Create(doubleValue);
                }

                return JsonValue.Create(text);
        }
    }

    private static string ExtractYamlText(string text, YamlFrontMatterBlock block)
    {
        var start = Math.Max(0, block.Span.Start);
        var endIncl = Math.Min(text.Length - 1, block.Span.End);
        if (endIncl < start) return string.Empty;
        var raw = text.Substring(start, endIncl - start + 1);
        var lines = raw.Replace("\r\n", "\n").Split('\n');
        var list = new List<string>(lines.Length);
        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();
            if (i == 0 && trimmed == "---") continue;
            if (i == lines.Length - 1 && (trimmed == "---" || trimmed == "...")) continue;
            list.Add(lines[i]);
        }
        return string.Join("\n", list).Trim();
    }

    private static string InlineText(ContainerInline? root)
    {
        if (root is null) return string.Empty;
        var sb = new StringBuilder();
        foreach (var literal in root.Descendants<LiteralInline>())
        {
            sb.Append(literal.Content.ToString());
        }
        return sb.ToString();
    }

    private static IEnumerable<string> ExtractTags(JsonNode node)
    {
        if (node is null) yield break;
        if (node is JsonArray arr)
        {
            foreach (var v in arr)
            {
                var s = v?.ToString();
                if (!string.IsNullOrWhiteSpace(s)) yield return s.Trim();
            }
            yield break;
        }
        var str = node.ToString();
        if (!string.IsNullOrWhiteSpace(str))
        {
            foreach (var part in str.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!string.IsNullOrWhiteSpace(part)) yield return part.Trim();
            }
        }
    }

    public IEnumerable<FormatSqlScript> GetSchemaScripts()
    {
        yield return new FormatSqlScript("markdown_views", MarkdownViewsSql.Value);
    }

    private static string ReadEmbeddedResource(string resourceName)
    {
        using var stream = typeof(MarkdownLoader).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded SQL resource {resourceName} was not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static List<CapsuleInfo> ExtractCapsules(
        IReadOnlyList<HeadingInfo> headings,
        MarkdownDocument markdigDoc,
        string text,
        TextLineMap lineMap)
    {
        var capsules = new List<CapsuleInfo>();

        foreach (var heading in headings)
        {
            var match = CapsuleHeadingPattern.Match(heading.Text);
            if (!match.Success)
                continue;

            var capsuleName = match.Groups[1].Value;
            var nodeId = Guid.NewGuid();
            var spanId = Guid.NewGuid();

            // Extract content from the section
            var sectionStart = heading.SectionSpan.StartChar;
            var sectionEnd = heading.SectionSpan.EndChar;
            var sectionText = text.Substring(sectionStart, Math.Min(sectionEnd - sectionStart, text.Length - sectionStart));

            // Parse capsule sections
            var (invariant, example, hasBoundary, boundaryText, seeAlso) = ParseCapsuleSections(sectionText);

            if (string.IsNullOrWhiteSpace(invariant))
                continue; // Skip if no invariant found - not a valid capsule

            capsules.Add(new CapsuleInfo(
                nodeId,
                spanId,
                capsuleName,
                invariant,
                example,
                hasBoundary,
                boundaryText,
                seeAlso,
                heading.Level,
                heading.SectionSpan));
        }

        return capsules;
    }

    private static (string Invariant, string? Example, bool HasBoundary, string? BoundaryText, IReadOnlyList<string> SeeAlso) ParseCapsuleSections(string sectionText)
    {
        var lines = sectionText.Split('\n');
        var invariant = string.Empty;
        string? example = null;
        var hasBoundary = false;
        string? boundaryText = null;
        var seeAlso = new List<string>();
        var depthBullets = new List<string>();

        var currentSection = CapsuleSection.None;
        var sectionContent = new StringBuilder();

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');
            var trimmed = line.Trim();

            // Check for section markers
            if (trimmed.StartsWith("**Invariant**", StringComparison.OrdinalIgnoreCase))
            {
                currentSection = CapsuleSection.Invariant;
                sectionContent.Clear();
                continue;
            }
            if (trimmed.StartsWith("**Example**", StringComparison.OrdinalIgnoreCase))
            {
                if (currentSection == CapsuleSection.Invariant)
                    invariant = sectionContent.ToString().Trim();
                currentSection = CapsuleSection.Example;
                sectionContent.Clear();
                continue;
            }
            if (trimmed.StartsWith("**Depth**", StringComparison.OrdinalIgnoreCase))
            {
                if (currentSection == CapsuleSection.Invariant)
                    invariant = sectionContent.ToString().Trim();
                else if (currentSection == CapsuleSection.Example)
                    example = sectionContent.ToString().Trim();
                currentSection = CapsuleSection.Depth;
                sectionContent.Clear();
                continue;
            }

            // Check for boundary marker in example section
            if (currentSection == CapsuleSection.Example && trimmed.StartsWith("//BOUNDARY:", StringComparison.OrdinalIgnoreCase))
            {
                hasBoundary = true;
                boundaryText = trimmed.Substring("//BOUNDARY:".Length).Trim();
                continue;
            }

            // Collect content based on current section
            switch (currentSection)
            {
                case CapsuleSection.Invariant:
                case CapsuleSection.Example:
                    if (!string.IsNullOrWhiteSpace(line))
                        sectionContent.AppendLine(line);
                    break;
                case CapsuleSection.Depth:
                    if (trimmed.StartsWith("-") || trimmed.StartsWith("*"))
                    {
                        var bullet = trimmed.TrimStart('-', '*', ' ');
                        depthBullets.Add(bullet);

                        // Check for SeeAlso references
                        var seeAlsoMatch = SeeAlsoPattern.Match(bullet);
                        if (seeAlsoMatch.Success)
                        {
                            var references = seeAlsoMatch.Groups[1].Value;
                            foreach (Match nameMatch in CapsuleNamePattern.Matches(references))
                            {
                                seeAlso.Add(nameMatch.Groups[1].Value);
                            }
                        }
                    }
                    break;
            }
        }

        // Finalize last section
        if (currentSection == CapsuleSection.Invariant)
            invariant = sectionContent.ToString().Trim();
        else if (currentSection == CapsuleSection.Example)
            example = sectionContent.ToString().Trim();

        return (invariant, string.IsNullOrWhiteSpace(example) ? null : example, hasBoundary, boundaryText, seeAlso);
    }

    private enum CapsuleSection
    {
        None,
        Invariant,
        Example,
        Depth
    }

    private static string BuildCapsuleHeadline(CapsuleInfo capsule)
    {
        var invariantPreview = capsule.Invariant.Length > 80
            ? capsule.Invariant.Substring(0, 77) + "..."
            : capsule.Invariant;
        return $"Capsule: {capsule.Name} - {invariantPreview}";
    }

    private static string BuildCapsuleStructure(CapsuleInfo capsule)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"**Invariant**: {capsule.Invariant}");

        if (!string.IsNullOrEmpty(capsule.Example))
        {
            sb.AppendLine();
            sb.AppendLine($"**Example**: {capsule.Example}");
            if (capsule.HasBoundary && !string.IsNullOrEmpty(capsule.BoundaryText))
            {
                sb.AppendLine($"//BOUNDARY: {capsule.BoundaryText}");
            }
        }

        return sb.ToString().Trim();
    }

    [LoggerMessage(LogLevel.Warning, "Failed to parse {Name} as markdown")]
    partial void LogFailedToParseNameAsMarkdown(Exception ex, string name);

    [LoggerMessage(LogLevel.Warning, "Failed to load front matter from {Name}")]
    partial void LogFailedToLoadFrontmatter(Exception ex, string name);
}
