using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RepoQL.Contracts;
using RepoQL.Contracts.Models;

namespace RepoQL.Formats.Terraform;

public sealed partial class TerraformLoader : IFormatLoader, IFormatMaterializer
{
    private ILogger<TerraformLoader> Logger { get; }
    private readonly TerraformAntlrClient _client;

    private const string StateMetadataKey = "terraform.state";

    public TerraformLoader(ILogger<TerraformLoader>? logger = null)
    {
        Logger = logger ?? NullLogger<TerraformLoader>.Instance;
        _client = new TerraformAntlrClient();
    }

    public bool Supports(SemanticMediaType mediaType)
    {
        ArgumentNullException.ThrowIfNull(mediaType);

        return string.Equals(mediaType.Kind, TerraformMediaTypes.Terraform.Kind, StringComparison.OrdinalIgnoreCase)
               || string.Equals(mediaType.Kind, TerraformMediaTypes.TerraformVars.Kind, StringComparison.OrdinalIgnoreCase);
    }

    public async Task<bool> CanLoadAsync(DiscoveredArtifact artifact, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);

        var name = artifact.File.Name.ToLowerInvariant();
        var extension = Path.GetExtension(name);

        if (TerraformMediaTypes.TryResolve(extension, out var mediaType))
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
            throw new InvalidOperationException("RepoUri required to load Terraform files.");

        var loaded = await FileContentReader.ReadAllTextWithDigestAsync(
            artifact.File,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var text = loaded.Text;
        var digest = loaded.Digest;

        var parseResult = _client.Parse(text);

        var state = new TerraformDocumentState
        {
            DocumentId = Guid.NewGuid(),
            ParseResult = parseResult,
            Digest = digest,
            Size = loaded.ByteLength,
            MediaType = artifact.MediaType ?? TerraformMediaTypes.Terraform,
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
        var state = document.GetMetadataOrDefault<TerraformDocumentState>(StateMetadataKey)
            ?? throw new InvalidOperationException("Terraform document missing state metadata.");

        var parseResult = state.ParseResult;

        // Calculate token count for the text content
        var tokenCount = TokenEstimator.EstimateTokensSafe(document.Text);

        // Generate X-ray summaries
        var headline = BuildHeadline(document, parseResult, tokenCount);
        var structure = BuildStructure(parseResult);

        var artifact = new Artifact
        {
            Id = Guid.NewGuid(),
            Digest = state.Digest,
            Size = state.Size,
            MediaType = state.MediaType,
            Text = document.Text,
            StoreUri = state.StoreUri,
            TokenCount = tokenCount,
            Headline = headline,
            Structure = structure
        };

        var docNode = new Node
        {
            Id = state.DocumentId,
            Kind = TerraformNodeKinds.Document,
            Uri = document.Uri,
            ArtifactId = artifact.Id,
            Props = new JsonObject
            {
                [TerraformPropertyKeys.Language] = "terraform",
                [TerraformPropertyKeys.ByteSize] = artifact.Size,
                [TerraformPropertyKeys.LineCount] = document.LineMap.LineCount,
                ["resources"] = parseResult.Resources.Count,
                ["variables"] = parseResult.Variables.Count,
                ["outputs"] = parseResult.Outputs.Count,
                ["modules"] = parseResult.Modules.Count,
                ["providers"] = parseResult.Providers.Count,
                ["data_sources"] = parseResult.DataSources.Count
            },
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        var nodes = new List<Node> { docNode };
        var spans = new List<Span>();
        var edges = new List<Edge>();
        var now = DateTimeOffset.UtcNow;
        var ordinal = 0;

        // Materialize resources
        foreach (var resource in parseResult.Resources)
        {
            var span = CreateSpan(resource.Span, state.DocumentId, document);
            spans.Add(span);
            var symbolName = $"{resource.ResourceType}.{resource.Name}";
            var node = CreateResourceNode(resource, artifact.Id, document.Uri.Container, span, now);
            nodes.Add(node);
            edges.Add(CreateComposition(docNode.Id, node.Id, ordinal++, state.DocumentId, now));
        }

        // Materialize data sources
        foreach (var data in parseResult.DataSources)
        {
            var span = CreateSpan(data.Span, state.DocumentId, document);
            spans.Add(span);
            var node = CreateDataNode(data, artifact.Id, document.Uri.Container, span, now);
            nodes.Add(node);
            edges.Add(CreateComposition(docNode.Id, node.Id, ordinal++, state.DocumentId, now));
        }

        // Materialize variables
        foreach (var variable in parseResult.Variables)
        {
            var span = CreateSpan(variable.Span, state.DocumentId, document);
            spans.Add(span);
            var node = CreateVariableNode(variable, artifact.Id, document.Uri.Container, span, now);
            nodes.Add(node);
            edges.Add(CreateComposition(docNode.Id, node.Id, ordinal++, state.DocumentId, now));
        }

        // Materialize outputs
        foreach (var output in parseResult.Outputs)
        {
            var span = CreateSpan(output.Span, state.DocumentId, document);
            spans.Add(span);
            var node = CreateOutputNode(output, artifact.Id, document.Uri.Container, span, now);
            nodes.Add(node);
            edges.Add(CreateComposition(docNode.Id, node.Id, ordinal++, state.DocumentId, now));
        }

        // Materialize modules
        foreach (var module in parseResult.Modules)
        {
            var span = CreateSpan(module.Span, state.DocumentId, document);
            spans.Add(span);
            var node = CreateModuleNode(module, artifact.Id, document.Uri.Container, span, now);
            nodes.Add(node);
            edges.Add(CreateComposition(docNode.Id, node.Id, ordinal++, state.DocumentId, now));
        }

        // Materialize providers
        foreach (var provider in parseResult.Providers)
        {
            var span = CreateSpan(provider.Span, state.DocumentId, document);
            spans.Add(span);
            var node = CreateProviderNode(provider, artifact.Id, document.Uri.Container, span, now);
            nodes.Add(node);
            edges.Add(CreateComposition(docNode.Id, node.Id, ordinal++, state.DocumentId, now));
        }

        // Materialize locals
        foreach (var local in parseResult.Locals)
        {
            var span = CreateSpan(local.Span, state.DocumentId, document);
            spans.Add(span);
            var node = CreateLocalsNode(artifact.Id, document.Uri.Container, span, now);
            nodes.Add(node);
            edges.Add(CreateComposition(docNode.Id, node.Id, ordinal++, state.DocumentId, now));
        }

        // Materialize terraform blocks
        foreach (var tfBlock in parseResult.TerraformBlocks)
        {
            var span = CreateSpan(tfBlock.Span, state.DocumentId, document);
            spans.Add(span);
            var node = CreateTerraformBlockNode(tfBlock, artifact.Id, document.Uri.Container, span, now);
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

    private static string BuildHeadline(DocumentModel document, TerraformParseResult parseResult, int? tokenCount)
    {
        var fileName = GetFileName(document.Uri);
        var parts = new List<string> { fileName };

        if (parseResult.Resources.Count > 0)
        {
            var resourceNames = parseResult.Resources
                .Take(3)
                .Select(r => $"{r.ResourceType}.{r.Name}");
            parts.Add(string.Join(", ", resourceNames));
            if (parseResult.Resources.Count > 3)
                parts.Add($"+{parseResult.Resources.Count - 3} more");
        }
        else if (parseResult.Variables.Count > 0)
        {
            parts.Add($"{parseResult.Variables.Count} variables");
        }
        else if (parseResult.Outputs.Count > 0)
        {
            parts.Add($"{parseResult.Outputs.Count} outputs");
        }

        var tokenPart = tokenCount.HasValue ? $" | {tokenCount.Value} tokens" : string.Empty;
        return string.Join(" | ", parts) + tokenPart;
    }

    private static string BuildStructure(TerraformParseResult parseResult)
    {
        var sb = new StringBuilder();

        if (parseResult.Providers.Count > 0)
        {
            sb.AppendLine("Providers:");
            foreach (var p in parseResult.Providers)
            {
                var extra = p.Region != null ? $" region={p.Region}" : "";
                sb.AppendLine($"  provider {p.ProviderType}{extra}");
            }
        }

        if (parseResult.Resources.Count > 0)
        {
            sb.AppendLine("Resources:");
            foreach (var r in parseResult.Resources)
            {
                sb.AppendLine($"  resource {r.ResourceType} {r.Name}");
            }
        }

        if (parseResult.DataSources.Count > 0)
        {
            sb.AppendLine("Data Sources:");
            foreach (var d in parseResult.DataSources)
            {
                sb.AppendLine($"  data {d.ResourceType} {d.Name}");
            }
        }

        if (parseResult.Modules.Count > 0)
        {
            sb.AppendLine("Modules:");
            foreach (var m in parseResult.Modules)
            {
                var src = m.Source != null ? $" source={m.Source}" : "";
                sb.AppendLine($"  module {m.Name}{src}");
            }
        }

        if (parseResult.Variables.Count > 0)
        {
            sb.AppendLine("Variables:");
            foreach (var v in parseResult.Variables)
            {
                var type = v.Type != null ? $": {v.Type}" : "";
                sb.AppendLine($"  variable {v.Name}{type}");
            }
        }

        if (parseResult.Outputs.Count > 0)
        {
            sb.AppendLine("Outputs:");
            foreach (var o in parseResult.Outputs)
            {
                sb.AppendLine($"  output {o.Name}");
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static Node CreateResourceNode(TerraformResourceInfo resource, Guid artifactId, Uri container, Span span, DateTimeOffset now)
    {
        var symbolName = $"{resource.ResourceType}.{resource.Name}";
        return new Node
        {
            Id = Guid.NewGuid(),
            Kind = TerraformNodeKinds.Resource,
            ArtifactId = artifactId,
            SpanId = span.Id,
            Uri = RepoUri.FromSymbol(container, symbolName, span.StartLine, span.EndLine),
            Props = new JsonObject
            {
                [TerraformPropertyKeys.Name] = resource.Name,
                [TerraformPropertyKeys.ResourceType] = resource.ResourceType
            },
            Headline = $"resource {resource.ResourceType} {resource.Name}",
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static Node CreateDataNode(TerraformDataInfo data, Guid artifactId, Uri container, Span span, DateTimeOffset now)
    {
        var symbolName = $"data.{data.ResourceType}.{data.Name}";
        return new Node
        {
            Id = Guid.NewGuid(),
            Kind = TerraformNodeKinds.Data,
            ArtifactId = artifactId,
            SpanId = span.Id,
            Uri = RepoUri.FromSymbol(container, symbolName, span.StartLine, span.EndLine),
            Props = new JsonObject
            {
                [TerraformPropertyKeys.Name] = data.Name,
                [TerraformPropertyKeys.ResourceType] = data.ResourceType
            },
            Headline = $"data {data.ResourceType} {data.Name}",
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static Node CreateVariableNode(TerraformVariableInfo variable, Guid artifactId, Uri container, Span span, DateTimeOffset now)
    {
        var props = new JsonObject
        {
            [TerraformPropertyKeys.Name] = variable.Name
        };
        if (variable.Type != null)
            props[TerraformPropertyKeys.Type] = variable.Type;
        if (variable.Default != null)
            props[TerraformPropertyKeys.Default] = variable.Default;
        if (variable.Description != null)
            props[TerraformPropertyKeys.Description] = variable.Description;

        var headline = variable.Type != null
            ? $"variable {variable.Name}: {variable.Type}"
            : $"variable {variable.Name}";

        return new Node
        {
            Id = Guid.NewGuid(),
            Kind = TerraformNodeKinds.Variable,
            ArtifactId = artifactId,
            SpanId = span.Id,
            Uri = RepoUri.FromSymbol(container, $"var.{variable.Name}", span.StartLine, span.EndLine),
            Props = props,
            Headline = headline,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static Node CreateOutputNode(TerraformOutputInfo output, Guid artifactId, Uri container, Span span, DateTimeOffset now)
    {
        var props = new JsonObject
        {
            [TerraformPropertyKeys.Name] = output.Name
        };
        if (output.Description != null)
            props[TerraformPropertyKeys.Description] = output.Description;

        return new Node
        {
            Id = Guid.NewGuid(),
            Kind = TerraformNodeKinds.Output,
            ArtifactId = artifactId,
            SpanId = span.Id,
            Uri = RepoUri.FromSymbol(container, $"output.{output.Name}", span.StartLine, span.EndLine),
            Props = props,
            Headline = $"output {output.Name}",
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static Node CreateModuleNode(TerraformModuleInfo module, Guid artifactId, Uri container, Span span, DateTimeOffset now)
    {
        var props = new JsonObject
        {
            [TerraformPropertyKeys.Name] = module.Name
        };
        if (module.Source != null)
            props[TerraformPropertyKeys.Source] = module.Source;
        if (module.Version != null)
            props[TerraformPropertyKeys.Version] = module.Version;

        var headline = module.Source != null
            ? $"module {module.Name} source={module.Source}"
            : $"module {module.Name}";

        return new Node
        {
            Id = Guid.NewGuid(),
            Kind = TerraformNodeKinds.Module,
            ArtifactId = artifactId,
            SpanId = span.Id,
            Uri = RepoUri.FromSymbol(container, $"module.{module.Name}", span.StartLine, span.EndLine),
            Props = props,
            Headline = headline,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static Node CreateProviderNode(TerraformProviderInfo provider, Guid artifactId, Uri container, Span span, DateTimeOffset now)
    {
        var props = new JsonObject
        {
            [TerraformPropertyKeys.Name] = provider.ProviderType
        };
        if (provider.Region != null)
            props["region"] = provider.Region;
        if (provider.Alias != null)
            props["alias"] = provider.Alias;

        var headline = provider.Region != null
            ? $"provider {provider.ProviderType} region={provider.Region}"
            : $"provider {provider.ProviderType}";

        var symbolName = provider.Alias != null
            ? $"provider.{provider.ProviderType}.{provider.Alias}"
            : $"provider.{provider.ProviderType}";

        return new Node
        {
            Id = Guid.NewGuid(),
            Kind = TerraformNodeKinds.Provider,
            ArtifactId = artifactId,
            SpanId = span.Id,
            Uri = RepoUri.FromSymbol(container, symbolName, span.StartLine, span.EndLine),
            Props = props,
            Headline = headline,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static Node CreateLocalsNode(Guid artifactId, Uri container, Span span, DateTimeOffset now)
    {
        return new Node
        {
            Id = Guid.NewGuid(),
            Kind = TerraformNodeKinds.Locals,
            ArtifactId = artifactId,
            SpanId = span.Id,
            Uri = RepoUri.FromSymbol(container, "locals", span.StartLine, span.EndLine),
            Props = new JsonObject(),
            Headline = "locals",
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static Node CreateTerraformBlockNode(TerraformBlockInfo tfBlock, Guid artifactId, Uri container, Span span, DateTimeOffset now)
    {
        var props = new JsonObject();
        if (tfBlock.RequiredVersion != null)
            props[TerraformPropertyKeys.RequiredVersion] = tfBlock.RequiredVersion;

        var headline = tfBlock.RequiredVersion != null
            ? $"terraform required_version={tfBlock.RequiredVersion}"
            : "terraform";

        return new Node
        {
            Id = Guid.NewGuid(),
            Kind = TerraformNodeKinds.Terraform,
            ArtifactId = artifactId,
            SpanId = span.Id,
            Uri = RepoUri.FromSymbol(container, "terraform", span.StartLine, span.EndLine),
            Props = props,
            Headline = headline,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static Span CreateSpan(TerraformSpan tfSpan, Guid docId, DocumentModel document)
    {
        var start = Math.Clamp(tfSpan.Start, 0, document.Text.Length);
        var end = Math.Clamp(tfSpan.End, start, document.Text.Length);
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
}
