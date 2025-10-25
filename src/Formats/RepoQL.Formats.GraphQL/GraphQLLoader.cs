using System.Text;
using System.Text.Json.Nodes;
using GraphQLParser;
using GraphQLParser.AST;
using GraphQLParser.Exceptions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RepoQL.Contracts;
using RepoQL.Contracts.Models;
using RepoQL.Templating;

namespace RepoQL.Formats.GraphQL;

public sealed partial class GraphQLLoader(ITemplateRenderer? renderer = null, ILogger<GraphQLLoader>? logger = null)
    : IFormatLoader, IFormatMaterializer
{
    internal const string StateMetadataKey = "graphql.state";

    private readonly ITemplateRenderer _renderer = renderer ?? new LiquidTemplateRenderer(
        assembly: typeof(GraphQLLoader).Assembly,
        resourceRoot: "RepoQL.Formats.GraphQL.Templates");

    private ILogger<GraphQLLoader> Logger { get; } = logger ?? NullLogger<GraphQLLoader>.Instance;

    private static readonly SemanticMediaType GraphQLMediaType = SemanticMediaType
        .Create("text", "graphql")
        .WithKind("graphql.doc");

    private static readonly HashSet<string> KnownExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".graphql", ".graphqls", ".gql", ".gqls"
    };

    public bool Supports(SemanticMediaType mediaType)
    {
        ArgumentNullException.ThrowIfNull(mediaType);
        if (string.Equals(mediaType.Kind, GraphQLMediaType.Kind, StringComparison.OrdinalIgnoreCase))
            return true;

        return string.Equals(mediaType.Type, GraphQLMediaType.Type, StringComparison.OrdinalIgnoreCase)
               && string.Equals(mediaType.Subtype, GraphQLMediaType.Subtype, StringComparison.OrdinalIgnoreCase);
    }

    public Task<bool> CanLoadAsync(DiscoveredArtifact artifact, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        var name = artifact.File.Name ?? string.Empty;
        if (KnownExtensions.Any(name.EndsWith))
        {
            artifact.MediaType = GraphQLMediaType;
            return Task.FromResult(true);
        }

        if (artifact.MediaType is not null &&
            (string.Equals(artifact.MediaType.Kind, "graphql.doc", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(artifact.MediaType.Subtype, "graphql", StringComparison.OrdinalIgnoreCase)))
        {
            artifact.MediaType = artifact.MediaType.WithKind("graphql.doc");
            return Task.FromResult(true);
        }

        return Task.FromResult(false);
    }

    public async Task<DocumentModel> LoadAsync(DiscoveredArtifact artifact, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        if (artifact.RepoUri is null)
            throw new InvalidOperationException("RepoUri required for GraphQL loader.");

        var mediaType = artifact.MediaType ?? GraphQLMediaType;

        var loaded = await FileContentReader.ReadAllTextWithDigestAsync(
            artifact.File,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var text = loaded.Text;
        var digest = loaded.Digest;
        GraphQLDocument documentAst;
        try
        {
            documentAst = Parser.Parse(text);
        }
        catch (GraphQLSyntaxErrorException ex)
        {
            throw new InvalidOperationException($"Failed to parse GraphQL document: {artifact.File.Name}", ex);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Unexpected failure parsing GraphQL document: {artifact.File.Name}", ex);
        }

        var state = BuildState(documentAst, artifact, digest, mediaType, loaded.ByteLength);
        var metadata = new Dictionary<string, object?>
        {
            [StateMetadataKey] = state
        };

        return new DocumentModel(artifact.RepoUri, mediaType, text, documentAst, metadata);
    }

    public Records Materialize(DocumentModel document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!Supports(document.MediaType))
            throw new InvalidOperationException("Document media type not supported by GraphQL materializer.");

        var state = document.GetMetadataOrDefault<GraphQLDocumentState>(StateMetadataKey)
                    ?? throw new InvalidOperationException("GraphQL document missing state metadata.");

        var rendererModel = GraphQLXrayModelBuilder.Build(document, state);

        string? headline = null;
        string? summary = null;
        string? structure = null;
        try
        {
            headline = _renderer.RenderAsync("xray/headline", rendererModel).GetAwaiter().GetResult().Trim();
            summary = _renderer.RenderAsync("xray/summary", rendererModel).GetAwaiter().GetResult().Trim();
            structure = _renderer.RenderAsync("xray/structure", rendererModel).GetAwaiter().GetResult().Trim();
        }
        catch (Exception ex)
        {
            LogFailedToRenderTemplates(Logger, ex, document.Uri.ToString());
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
                ["queries"] = state.Counts.QueryCount,
                ["mutations"] = state.Counts.MutationCount,
                ["subscriptions"] = state.Counts.SubscriptionCount,
                ["fragments"] = state.Counts.FragmentCount,
                ["types"] = state.Types.Count,
                ["directives"] = state.Counts.DirectiveCount,
                ["has_schema"] = state.HasSchemaDefinition
            },
            CreatedAt = now,
            UpdatedAt = now
        };

        var nodes = new List<Node> { docNode };
        var spans = new List<Span>();
        var edges = new List<Edge>();
        var ordinal = 0;

        var fragmentLookup = new Dictionary<string, GraphQLFragmentInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var fragment in state.Fragments)
            fragmentLookup[fragment.Name] = fragment;

        var typeLookup = new Dictionary<string, GraphQLTypeInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var type in state.Types)
            typeLookup[type.Name] = type;

        foreach (var operation in state.Operations)
        {
            spans.Add(operation.Span.ToSpan(document, docNode.Id, operation.SpanId));

            var opProps = new JsonObject
            {
                ["name"] = operation.Name,
                ["kind"] = operation.Kind.ToString().ToLowerInvariant(),
                ["variables"] = operation.Variables.Count,
                ["directives"] = operation.DirectiveCount
            };
            if (operation.TopLevelFields.Count > 0)
            {
                opProps["fields"] = new JsonArray(operation.TopLevelFields.Select(f => JsonValue.Create(f)!).ToArray());
            }

            nodes.Add(new Node
            {
                Id = operation.NodeId,
                Kind = "graphql.operation",
                SpanId = operation.SpanId,
                Props = opProps,
                CreatedAt = now,
                UpdatedAt = now
            });
            edges.Add(CreateHasPart(docNode.Id, operation.NodeId, docNode.Id, ordinal++, now));

            foreach (var usage in operation.FragmentUsages)
            {
                spans.Add(usage.Span.ToSpan(document, docNode.Id, usage.UsageSpanId));
                if (fragmentLookup.TryGetValue(usage.Name, out var frag))
                {
                    edges.Add(CreateReference(operation.NodeId, frag.NodeId, docNode.Id, now));
                }
            }

            foreach (var variableUsage in operation.VariableUsages)
            {
                spans.Add(variableUsage.Span.ToSpan(document, docNode.Id, variableUsage.UsageSpanId));
            }

            foreach (var variableDefinition in operation.Variables)
            {
                spans.Add(variableDefinition.Span.ToSpan(document, docNode.Id, Guid.NewGuid()));
            }
        }

        foreach (var fragment in state.Fragments)
        {
            spans.Add(fragment.Span.ToSpan(document, docNode.Id, fragment.SpanId));

            var props = new JsonObject
            {
                ["name"] = fragment.Name,
                ["type_condition"] = fragment.TypeCondition,
                ["directives"] = fragment.DirectiveCount,
                ["uses_fragments"] = fragment.FragmentUsages.Count
            };

            nodes.Add(new Node
            {
                Id = fragment.NodeId,
                Kind = "graphql.fragment",
                SpanId = fragment.SpanId,
                Props = props,
                CreatedAt = now,
                UpdatedAt = now
            });
            edges.Add(CreateHasPart(docNode.Id, fragment.NodeId, docNode.Id, ordinal++, now));

            foreach (var usage in fragment.FragmentUsages)
            {
                spans.Add(usage.Span.ToSpan(document, docNode.Id, usage.UsageSpanId));
                if (fragmentLookup.TryGetValue(usage.Name, out var target))
                {
                    edges.Add(CreateReference(fragment.NodeId, target.NodeId, docNode.Id, now));
                }
            }
        }

        foreach (var type in state.Types)
        {
            spans.Add(type.Span.ToSpan(document, docNode.Id, type.SpanId));

            var props = new JsonObject
            {
                ["name"] = type.Name,
                ["kind"] = type.Kind.ToString().ToLowerInvariant(),
                ["fields"] = type.Fields.Count,
                ["enum_values"] = type.EnumValues.Count,
                ["union_members"] = type.UnionMembers.Count,
                ["implements"] = new JsonArray(type.Implements.Select(i => JsonValue.Create(i)!).ToArray()),
                ["has_description"] = type.HasDescription
            };

            nodes.Add(new Node
            {
                Id = type.NodeId,
                Kind = $"graphql.{type.Kind.ToString().ToLowerInvariant()}",
                SpanId = type.SpanId,
                Props = props,
                CreatedAt = now,
                UpdatedAt = now
            });
            edges.Add(CreateHasPart(docNode.Id, type.NodeId, docNode.Id, ordinal++, now));

            foreach (var iface in type.Implements)
            {
                if (typeLookup.TryGetValue(iface, out var ifaceInfo))
                {
                    edges.Add(CreateImplement(type.NodeId, ifaceInfo.NodeId, docNode.Id, now));
                }
            }

            foreach (var field in type.Fields)
            {
                spans.Add(field.Span.ToSpan(document, docNode.Id, field.SpanId));

                nodes.Add(new Node
                {
                    Id = field.NodeId,
                    Kind = "graphql.field",
                    SpanId = field.SpanId,
                    Props = new JsonObject
                    {
                        ["name"] = field.Name,
                        ["type"] = field.Type,
                        ["arguments"] = field.ArgumentCount,
                        ["deprecated"] = field.IsDeprecated,
                        ["deprecation_reason"] = field.DeprecationReason,
                        ["has_description"] = field.HasDescription
                    },
                    CreatedAt = now,
                    UpdatedAt = now
                });
                edges.Add(CreateHasPart(type.NodeId, field.NodeId, docNode.Id, null, now));
            }

            foreach (var member in type.UnionMembers)
            {
                if (typeLookup.TryGetValue(member, out var unionMember))
                {
                    edges.Add(CreateReference(type.NodeId, unionMember.NodeId, docNode.Id, now));
                }
            }

            foreach (var enumValue in type.EnumValues)
            {
                spans.Add(enumValue.Span.ToSpan(document, docNode.Id, enumValue.SpanId));
                nodes.Add(new Node
                {
                    Id = enumValue.NodeId,
                    Kind = "graphql.enum_value",
                    SpanId = enumValue.SpanId,
                    Props = new JsonObject
                    {
                        ["name"] = enumValue.Name,
                        ["deprecated"] = enumValue.IsDeprecated,
                        ["deprecation_reason"] = enumValue.DeprecationReason,
                        ["has_description"] = enumValue.HasDescription
                    },
                    CreatedAt = now,
                    UpdatedAt = now
                });
                edges.Add(CreateHasPart(type.NodeId, enumValue.NodeId, docNode.Id, null, now));
            }
        }

        foreach (var directive in state.Directives)
        {
            spans.Add(directive.Span.ToSpan(document, docNode.Id, directive.SpanId));
            var props = new JsonObject
            {
                ["name"] = directive.Name,
                ["repeatable"] = directive.IsRepeatable,
                ["locations"] = new JsonArray(directive.Locations.Select(l => JsonValue.Create(l)!).ToArray()),
                ["arguments"] = directive.ArgumentCount,
                ["has_description"] = directive.HasDescription
            };

            nodes.Add(new Node
            {
                Id = directive.NodeId,
                Kind = "graphql.directive",
                SpanId = directive.SpanId,
                Props = props,
                CreatedAt = now,
                UpdatedAt = now
            });
            edges.Add(CreateHasPart(docNode.Id, directive.NodeId, docNode.Id, ordinal++, now));
        }

        return new Records
        {
            Artifacts = [artifact],
            Nodes = [.. nodes],
            Spans = [.. spans],
            Edges = [.. edges]
        };
    }

    private static bool IsGraphQLKeyword(string line)
    {
        if (line.Length == 0) return false;
        const StringComparison cmp = StringComparison.OrdinalIgnoreCase;
        return line.StartsWith("query", cmp)
               || line.StartsWith("mutation", cmp)
               || line.StartsWith("subscription", cmp)
               || line.StartsWith("fragment", cmp)
               || line.StartsWith("schema", cmp)
               || line.StartsWith("type", cmp)
               || line.StartsWith("interface", cmp)
               || line.StartsWith("union", cmp)
               || line.StartsWith("enum", cmp)
               || line.StartsWith("input", cmp)
               || line.StartsWith("scalar", cmp)
               || line.StartsWith("{", StringComparison.Ordinal);
    }

    private static GraphQLDocumentState BuildState(GraphQLDocument document, DiscoveredArtifact artifact, string digest, SemanticMediaType mediaType, long byteLength)
    {
        var operations = new List<GraphQLOperationInfo>();
        var fragments = new List<GraphQLFragmentInfo>();
        var types = new List<GraphQLTypeInfo>();
        var directives = new List<GraphQLDirectiveInfo>();
        var documentId = Guid.NewGuid();

        var counts = new GraphQLCounts(
            QueryCount: 0,
            MutationCount: 0,
            SubscriptionCount: 0,
            FragmentCount: 0,
            ObjectTypeCount: 0,
            InterfaceTypeCount: 0,
            InputTypeCount: 0,
            EnumTypeCount: 0,
            UnionTypeCount: 0,
            ScalarTypeCount: 0,
            DirectiveCount: 0);

        var countsMutable = counts with { };
        var collector = new GraphQLStateCollector();
        var hasSchemaDefinition = false;

        foreach (var definition in document.Definitions)
        {
            switch (definition)
            {
                case GraphQLOperationDefinition operation:
                {
                    var info = collector.CreateOperation(operation);
                    operations.Add(info);
                    countsMutable = info.Kind switch
                    {
                        GraphQLOperationKind.Mutation => countsMutable with { MutationCount = countsMutable.MutationCount + 1 },
                        GraphQLOperationKind.Subscription => countsMutable with { SubscriptionCount = countsMutable.SubscriptionCount + 1 },
                        GraphQLOperationKind.Query or GraphQLOperationKind.Anonymous => countsMutable with { QueryCount = countsMutable.QueryCount + 1 },
                        _ => countsMutable
                    };
                    break;
                }
                case GraphQLFragmentDefinition fragment:
                {
                    fragments.Add(collector.CreateFragment(fragment));
                    countsMutable = countsMutable with { FragmentCount = countsMutable.FragmentCount + 1 };
                    break;
                }
                case GraphQLSchemaDefinition:
                    hasSchemaDefinition = true;
                    break;
                case GraphQLSchemaExtension:
                    hasSchemaDefinition = true;
                    break;
                case GraphQLObjectTypeDefinition obj:
                {
                    types.Add(collector.CreateType(obj));
                    countsMutable = countsMutable with { ObjectTypeCount = countsMutable.ObjectTypeCount + 1 };
                    break;
                }
                case GraphQLInterfaceTypeDefinition @interface:
                {
                    types.Add(collector.CreateType(@interface));
                    countsMutable = countsMutable with { InterfaceTypeCount = countsMutable.InterfaceTypeCount + 1 };
                    break;
                }
                case GraphQLInputObjectTypeDefinition input:
                {
                    types.Add(collector.CreateType(input));
                    countsMutable = countsMutable with { InputTypeCount = countsMutable.InputTypeCount + 1 };
                    break;
                }
                case GraphQLEnumTypeDefinition enumDef:
                {
                    types.Add(collector.CreateType(enumDef));
                    countsMutable = countsMutable with { EnumTypeCount = countsMutable.EnumTypeCount + 1 };
                    break;
                }
                case GraphQLUnionTypeDefinition unionDef:
                {
                    types.Add(collector.CreateType(unionDef));
                    countsMutable = countsMutable with { UnionTypeCount = countsMutable.UnionTypeCount + 1 };
                    break;
                }
                case GraphQLScalarTypeExtension scalarExt:
                    types.Add(collector.CreateType(scalarExt));
                    countsMutable = countsMutable with { ScalarTypeCount = countsMutable.ScalarTypeCount + 1 };
                    break;
                case GraphQLScalarTypeDefinition scalar:
                {
                    types.Add(collector.CreateType(scalar));
                    countsMutable = countsMutable with { ScalarTypeCount = countsMutable.ScalarTypeCount + 1 };
                    break;
                }
                case GraphQLDirectiveDefinition directiveDefinition:
                {
                    directives.Add(collector.CreateDirective(directiveDefinition));
                    countsMutable = countsMutable with { DirectiveCount = countsMutable.DirectiveCount + 1 };
                    break;
                }
                case GraphQLObjectTypeExtension objExt:
                    types.Add(collector.CreateType(objExt));
                    countsMutable = countsMutable with { ObjectTypeCount = countsMutable.ObjectTypeCount + 1 };
                    break;
                case GraphQLInterfaceTypeExtension ifaceExt:
                    types.Add(collector.CreateType(ifaceExt));
                    countsMutable = countsMutable with { InterfaceTypeCount = countsMutable.InterfaceTypeCount + 1 };
                    break;
                case GraphQLInputObjectTypeExtension inputExt:
                    types.Add(collector.CreateType(inputExt));
                    countsMutable = countsMutable with { InputTypeCount = countsMutable.InputTypeCount + 1 };
                    break;
                case GraphQLEnumTypeExtension enumExt:
                    types.Add(collector.CreateType(enumExt));
                    countsMutable = countsMutable with { EnumTypeCount = countsMutable.EnumTypeCount + 1 };
                    break;
                case GraphQLUnionTypeExtension unionExt:
                    types.Add(collector.CreateType(unionExt));
                    countsMutable = countsMutable with { UnionTypeCount = countsMutable.UnionTypeCount + 1 };
                    break;
                default:
                {
                    if (definition is GraphQLSchemaExtension)
                    {
                        hasSchemaDefinition = true;
                    }
                    break;
                }
            }
        }

        operations.Sort((a, b) => string.Compare(a.Name ?? string.Empty, b.Name ?? string.Empty, StringComparison.OrdinalIgnoreCase));
        fragments.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        types.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        directives.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

        return new GraphQLDocumentState
        {
            DocumentId = documentId,
            Digest = digest,
            Size = byteLength,
            MediaType = mediaType,
            StoreUri = artifact.RepoUri.ToString(),
            Operations = operations,
            Fragments = fragments,
            Types = types,
            Directives = directives,
            Counts = countsMutable,
            HasSchemaDefinition = hasSchemaDefinition
        };
    }

    private static Edge CreateHasPart(Guid documentId, Guid childId, Guid scopeDocumentId, int? ordinal, DateTimeOffset timestamp)
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

    private static Edge CreateReference(Guid srcId, Guid dstId, Guid scopeDocumentId, DateTimeOffset timestamp)
        => new()
        {
            Id = Guid.NewGuid(),
            SrcId = srcId,
            DstId = dstId,
            Type = "REFERS_TO",
            IsComposition = false,
            ScopeDocumentId = scopeDocumentId,
            CreatedAt = timestamp
        };

    private static Edge CreateImplement(Guid srcId, Guid dstId, Guid scopeDocumentId, DateTimeOffset timestamp)
        => new()
        {
            Id = Guid.NewGuid(),
            SrcId = srcId,
            DstId = dstId,
            Type = "IMPLEMENTS",
            IsComposition = false,
            ScopeDocumentId = scopeDocumentId,
            CreatedAt = timestamp
        };

    [LoggerMessage(LogLevel.Warning, "GraphQL loader failed to sniff file '{Name}'")]
    private static partial void LogFailedToSniffFile(ILogger logger, Exception ex, string name);

    [LoggerMessage(LogLevel.Warning, "GraphQL loader failed to render templates for {Uri}")]
    private static partial void LogFailedToRenderTemplates(ILogger logger, Exception ex, string uri);
}
