using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using Markdig;
using Markdig.Extensions.Yaml;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RepoQL.Contracts;
using RepoQL.Contracts.Models;
using RepoQL.Templating;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace RepoQL.Formats.Markdown;

public sealed partial class MarkdownLoader(ILogger<MarkdownLoader>? logger = null) : IFormatLoader, IFormatMaterializer
{
    internal const string StateMetadataKey = "markdown.state";

    private ILogger<MarkdownLoader> Logger = logger ?? NullLogger<MarkdownLoader>.Instance;
    private static readonly SemanticMediaType MarkdownMediaType = SemanticMediaType
        .Create("text", "markdown")
        .WithKind("markdown.doc");

    private readonly MarkdownPipeline _pipeline = new MarkdownPipelineBuilder()
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

    private readonly LiquidTemplateRenderer _renderer = new(
        assembly: typeof(MarkdownLoader).Assembly,
        resourceRoot: "RepoQL.Formats.Markdown.Templates");

    private static readonly Lazy<string> MarkdownViewsSql = new(
        () => ReadEmbeddedResource("RepoQL.Formats.Markdown.Schema.markdown_views.sql"));

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

        string text;
        await using (var fs = artifact.File.CreateReadStream())
        using (var sr = new StreamReader(fs))
        {
            text = await sr.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }

        var digest = "xxh64:" + Convert.ToHexString(artifact.Hash ?? throw new InvalidOperationException("Artifact hash missing")).ToLowerInvariant();
        var docId = Guid.NewGuid();
        var markdigDoc = Markdig.Markdown.Parse(text, _pipeline);

        var lineMap = new TextLineMap(text);
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
                        headings.Add(new HeadingInfo(nodeId, spanId, heading.Level, headingText, slug, span));
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

        foreach (var link in markdigDoc.Descendants<LinkInline>())
        {
            var span = ToDocumentSpan(lineMap, link.Span);
            var spanId = Guid.NewGuid();
            var nodeId = Guid.NewGuid();
            links.Add(new LinkInfo(nodeId, spanId, link.Url ?? string.Empty, link.Title ?? string.Empty, InlineText(link), span));
        }
        var imagesCount = markdigDoc.Descendants<LinkInline>().Count(l => l.IsImage);
        var tablesCount = markdigDoc.Descendants<Table>().Count();

        var surface = new MarkdownSurface
        {
            DocumentId = docId,
            DocumentProperties = documentProps,
            Headings = headings,
            Links = links,
            CodeBlocks = blocks
        };

        var state = new MarkdownDocumentState
        {
            Surface = surface,
            Digest = digest,
            Size = artifact.File.Length,
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

            var frontmatterKeys = 0;
            string? title = null;
            var topics = new List<string>();
            string? topLang = null;
            var tags = new List<string>();
            try
            {
                if (state.Surface.DocumentProperties["frontmatter"] is JsonObject fm)
                {
                    frontmatterKeys = fm.Count;
                    if (fm.TryGetPropertyValue("title", out var t) && t is not null)
                    {
                        title = t.ToString();
                    }
                    // Extract tags or keywords from frontmatter (array or comma-separated string)
                    if (fm.TryGetPropertyValue("tags", out var tv) && tv is not null)
                    {
                        tags.AddRange(ExtractTags(tv));
                    }
                    else if (fm.TryGetPropertyValue("keywords", out var kv) && kv is not null)
                    {
                        tags.AddRange(ExtractTags(kv));
                    }
                }
            }
            catch { }

            // Prefer frontmatter title, else first H1 heading
            title ??= state.Surface.Headings.FirstOrDefault(h => h.Level == 1)?.Text;

            // Topics: first few distinct H2/H3 headings
            topics = state.Surface.Headings
                .Where(h => h.Level >= 2)
                .Select(h => h.Text?.Trim())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(3)
                .OfType<string>()
                .ToList();

            // Top language: most frequent fenced code block language
            if (langCounts.Count > 0)
            {
                topLang = langCounts
                    .OrderByDescending(kv => kv.Value)
                    .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                    .First().Key;
            }

            var model = new Dictionary<string, object?>
            {
                ["file_name"] = fileName,
                ["media_kind"] = state.MediaType.Kind ?? string.Empty,
                ["media_base"] = $"{state.MediaType.Type}/{state.MediaType.Subtype}",
                ["size_bytes"] = state.Size,
                ["line_count"] = document.LineMap.LineCount,
                ["headings_count"] = state.Surface.Headings.Count,
                ["codeblocks_count"] = state.Surface.CodeBlocks.Count,
                ["links_count"] = state.Surface.Links.Count,
                ["images_count"] = imagesCount,
                ["tables_count"] = tablesCount,
                ["frontmatter_keys"] = frontmatterKeys,
                ["code_lang_counts"] = langCounts,
                ["title"] = title,
                ["topics"] = topics,
                ["top_lang"] = topLang,
                ["tags"] = tags.Distinct(StringComparer.OrdinalIgnoreCase).Take(2).ToList(),
                ["headings"] = state.Surface.Headings.Select(h => new Dictionary<string, object?>
                {
                    ["level"] = h.Level,
                    ["text"] = h.Text,
                    ["indent"] = new string(' ', Math.Max(0, (h.Level - 1) * 2))
                }).ToList()
            };

            headline = _renderer.RenderAsync("xray/headline", model).GetAwaiter().GetResult();
            summary = _renderer.RenderAsync("xray/summary", model).GetAwaiter().GetResult();
            structure = _renderer.RenderAsync("xray/structure", model).GetAwaiter().GetResult();
        }
        catch
        {
            // ignore templating errors; x-ray is best-effort
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

        foreach (var heading in state.Surface.Headings)
        {
            var span = ToSpan(document, heading.Span, docNode.Id, heading.SpanId);
            spans.Add(span);

            var node = new Node
            {
                Id = heading.NodeId,
                Kind = "md_heading",
                SpanId = heading.SpanId,
                Props = new JsonObject
                {
                    ["level"] = heading.Level,
                    ["text"] = heading.Text,
                    ["slug"] = heading.Slug
                },
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

    private static bool TryLoadFrontMatter(string text, YamlFrontMatterBlock block, JsonObject props)
    {
        try
        {
            var yaml = ExtractYamlText(text, block);
            if (string.IsNullOrWhiteSpace(yaml)) return false;
            var json = YamlToJson(yaml);
            if (json is not null) 
                props["frontmatter"] = json;
            return true;
        }
        catch
        {
            return false;
        }
    }

    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("AOT", "IL3050", Justification = "YAML frontmatter deserialization uses reflection, not AOT-compatible by design")]
    private static JsonNode? YamlToJson(string yaml)
    {
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
        var result = deserializer.Deserialize<object?>(yaml);
        return ToJsonNode(result);
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
                return JsonValue.Create(obj.ToString());
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

    [LoggerMessage(LogLevel.Warning, "Failed to parse {Name} as markdown")]
    partial void LogFailedToParseNameAsMarkdown(Exception ex, string name);
    
    [LoggerMessage(LogLevel.Warning, "Failed to load front matter from {Name}")]
    partial void LogFailedToLoadFrontmatter(Exception ex, string name);
}
