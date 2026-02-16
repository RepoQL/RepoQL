using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RepoQL.Contracts;
using RepoQL.Contracts.Models;
using RepoQL.Templating;
using RepoQL.Templating.Filters;

namespace RepoQL.Formats.Json;

/// <summary>
/// Loads and materializes generic JSON files into graph records.
///
/// Purpose: Provides the default JSON tier with x-ray templates, key nodes, spans, and SQL macros.
///
/// Complexity: Handles load/parsing options, metadata persistence, template modeling, and graph materialization.
/// </summary>
public sealed class JsonLoader(JsonStructureParser parser) : IFormatLoader, IFormatMaterializer, IFormatSchemaProvider
{
    internal const string StateMetadataKey = "json.state";

    private const string DigestMetadataKey = "json.digest";
    private const string ByteSizeMetadataKey = "json.byte_size";

    private readonly JsonStructureParser _parser = parser ?? throw new ArgumentNullException(nameof(parser));

    private readonly LiquidTemplateRenderer _renderer = new(
        assembly: typeof(JsonLoader).Assembly,
        resourceRoot: "RepoQL.Formats.Json.Templates",
        configure: StandardFilters.RegisterAll);

    public bool Supports(SemanticMediaType mediaType)
    {
        ArgumentNullException.ThrowIfNull(mediaType);
        return mediaType.Kind?.StartsWith("json", StringComparison.OrdinalIgnoreCase) == true;
    }

    public Task<bool> CanLoadAsync(DiscoveredArtifact artifact, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);

        var fileName = artifact.File.Name;
        if (fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".json5", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".jsonc", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".ndjson", StringComparison.OrdinalIgnoreCase))
        {
            artifact.MediaType = JsonMediaTypes.Json;
            return Task.FromResult(true);
        }

        return Task.FromResult(false);
    }

    public async Task<DocumentModel> LoadAsync(DiscoveredArtifact artifact, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        if (artifact.RepoUri is null)
            throw new InvalidOperationException("RepoUri required for JSON loader.");

        var loaded = await FileContentReader.ReadAllTextWithDigestAsync(
            artifact.File,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var isJsonLines = IsJsonLinesExtension(artifact.File.Name);
        var isJsonc = artifact.File.Name.EndsWith(".jsonc", StringComparison.OrdinalIgnoreCase);
        var isJson5 = artifact.File.Name.EndsWith(".json5", StringComparison.OrdinalIgnoreCase);
        var options = isJsonLines
            ? new JsonParseOptions { IsJsonl = true }
            : null;

        JsonParseResult parseResult;
        if (isJsonLines)
        {
            parseResult = _parser.Parse(loaded.Text, options);
        }
        else if (isJson5)
        {
            var normalized = Json5Normalizer.Normalize(loaded.Text);
            parseResult = _parser.Parse(normalized, options);
        }
        else if (isJsonc)
        {
            var normalizedBytes = Encoding.UTF8.GetBytes(loaded.Text);
            JsonNormalizer.StripComments(normalizedBytes);
            parseResult = _parser.Parse(normalizedBytes, options);
        }
        else
        {
            parseResult = ParseJsonWithCommentFallback(loaded.Text, options);
        }

        var metadata = new Dictionary<string, object?>
        {
            [StateMetadataKey] = parseResult,
            [DigestMetadataKey] = loaded.Digest,
            [ByteSizeMetadataKey] = loaded.ByteLength
        };

        return new DocumentModel(
            artifact.RepoUri,
            artifact.MediaType ?? JsonMediaTypes.Json,
            loaded.Text,
            metadata: metadata);
    }

    public Records Materialize(DocumentModel document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var parseResult = document.GetMetadataOrDefault<JsonParseResult>(StateMetadataKey)
            ?? throw new InvalidOperationException("JSON document missing parse metadata.");

        var digest = document.GetMetadataOrDefault<string>(DigestMetadataKey);
        if (string.IsNullOrWhiteSpace(digest))
            digest = ContentDigest.FromBytes(Encoding.UTF8.GetBytes(document.Text));

        var size = document.GetMetadataOrDefault<long>(ByteSizeMetadataKey);
        if (size <= 0 && document.Text.Length > 0)
            size = Encoding.UTF8.GetByteCount(document.Text);

        var tokenCount = EstimateTokenCount(document.Text);
        var shape = GetShapeLabel(parseResult.Shape);

        var keys = parseResult.Keys
            .Select(key => new Dictionary<string, object?>
            {
                ["indent"] = new string(' ', key.Depth * 2),
                ["name"] = key.Name,
                ["type_label"] = GetTypeLabel(key),
                ["estimated_tokens"] = key.EstimatedTokens,
                ["scalar_value"] = key.ScalarValue,
                ["path"] = key.Path
            })
            .ToList();

        var model = new Dictionary<string, object?>
        {
            ["file_name"] = GetFileName(document.Uri),
            ["size_bytes"] = size,
            ["token_count"] = tokenCount,
            ["shape"] = shape,
            ["top_keys"] = parseResult.Keys.Where(k => k.Depth == 0).Select(k => k.Name).ToList(),
            ["key_count"] = parseResult.TotalKeyCount,
            ["max_depth"] = parseResult.MaxDepth,
            ["keys"] = keys
        };

        var headline = _renderer.RenderAsync("explore/headline", model).GetAwaiter().GetResult();
        var summary = _renderer.RenderAsync("explore/summary", model).GetAwaiter().GetResult();
        var structure = _renderer.RenderAsync("explore/structure", model).GetAwaiter().GetResult();

        var artifact = new Artifact
        {
            Id = Guid.NewGuid(),
            Digest = digest,
            Size = size,
            MediaType = document.MediaType,
            Text = document.Text,
            StoreUri = document.Uri,
            Headline = headline,
            Summary = summary,
            Structure = structure,
            TokenCount = tokenCount
        };

        var now = DateTimeOffset.UtcNow;
        var documentNode = new Node
        {
            Id = Guid.NewGuid(),
            Kind = "document",
            Uri = document.Uri,
            ArtifactId = artifact.Id,
            Props = new JsonObject
            {
                ["media_type"] = document.MediaType?.ToString(),
                ["shape"] = shape,
                ["key_count"] = parseResult.TotalKeyCount,
                ["max_depth"] = parseResult.MaxDepth
            },
            CreatedAt = now,
            UpdatedAt = now
        };

        var nodes = new List<Node> { documentNode };
        var spans = new List<Span>();
        var edges = new List<Edge>();
        var ordinal = 0;

        foreach (var key in parseResult.Keys.Where(k => k.IsNodeEligible))
        {
            var span = new Span
            {
                Id = Guid.NewGuid(),
                DocumentId = documentNode.Id,
                StartLine = key.StartLine,
                EndLine = key.EndLine
            };
            spans.Add(span);

            var keyNode = new Node
            {
                Id = Guid.NewGuid(),
                Kind = "json_key",
                SpanId = span.Id,
                Uri = RepoUri.FromSymbol(document.Uri.Container, key.Path, key.StartLine, key.EndLine),
                Props = new JsonObject
                {
                    ["path"] = key.Path,
                    ["name"] = key.Name,
                    ["depth"] = key.Depth,
                    ["value_kind"] = ValueKindLabel(key.ValueKind),
                    ["scalar_value"] = key.ScalarValue,
                    ["estimated_tokens"] = key.EstimatedTokens
                },
                CreatedAt = now,
                UpdatedAt = now
            };

            nodes.Add(keyNode);
            edges.Add(CreateHasPart(documentNode.Id, keyNode.Id, documentNode.Id, ordinal++, now));
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
        yield return new FormatSqlScript("json_macros", JsonMacrosSql.Value);
    }

    private static bool IsJsonLinesExtension(string fileName)
        => fileName.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)
           || fileName.EndsWith(".ndjson", StringComparison.OrdinalIgnoreCase);

    private JsonParseResult ParseJsonWithCommentFallback(string text, JsonParseOptions? options)
    {
        try
        {
            return _parser.Parse(text, options);
        }
        catch (JsonException originalException)
        {
            try
            {
                var normalizedBytes = JsonNormalizer.StripComments(text);
                return _parser.Parse(normalizedBytes, options);
            }
            catch (JsonException)
            {
                ExceptionDispatchInfo.Capture(originalException).Throw();
                throw;
            }
        }
    }

    private static int EstimateTokenCount(string text)
        => string.IsNullOrEmpty(text) ? 0 : text.Length / 4;

    private static string GetShapeLabel(JsonShape shape)
    {
        return shape switch
        {
            JsonShape.FlatObject => "object",
            JsonShape.NestedObject => "object",
            JsonShape.Array => "array",
            JsonShape.SingleValue => "value",
            _ => "empty"
        };
    }

    private static string GetTypeLabel(JsonKeyInfo key)
    {
        var label = ValueKindLabel(key.ValueKind);
        if (key.ValueKind == JsonValueKind.Array && key.ArrayLength is int arrayLength)
            return $"{label}[{arrayLength}]";

        return label;
    }

    private static string ValueKindLabel(JsonValueKind valueKind)
    {
        return valueKind switch
        {
            JsonValueKind.Object => "object",
            JsonValueKind.Array => "array",
            JsonValueKind.String => "string",
            JsonValueKind.Number => "number",
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => "null",
            _ => "undefined"
        };
    }

    private static string GetFileName(RepoUri uri)
    {
        try
        {
            if (uri.IsFile)
            {
                var localPath = uri.LocalPath;
                if (!string.IsNullOrEmpty(localPath))
                    return Path.GetFileName(localPath);
            }
        }
        catch
        {
            // Fall through to URI parsing.
        }

        var absolutePath = Uri.UnescapeDataString(uri.AbsolutePath);
        var slash = absolutePath.LastIndexOf('/') >= 0
            ? absolutePath[(absolutePath.LastIndexOf('/') + 1)..]
            : absolutePath;
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

    private static readonly Lazy<string> JsonMacrosSql = new(() =>
        ReadEmbeddedResource("RepoQL.Formats.Json.Schema.json_macros.sql"));

    private static string ReadEmbeddedResource(string resourceName)
    {
        using var stream = typeof(JsonLoader).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{resourceName}' not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

