using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RepoQL.Contracts;
using RepoQL.Contracts.Models;

namespace RepoQL.Formats.CSS;

public sealed class CSSLoader : IFormatLoader, IFormatMaterializer
{
    private ILogger<CSSLoader> Logger { get; }
    private readonly CSSAntlrClient _cssClient;
    private readonly SCSSAntlrClient _scssClient;

    private const string StateMetadataKey = "css.state";

    public CSSLoader(ILogger<CSSLoader>? logger = null)
    {
        Logger = logger ?? NullLogger<CSSLoader>.Instance;
        _cssClient = new CSSAntlrClient();
        _scssClient = new SCSSAntlrClient();
    }

    public bool Supports(SemanticMediaType mediaType)
    {
        ArgumentNullException.ThrowIfNull(mediaType);

        return string.Equals(mediaType.Kind, CSSMediaTypes.CSS.Kind, StringComparison.OrdinalIgnoreCase)
               || string.Equals(mediaType.Kind, CSSMediaTypes.SCSS.Kind, StringComparison.OrdinalIgnoreCase)
               || string.Equals(mediaType.Kind, CSSMediaTypes.LESS.Kind, StringComparison.OrdinalIgnoreCase);
    }

    public async Task<bool> CanLoadAsync(DiscoveredArtifact artifact, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);

        var name = artifact.File.Name.ToLowerInvariant();
        var extension = Path.GetExtension(name);

        if (CSSMediaTypes.TryResolve(extension, out var mediaType))
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
            throw new InvalidOperationException("RepoUri required to load CSS files.");

        var loaded = await FileContentReader.ReadAllTextWithDigestAsync(
            artifact.File,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var text = loaded.Text;
        var digest = loaded.Digest;

        // Use appropriate parser based on media type
        var parseResult = CSSMediaTypes.IsSCSS(artifact.MediaType)
            ? _scssClient.Parse(text)
            : _cssClient.Parse(text);

        var state = new CSSDocumentState
        {
            DocumentId = Guid.NewGuid(),
            ParseResult = parseResult,
            Digest = digest,
            Size = loaded.ByteLength,
            MediaType = artifact.MediaType ?? CSSMediaTypes.CSS,
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
        var state = document.GetMetadataOrDefault<CSSDocumentState>(StateMetadataKey)
            ?? throw new InvalidOperationException("CSS document missing state metadata.");

        var parseResult = state.ParseResult;

        // Calculate token count
        var tokenCount = TokenEstimator.EstimateTokensSafe(document.Text);

        // Generate X-ray summaries
        var headline = BuildHeadline(document, parseResult, state.MediaType, tokenCount);
        var structure = BuildStructure(parseResult, state.MediaType);

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

        var language = state.MediaType.Kind switch
        {
            "code.scss" => "scss",
            "code.less" => "less",
            _ => "css"
        };

        var docNode = new Node
        {
            Id = state.DocumentId,
            Kind = CSSNodeKinds.Document,
            Uri = document.Uri,
            ArtifactId = artifact.Id,
            Props = new JsonObject
            {
                [CSSPropertyKeys.Language] = language,
                [CSSPropertyKeys.ByteSize] = artifact.Size,
                [CSSPropertyKeys.LineCount] = document.LineMap.LineCount,
                ["rulesets"] = parseResult.Rulesets.Count,
                ["imports"] = parseResult.Imports.Count,
                ["media_rules"] = parseResult.MediaRules.Count,
                ["keyframes"] = parseResult.Keyframes.Count,
                ["variables"] = parseResult.Variables.Count,
                ["mixins"] = parseResult.Mixins.Count
            },
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        var nodes = new List<Node> { docNode };
        var spans = new List<Span>();
        var edges = new List<Edge>();
        var now = DateTimeOffset.UtcNow;
        var ordinal = 0;

        // Materialize imports first
        foreach (var import in parseResult.Imports)
        {
            var span = CreateSpan(import.Span, state.DocumentId, document);
            spans.Add(span);
            var node = CreateImportNode(import, artifact.Id, document.Uri.Container, span, now);
            nodes.Add(node);
            edges.Add(CreateComposition(docNode.Id, node.Id, ordinal++, state.DocumentId, now));
        }

        // Materialize charsets
        foreach (var charset in parseResult.Charsets)
        {
            var span = CreateSpan(charset.Span, state.DocumentId, document);
            spans.Add(span);
            var node = CreateCharsetNode(charset, artifact.Id, document.Uri.Container, span, now);
            nodes.Add(node);
            edges.Add(CreateComposition(docNode.Id, node.Id, ordinal++, state.DocumentId, now));
        }

        // Materialize namespaces
        foreach (var ns in parseResult.Namespaces)
        {
            var span = CreateSpan(ns.Span, state.DocumentId, document);
            spans.Add(span);
            var node = CreateNamespaceNode(ns, artifact.Id, document.Uri.Container, span, now);
            nodes.Add(node);
            edges.Add(CreateComposition(docNode.Id, node.Id, ordinal++, state.DocumentId, now));
        }

        // Materialize SCSS variables
        foreach (var variable in parseResult.Variables)
        {
            var span = CreateSpan(variable.Span, state.DocumentId, document);
            spans.Add(span);
            var node = CreateVariableNode(variable, artifact.Id, document.Uri.Container, span, now);
            nodes.Add(node);
            edges.Add(CreateComposition(docNode.Id, node.Id, ordinal++, state.DocumentId, now));
        }

        // Materialize SCSS mixins
        foreach (var mixin in parseResult.Mixins)
        {
            var span = CreateSpan(mixin.Span, state.DocumentId, document);
            spans.Add(span);
            var node = CreateMixinNode(mixin, artifact.Id, document.Uri.Container, span, now);
            nodes.Add(node);
            edges.Add(CreateComposition(docNode.Id, node.Id, ordinal++, state.DocumentId, now));
        }

        // Materialize SCSS functions
        foreach (var func in parseResult.Functions)
        {
            var span = CreateSpan(func.Span, state.DocumentId, document);
            spans.Add(span);
            var node = CreateFunctionNode(func, artifact.Id, document.Uri.Container, span, now);
            nodes.Add(node);
            edges.Add(CreateComposition(docNode.Id, node.Id, ordinal++, state.DocumentId, now));
        }

        // Materialize media rules
        foreach (var media in parseResult.MediaRules)
        {
            var span = CreateSpan(media.Span, state.DocumentId, document);
            spans.Add(span);
            var node = CreateMediaNode(media, artifact.Id, document.Uri.Container, span, now);
            nodes.Add(node);
            edges.Add(CreateComposition(docNode.Id, node.Id, ordinal++, state.DocumentId, now));
        }

        // Materialize keyframes
        foreach (var keyframes in parseResult.Keyframes)
        {
            var span = CreateSpan(keyframes.Span, state.DocumentId, document);
            spans.Add(span);
            var node = CreateKeyframesNode(keyframes, artifact.Id, document.Uri.Container, span, now);
            nodes.Add(node);
            edges.Add(CreateComposition(docNode.Id, node.Id, ordinal++, state.DocumentId, now));
        }

        // Materialize font-face rules
        foreach (var fontFace in parseResult.FontFaces)
        {
            var span = CreateSpan(fontFace.Span, state.DocumentId, document);
            spans.Add(span);
            var node = CreateFontFaceNode(artifact.Id, document.Uri.Container, span, now);
            nodes.Add(node);
            edges.Add(CreateComposition(docNode.Id, node.Id, ordinal++, state.DocumentId, now));
        }

        // Materialize @supports rules
        foreach (var supports in parseResult.SupportsRules)
        {
            var span = CreateSpan(supports.Span, state.DocumentId, document);
            spans.Add(span);
            var node = CreateSupportsNode(supports, artifact.Id, document.Uri.Container, span, now);
            nodes.Add(node);
            edges.Add(CreateComposition(docNode.Id, node.Id, ordinal++, state.DocumentId, now));
        }

        // Materialize page rules
        foreach (var page in parseResult.Pages)
        {
            var span = CreateSpan(page.Span, state.DocumentId, document);
            spans.Add(span);
            var node = CreatePageNode(page, artifact.Id, document.Uri.Container, span, now);
            nodes.Add(node);
            edges.Add(CreateComposition(docNode.Id, node.Id, ordinal++, state.DocumentId, now));
        }

        // Materialize rulesets
        foreach (var ruleset in parseResult.Rulesets)
        {
            var span = CreateSpan(ruleset.Span, state.DocumentId, document);
            spans.Add(span);
            var node = CreateRulesetNode(ruleset, artifact.Id, document.Uri.Container, span, now);
            nodes.Add(node);
            edges.Add(CreateComposition(docNode.Id, node.Id, ordinal++, state.DocumentId, now));
        }

        // Materialize SCSS includes
        foreach (var include in parseResult.Includes)
        {
            var span = CreateSpan(include.Span, state.DocumentId, document);
            spans.Add(span);
            var node = CreateIncludeNode(include, artifact.Id, document.Uri.Container, span, now);
            nodes.Add(node);
            edges.Add(CreateComposition(docNode.Id, node.Id, ordinal++, state.DocumentId, now));
        }

        // Materialize SCSS extends
        foreach (var extend in parseResult.Extends)
        {
            var span = CreateSpan(extend.Span, state.DocumentId, document);
            spans.Add(span);
            var node = CreateExtendNode(extend, artifact.Id, document.Uri.Container, span, now);
            nodes.Add(node);
            edges.Add(CreateComposition(docNode.Id, node.Id, ordinal++, state.DocumentId, now));
        }

        return new Records
        {
            Artifacts = [artifact],
            Nodes = nodes.ToArray(),
            Spans = spans.ToArray(),
            Edges = edges.ToArray()
        };
    }

    private static string BuildHeadline(DocumentModel document, CSSParseResult parseResult, SemanticMediaType mediaType, int? tokenCount)
    {
        var fileName = GetFileName(document.Uri);
        var parts = new List<string> { fileName };

        var language = mediaType.Kind switch
        {
            "code.scss" => "SCSS",
            "code.less" => "LESS",
            _ => "CSS"
        };

        var counts = new List<string>();
        if (parseResult.Rulesets.Count > 0) counts.Add($"{parseResult.Rulesets.Count} rules");
        if (parseResult.Variables.Count > 0) counts.Add($"{parseResult.Variables.Count} vars");
        if (parseResult.Mixins.Count > 0) counts.Add($"{parseResult.Mixins.Count} mixins");
        if (parseResult.MediaRules.Count > 0) counts.Add($"{parseResult.MediaRules.Count} media");
        if (parseResult.Keyframes.Count > 0) counts.Add($"{parseResult.Keyframes.Count} keyframes");

        if (counts.Count > 0)
        {
            parts.Add(string.Join(", ", counts.Take(3)));
        }
        else
        {
            parts.Add(language);
        }

        if (tokenCount.HasValue)
        {
            parts.Add($"{tokenCount.Value} tokens");
        }

        return string.Join(" | ", parts);
    }

    private static string BuildStructure(CSSParseResult parseResult, SemanticMediaType mediaType)
    {
        var sb = new StringBuilder();

        if (parseResult.Imports.Count > 0)
        {
            sb.AppendLine("Imports:");
            foreach (var import in parseResult.Imports)
            {
                sb.AppendLine($"  @import \"{import.Path}\"");
            }
        }

        if (parseResult.Variables.Count > 0)
        {
            sb.AppendLine("Variables:");
            foreach (var v in parseResult.Variables)
            {
                sb.AppendLine($"  {v.Name}");
            }
        }

        if (parseResult.Mixins.Count > 0)
        {
            sb.AppendLine("Mixins:");
            foreach (var m in parseResult.Mixins)
            {
                sb.AppendLine($"  @mixin {m.Name}");
            }
        }

        if (parseResult.Functions.Count > 0)
        {
            sb.AppendLine("Functions:");
            foreach (var f in parseResult.Functions)
            {
                sb.AppendLine($"  @function {f.Name}");
            }
        }

        if (parseResult.MediaRules.Count > 0)
        {
            sb.AppendLine("Media Queries:");
            foreach (var m in parseResult.MediaRules)
            {
                var condition = m.Condition.Length > 50 ? m.Condition[..50] + "..." : m.Condition;
                sb.AppendLine($"  @media {condition}");
            }
        }

        if (parseResult.Keyframes.Count > 0)
        {
            sb.AppendLine("Keyframes:");
            foreach (var k in parseResult.Keyframes)
            {
                sb.AppendLine($"  @keyframes {k.Name}");
            }
        }

        if (parseResult.FontFaces.Count > 0)
        {
            sb.AppendLine($"Font Faces: {parseResult.FontFaces.Count}");
        }

        if (parseResult.Rulesets.Count > 0)
        {
            sb.AppendLine("Selectors:");
            foreach (var r in parseResult.Rulesets)
            {
                var selector = r.Selector.Length > 60 ? r.Selector[..60] + "..." : r.Selector;
                sb.AppendLine($"  {selector}");
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static Node CreateRulesetNode(CSSRulesetInfo ruleset, Guid artifactId, Uri container, Span span, DateTimeOffset now)
    {
        var symbolName = SanitizeSymbolName(ruleset.Selector);
        return new Node
        {
            Id = Guid.NewGuid(),
            Kind = CSSNodeKinds.Ruleset,
            ArtifactId = artifactId,
            SpanId = span.Id,
            Uri = CreateUriWithCharRange(container, symbolName, span),
            Props = new JsonObject
            {
                [CSSPropertyKeys.Selector] = ruleset.Selector
            },
            Headline = ruleset.Selector,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static RepoUri CreateUriWithCharRange(Uri container, string symbolName, Span span)
    {
        // Use char range to ensure uniqueness for minified CSS where multiple rules are on the same line
        return RepoUri.Create(container,
            RepoUri.Location.FromSymbol(symbolName)
                .WithLineRange(span.StartLine, span.EndLine)
                .WithCharRange(span.StartByte, span.EndByte));
    }

    private static Node CreateImportNode(CSSImportInfo import, Guid artifactId, Uri container, Span span, DateTimeOffset now)
    {
        var symbolName = $"import.{SanitizeSymbolName(import.Path)}";
        return new Node
        {
            Id = Guid.NewGuid(),
            Kind = CSSNodeKinds.Import,
            ArtifactId = artifactId,
            SpanId = span.Id,
            Uri = CreateUriWithCharRange(container, symbolName, span),
            Props = new JsonObject
            {
                [CSSPropertyKeys.Path] = import.Path
            },
            Headline = $"@import \"{import.Path}\"",
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static Node CreateMediaNode(CSSMediaInfo media, Guid artifactId, Uri container, Span span, DateTimeOffset now)
    {
        var symbolName = $"media.{SanitizeSymbolName(media.Condition)}";
        return new Node
        {
            Id = Guid.NewGuid(),
            Kind = CSSNodeKinds.Media,
            ArtifactId = artifactId,
            SpanId = span.Id,
            Uri = CreateUriWithCharRange(container, symbolName, span),
            Props = new JsonObject
            {
                [CSSPropertyKeys.Condition] = media.Condition
            },
            Headline = $"@media {media.Condition}",
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static Node CreateKeyframesNode(CSSKeyframesInfo keyframes, Guid artifactId, Uri container, Span span, DateTimeOffset now)
    {
        var symbolName = $"keyframes.{keyframes.Name}";
        return new Node
        {
            Id = Guid.NewGuid(),
            Kind = CSSNodeKinds.Keyframes,
            ArtifactId = artifactId,
            SpanId = span.Id,
            Uri = CreateUriWithCharRange(container, symbolName, span),
            Props = new JsonObject
            {
                [CSSPropertyKeys.Name] = keyframes.Name
            },
            Headline = $"@keyframes {keyframes.Name}",
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static Node CreateFontFaceNode(Guid artifactId, Uri container, Span span, DateTimeOffset now)
    {
        var symbolName = "fontface";
        return new Node
        {
            Id = Guid.NewGuid(),
            Kind = CSSNodeKinds.FontFace,
            ArtifactId = artifactId,
            SpanId = span.Id,
            Uri = CreateUriWithCharRange(container, symbolName, span),
            Props = new JsonObject(),
            Headline = "@font-face",
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static Node CreateSupportsNode(CSSSupportsInfo supports, Guid artifactId, Uri container, Span span, DateTimeOffset now)
    {
        var symbolName = $"supports.{SanitizeSymbolName(supports.Condition)}";
        return new Node
        {
            Id = Guid.NewGuid(),
            Kind = CSSNodeKinds.Supports,
            ArtifactId = artifactId,
            SpanId = span.Id,
            Uri = CreateUriWithCharRange(container, symbolName, span),
            Props = new JsonObject
            {
                [CSSPropertyKeys.Condition] = supports.Condition
            },
            Headline = $"@supports {supports.Condition}",
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static Node CreateCharsetNode(CSSCharsetInfo charset, Guid artifactId, Uri container, Span span, DateTimeOffset now)
    {
        var symbolName = $"charset.{charset.Charset}";
        return new Node
        {
            Id = Guid.NewGuid(),
            Kind = CSSNodeKinds.Charset,
            ArtifactId = artifactId,
            SpanId = span.Id,
            Uri = CreateUriWithCharRange(container, symbolName, span),
            Props = new JsonObject
            {
                [CSSPropertyKeys.Value] = charset.Charset
            },
            Headline = $"@charset \"{charset.Charset}\"",
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static Node CreateNamespaceNode(CSSNamespaceInfo ns, Guid artifactId, Uri container, Span span, DateTimeOffset now)
    {
        var symbolName = string.IsNullOrEmpty(ns.Prefix) ? "namespace" : $"namespace.{ns.Prefix}";
        var headline = string.IsNullOrEmpty(ns.Prefix)
            ? $"@namespace \"{ns.Uri}\""
            : $"@namespace {ns.Prefix} \"{ns.Uri}\"";

        return new Node
        {
            Id = Guid.NewGuid(),
            Kind = CSSNodeKinds.Namespace,
            ArtifactId = artifactId,
            SpanId = span.Id,
            Uri = CreateUriWithCharRange(container, symbolName, span),
            Props = new JsonObject
            {
                [CSSPropertyKeys.Name] = ns.Prefix ?? "",
                [CSSPropertyKeys.Value] = ns.Uri
            },
            Headline = headline,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static Node CreatePageNode(CSSPageInfo page, Guid artifactId, Uri container, Span span, DateTimeOffset now)
    {
        var symbolName = string.IsNullOrEmpty(page.PseudoPage) ? "page" : $"page.{page.PseudoPage}";
        var headline = string.IsNullOrEmpty(page.PseudoPage) ? "@page" : $"@page {page.PseudoPage}";

        return new Node
        {
            Id = Guid.NewGuid(),
            Kind = CSSNodeKinds.Page,
            ArtifactId = artifactId,
            SpanId = span.Id,
            Uri = CreateUriWithCharRange(container, symbolName, span),
            Props = new JsonObject(),
            Headline = headline,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static Node CreateVariableNode(SCSSVariableInfo variable, Guid artifactId, Uri container, Span span, DateTimeOffset now)
    {
        var symbolName = $"var.{variable.Name.TrimStart('$')}";
        return new Node
        {
            Id = Guid.NewGuid(),
            Kind = CSSNodeKinds.Variable,
            ArtifactId = artifactId,
            SpanId = span.Id,
            Uri = CreateUriWithCharRange(container, symbolName, span),
            Props = new JsonObject
            {
                [CSSPropertyKeys.Name] = variable.Name,
                [CSSPropertyKeys.Value] = variable.Value ?? ""
            },
            Headline = variable.Name,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static Node CreateMixinNode(SCSSMixinInfo mixin, Guid artifactId, Uri container, Span span, DateTimeOffset now)
    {
        var symbolName = $"mixin.{mixin.Name}";
        return new Node
        {
            Id = Guid.NewGuid(),
            Kind = CSSNodeKinds.Mixin,
            ArtifactId = artifactId,
            SpanId = span.Id,
            Uri = CreateUriWithCharRange(container, symbolName, span),
            Props = new JsonObject
            {
                [CSSPropertyKeys.Name] = mixin.Name
            },
            Headline = $"@mixin {mixin.Name}",
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static Node CreateIncludeNode(SCSSIncludeInfo include, Guid artifactId, Uri container, Span span, DateTimeOffset now)
    {
        var symbolName = $"include.{include.Name}";
        return new Node
        {
            Id = Guid.NewGuid(),
            Kind = CSSNodeKinds.Include,
            ArtifactId = artifactId,
            SpanId = span.Id,
            Uri = CreateUriWithCharRange(container, symbolName, span),
            Props = new JsonObject
            {
                [CSSPropertyKeys.Name] = include.Name
            },
            Headline = $"@include {include.Name}",
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static Node CreateExtendNode(SCSSExtendInfo extend, Guid artifactId, Uri container, Span span, DateTimeOffset now)
    {
        var symbolName = $"extend.{SanitizeSymbolName(extend.Extended)}";
        return new Node
        {
            Id = Guid.NewGuid(),
            Kind = CSSNodeKinds.Extend,
            ArtifactId = artifactId,
            SpanId = span.Id,
            Uri = CreateUriWithCharRange(container, symbolName, span),
            Props = new JsonObject
            {
                [CSSPropertyKeys.Name] = extend.Extended
            },
            Headline = $"@extend {extend.Extended}",
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static Node CreateFunctionNode(SCSSFunctionInfo func, Guid artifactId, Uri container, Span span, DateTimeOffset now)
    {
        var symbolName = $"function.{func.Name}";
        return new Node
        {
            Id = Guid.NewGuid(),
            Kind = CSSNodeKinds.Function,
            ArtifactId = artifactId,
            SpanId = span.Id,
            Uri = CreateUriWithCharRange(container, symbolName, span),
            Props = new JsonObject
            {
                [CSSPropertyKeys.Name] = func.Name
            },
            Headline = $"@function {func.Name}",
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static Span CreateSpan(CSSSpan cssSpan, Guid docId, DocumentModel document)
    {
        var start = Math.Clamp(cssSpan.Start, 0, document.Text.Length);
        var end = Math.Clamp(cssSpan.End, start, document.Text.Length);
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
            Type = "HAS_PART",
            IsComposition = true,
            Ordinal = ordinal,
            ScopeDocumentId = scopeDocId,
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

    private static string SanitizeSymbolName(string name)
    {
        // Replace characters that aren't valid in URIs
        return name
            .Replace(" ", "_")
            .Replace(",", "_")
            .Replace(">", "_gt_")
            .Replace("<", "_lt_")
            .Replace(":", "_")
            .Replace("(", "_")
            .Replace(")", "_")
            .Replace("[", "_")
            .Replace("]", "_")
            .Replace("{", "_")
            .Replace("}", "_")
            .Replace("#", "_hash_")
            .Replace(".", "_dot_")
            .Replace("@", "_at_")
            .Replace("&", "_amp_")
            .Trim('_');
    }
}
