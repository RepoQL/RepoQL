using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RepoQL.Contracts;
using RepoQL.Contracts.Models;
using RepoQL.Formats.PHP.Surface;
using RepoQL.Formats.PHP.TreeSitter;

namespace RepoQL.Formats.PHP;

public sealed partial class PHPLoader : IFormatLoader, IFormatMaterializer, IFormatSchemaProvider, IDisposable
{
    private ILogger<PHPLoader> Logger { get; }
    private readonly PhpTreeSitterClient _client;
    private static readonly Lazy<string> PhpViewsSql = new(
        () => ReadEmbeddedResource("RepoQL.Formats.PHP.Schema.php_views.sql"));

    private const string StateMetadataKey = "php.state";

    public PHPLoader(ILogger<PHPLoader>? logger = null)
    {
        Logger = logger ?? NullLogger<PHPLoader>.Instance;
        _client = new PhpTreeSitterClient();
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

        var surface = _client.Parse(text);

        var state = new PHPDocumentState
        {
            DocumentId = Guid.NewGuid(),
            Surface = surface,
            Digest = digest,
            Size = loaded.ByteLength,
            MediaType = artifact.MediaType ?? PHPMediaTypes.PHP,
            StoreUri = artifact.RepoUri.ToString()
        };

        var metadata = new Dictionary<string, object?>
        {
            [StateMetadataKey] = state
        };

        return new DocumentModel(artifact.RepoUri, state.MediaType, text, surface, metadata);
    }

    public Records Materialize(DocumentModel document)
    {
        var state = document.GetMetadataOrDefault<PHPDocumentState>(StateMetadataKey)
            ?? throw new InvalidOperationException("PHP document missing state metadata.");

        var surface = state.Surface;

        string? headline = null;
        string? summary = null;
        string? structure = null;

        var tokenCount = TokenEstimator.EstimateTokensSafe(document.Text);

        try
        {
            headline = BuildHeadline(document, state, tokenCount);
            summary = null;
            structure = BuildStructure(state);
        }
        catch (Exception ex)
        {
            LogXrayBuildError(Logger, ex);
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

        foreach (var classInfo in surface.Classes)
        {
            var classSpanId = Guid.NewGuid();
            var classSpan = CreateSpan(classInfo.Span, state.DocumentId, classSpanId, document);
            var classNode = CreateClassNode(classInfo, artifact.Id, document.Uri, classSpan.StartLine, classSpan.EndLine, classSpanId, now);
            nodes.Add(classNode);
            edges.Add(CreateComposition(docNode.Id, classNode.Id, ordinal++, state.DocumentId, now));
            spans.Add(classSpan);

            var memberOrdinal = 0;
            foreach (var method in classInfo.Methods)
            {
                var methodSpanId = Guid.NewGuid();
                var methodSpan = CreateSpan(method.Span, state.DocumentId, methodSpanId, document);
                var methodNode = CreateMethodNode(method, artifact.Id, document.Uri, classInfo.Name, methodSpan.StartLine, methodSpan.EndLine, methodSpanId, now);
                nodes.Add(methodNode);
                edges.Add(CreateComposition(classNode.Id, methodNode.Id, memberOrdinal++, state.DocumentId, now));
                spans.Add(methodSpan);
            }

            foreach (var prop in classInfo.Properties)
            {
                var propertySpanId = Guid.NewGuid();
                var propertySpan = CreateSpan(prop.Span, state.DocumentId, propertySpanId, document);
                var propNode = CreatePropertyNode(prop, artifact.Id, document.Uri, classInfo.Name, propertySpan.StartLine, propertySpan.EndLine, propertySpanId, now);
                nodes.Add(propNode);
                edges.Add(CreateComposition(classNode.Id, propNode.Id, memberOrdinal++, state.DocumentId, now));
                spans.Add(propertySpan);
            }

            foreach (var constant in classInfo.Constants)
            {
                var constantSpanId = Guid.NewGuid();
                var constantSpan = CreateSpan(constant.Span, state.DocumentId, constantSpanId, document);
                var constantNode = CreateConstantNode(constant, artifact.Id, document.Uri, classInfo.Name, constantSpan.StartLine, constantSpan.EndLine, constantSpanId, now);
                nodes.Add(constantNode);
                edges.Add(CreateComposition(classNode.Id, constantNode.Id, memberOrdinal++, state.DocumentId, now));
                spans.Add(constantSpan);
            }

            if (!string.IsNullOrEmpty(classInfo.Extends))
                edges.Add(CreateReferenceEdge(classNode.Id, PHPEdgeTypes.Extends, classInfo.Extends, state.DocumentId, now));

            foreach (var iface in classInfo.Implements)
                edges.Add(CreateReferenceEdge(classNode.Id, PHPEdgeTypes.Implements, iface, state.DocumentId, now));

            foreach (var trait in classInfo.UsesTraits)
                edges.Add(CreateReferenceEdge(classNode.Id, PHPEdgeTypes.UsesTrait, trait, state.DocumentId, now));
        }

        foreach (var ifaceInfo in surface.Interfaces)
        {
            var interfaceSpanId = Guid.NewGuid();
            var interfaceSpan = CreateSpan(ifaceInfo.Span, state.DocumentId, interfaceSpanId, document);
            var ifaceNode = CreateInterfaceNode(ifaceInfo, artifact.Id, document.Uri, interfaceSpan.StartLine, interfaceSpan.EndLine, interfaceSpanId, now);
            nodes.Add(ifaceNode);
            edges.Add(CreateComposition(docNode.Id, ifaceNode.Id, ordinal++, state.DocumentId, now));
            spans.Add(interfaceSpan);

            var memberOrdinal = 0;
            foreach (var method in ifaceInfo.Methods)
            {
                var methodSpanId = Guid.NewGuid();
                var methodSpan = CreateSpan(method.Span, state.DocumentId, methodSpanId, document);
                var methodNode = CreateMethodNode(method, artifact.Id, document.Uri, ifaceInfo.Name, methodSpan.StartLine, methodSpan.EndLine, methodSpanId, now);
                nodes.Add(methodNode);
                edges.Add(CreateComposition(ifaceNode.Id, methodNode.Id, memberOrdinal++, state.DocumentId, now));
                spans.Add(methodSpan);
            }

            foreach (var constant in ifaceInfo.Constants)
            {
                var constantSpanId = Guid.NewGuid();
                var constantSpan = CreateSpan(constant.Span, state.DocumentId, constantSpanId, document);
                var constantNode = CreateConstantNode(constant, artifact.Id, document.Uri, ifaceInfo.Name, constantSpan.StartLine, constantSpan.EndLine, constantSpanId, now);
                nodes.Add(constantNode);
                edges.Add(CreateComposition(ifaceNode.Id, constantNode.Id, memberOrdinal++, state.DocumentId, now));
                spans.Add(constantSpan);
            }

            foreach (var baseIface in ifaceInfo.Extends)
                edges.Add(CreateReferenceEdge(ifaceNode.Id, PHPEdgeTypes.Extends, baseIface, state.DocumentId, now));
        }

        foreach (var traitInfo in surface.Traits)
        {
            var traitSpanId = Guid.NewGuid();
            var traitSpan = CreateSpan(traitInfo.Span, state.DocumentId, traitSpanId, document);
            var traitNode = CreateTraitNode(traitInfo, artifact.Id, document.Uri, traitSpan.StartLine, traitSpan.EndLine, traitSpanId, now);
            nodes.Add(traitNode);
            edges.Add(CreateComposition(docNode.Id, traitNode.Id, ordinal++, state.DocumentId, now));
            spans.Add(traitSpan);

            var memberOrdinal = 0;
            foreach (var method in traitInfo.Methods)
            {
                var methodSpanId = Guid.NewGuid();
                var methodSpan = CreateSpan(method.Span, state.DocumentId, methodSpanId, document);
                var methodNode = CreateMethodNode(method, artifact.Id, document.Uri, traitInfo.Name, methodSpan.StartLine, methodSpan.EndLine, methodSpanId, now);
                nodes.Add(methodNode);
                edges.Add(CreateComposition(traitNode.Id, methodNode.Id, memberOrdinal++, state.DocumentId, now));
                spans.Add(methodSpan);
            }

            foreach (var prop in traitInfo.Properties)
            {
                var propertySpanId = Guid.NewGuid();
                var propertySpan = CreateSpan(prop.Span, state.DocumentId, propertySpanId, document);
                var propNode = CreatePropertyNode(prop, artifact.Id, document.Uri, traitInfo.Name, propertySpan.StartLine, propertySpan.EndLine, propertySpanId, now);
                nodes.Add(propNode);
                edges.Add(CreateComposition(traitNode.Id, propNode.Id, memberOrdinal++, state.DocumentId, now));
                spans.Add(propertySpan);
            }
        }

        foreach (var enumInfo in surface.Enums)
        {
            var enumSpanId = Guid.NewGuid();
            var enumSpan = CreateSpan(enumInfo.Span, state.DocumentId, enumSpanId, document);
            var enumNode = CreateEnumNode(enumInfo, artifact.Id, document.Uri, enumSpan.StartLine, enumSpan.EndLine, enumSpanId, now);
            nodes.Add(enumNode);
            edges.Add(CreateComposition(docNode.Id, enumNode.Id, ordinal++, state.DocumentId, now));
            spans.Add(enumSpan);

            var memberOrdinal = 0;
            foreach (var caseInfo in enumInfo.Cases)
            {
                var caseSpanId = Guid.NewGuid();
                var caseSpan = CreateSpan(caseInfo.Span, state.DocumentId, caseSpanId, document);
                var caseNode = CreateEnumCaseNode(caseInfo, artifact.Id, document.Uri, enumInfo.Name, caseSpan.StartLine, caseSpan.EndLine, caseSpanId, now);
                nodes.Add(caseNode);
                edges.Add(CreateComposition(enumNode.Id, caseNode.Id, memberOrdinal++, state.DocumentId, now));
                spans.Add(caseSpan);
            }

            foreach (var method in enumInfo.Methods)
            {
                var methodSpanId = Guid.NewGuid();
                var methodSpan = CreateSpan(method.Span, state.DocumentId, methodSpanId, document);
                var methodNode = CreateMethodNode(method, artifact.Id, document.Uri, enumInfo.Name, methodSpan.StartLine, methodSpan.EndLine, methodSpanId, now);
                nodes.Add(methodNode);
                edges.Add(CreateComposition(enumNode.Id, methodNode.Id, memberOrdinal++, state.DocumentId, now));
                spans.Add(methodSpan);
            }

            foreach (var iface in enumInfo.Implements)
                edges.Add(CreateReferenceEdge(enumNode.Id, PHPEdgeTypes.Implements, iface, state.DocumentId, now));
        }

        foreach (var funcInfo in surface.Functions)
        {
            var functionSpanId = Guid.NewGuid();
            var functionSpan = CreateSpan(funcInfo.Span, state.DocumentId, functionSpanId, document);
            var funcNode = CreateFunctionNode(funcInfo, artifact.Id, document.Uri, functionSpan.StartLine, functionSpan.EndLine, functionSpanId, now);
            nodes.Add(funcNode);
            edges.Add(CreateComposition(docNode.Id, funcNode.Id, ordinal++, state.DocumentId, now));
            spans.Add(functionSpan);
        }

        return new Records
        {
            Artifacts = [artifact],
            Nodes = nodes.ToArray(),
            Spans = spans.ToArray(),
            Edges = edges.ToArray()
        };
    }

    public IEnumerable<FormatSqlScript> GetSchemaScripts()
    {
        yield return new FormatSqlScript("php_views", PhpViewsSql.Value);
    }

    public void Dispose()
    {
        _client.Dispose();
    }

    #region X-ray Builders

    private static string BuildHeadline(DocumentModel document, PHPDocumentState state, int? tokenCount)
    {
        var surface = state.Surface;
        var fileName = GetFileName(document.Uri);
        var sizePart = $"{document.LineMap.LineCount} ln";
        if (tokenCount.HasValue)
            sizePart = $"{sizePart}, {FormatTokenCount(tokenCount.Value)}";

        var namespacePart = $"ns:{surface.Namespace ?? "(global)"}";
        var keyNames = new List<string>();
        string? primaryDeclaration = null;

        if (surface.Classes.Count > 0)
        {
            var primary = surface.Classes[0];
            primaryDeclaration = BuildDeclHeadline(primary);
            keyNames.AddRange(primary.Methods.Select(m => m.Name));
            keyNames.AddRange(primary.Constants.Select(c => c.Name));
            keyNames.AddRange(primary.Properties.Select(p => NormalizeSymbolName(p.Name)));
        }
        else if (surface.Interfaces.Count > 0)
        {
            var primary = surface.Interfaces[0];
            primaryDeclaration = BuildDeclHeadline(primary);
            keyNames.AddRange(primary.Methods.Select(m => m.Name));
            keyNames.AddRange(primary.Constants.Select(c => c.Name));
        }
        else if (surface.Traits.Count > 0)
        {
            var primary = surface.Traits[0];
            primaryDeclaration = BuildDeclHeadline(primary);
            keyNames.AddRange(primary.Methods.Select(m => m.Name));
            keyNames.AddRange(primary.Properties.Select(p => NormalizeSymbolName(p.Name)));
        }
        else if (surface.Enums.Count > 0)
        {
            var primary = surface.Enums[0];
            primaryDeclaration = BuildDeclHeadline(primary);
            keyNames.AddRange(primary.Cases.Select(c => c.Name));
            keyNames.AddRange(primary.Methods.Select(m => m.Name));
        }
        else
        {
            keyNames.AddRange(surface.Functions.Select(f => f.Name));
        }

        var namesPart = FormatNameList(keyNames, maxNames: 8);
        return string.Join(" | ", new[] { fileName, primaryDeclaration, namespacePart, namesPart, sizePart }
            .Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    private static string BuildStructure(PHPDocumentState state)
    {
        var surface = state.Surface;
        var sb = new StringBuilder();

        var hasWrittenDeclaration = false;
        void SeparateDeclarations()
        {
            if (!hasWrittenDeclaration)
            {
                hasWrittenDeclaration = true;
                return;
            }
            sb.AppendLine();
        }

        foreach (var classInfo in surface.Classes)
        {
            SeparateDeclarations();
            sb.AppendLine($"+ {BuildDeclHeadline(classInfo)}");

            foreach (var constant in classInfo.Constants)
                sb.AppendLine($"  {FormatConstantStructureLine(constant)}");

            foreach (var property in classInfo.Properties)
            {
                var staticModifier = property.IsStatic ? "static " : string.Empty;
                var typePart = string.IsNullOrWhiteSpace(property.Type) ? string.Empty : $"{property.Type} ";
                sb.AppendLine($"  {AccessibilitySymbol(property.Accessibility)}{staticModifier}{typePart}{property.Name}    #symbol={NormalizeSymbolName(property.Name)}");
            }

            foreach (var method in classInfo.Methods)
                sb.AppendLine($"  {FormatMethodStructureLine(method)}");
        }

        foreach (var interfaceInfo in surface.Interfaces)
        {
            SeparateDeclarations();
            sb.AppendLine($"+ {BuildDeclHeadline(interfaceInfo)}");

            foreach (var constant in interfaceInfo.Constants)
                sb.AppendLine($"  {FormatConstantStructureLine(constant)}");

            foreach (var method in interfaceInfo.Methods)
                sb.AppendLine($"  {FormatMethodStructureLine(method)}");
        }

        foreach (var traitInfo in surface.Traits)
        {
            SeparateDeclarations();
            sb.AppendLine($"+ {BuildDeclHeadline(traitInfo)}");

            foreach (var property in traitInfo.Properties)
            {
                var staticModifier = property.IsStatic ? "static " : string.Empty;
                var typePart = string.IsNullOrWhiteSpace(property.Type) ? string.Empty : $"{property.Type} ";
                sb.AppendLine($"  {AccessibilitySymbol(property.Accessibility)}{staticModifier}{typePart}{property.Name}    #symbol={NormalizeSymbolName(property.Name)}");
            }

            foreach (var method in traitInfo.Methods)
                sb.AppendLine($"  {FormatMethodStructureLine(method)}");
        }

        foreach (var enumInfo in surface.Enums)
        {
            SeparateDeclarations();
            sb.AppendLine($"+ {BuildDeclHeadline(enumInfo)}");

            foreach (var caseInfo in enumInfo.Cases)
                sb.AppendLine($"  +case {caseInfo.Name}    #symbol={NormalizeSymbolName(caseInfo.Name)}");

            foreach (var method in enumInfo.Methods)
                sb.AppendLine($"  {FormatMethodStructureLine(method)}");
        }

        if (surface.Functions.Count > 0)
        {
            SeparateDeclarations();
            foreach (var functionInfo in surface.Functions)
            {
                var returnTypePart = string.IsNullOrWhiteSpace(functionInfo.ReturnType) ? string.Empty : $"{functionInfo.ReturnType} ";
                var parameters = FormatParameters(functionInfo.Parameters, includeNames: true);
                sb.AppendLine($"+{returnTypePart}{functionInfo.Name}({parameters})    #symbol={NormalizeSymbolName(functionInfo.Name)}");
            }
        }

        return sb.ToString().TrimEnd();
    }

    #endregion

    #region Declaration Headlines

    private static string BuildDeclHeadline(PhpClassInfo classInfo)
    {
        var sb = new StringBuilder();
        if (classInfo.IsAbstract) sb.Append("abstract ");
        if (classInfo.IsFinal) sb.Append("final ");
        if (classInfo.IsReadonly) sb.Append("readonly ");
        sb.Append("class ").Append(classInfo.Name);
        if (!string.IsNullOrWhiteSpace(classInfo.Extends))
            sb.Append(" extends ").Append(classInfo.Extends);
        if (classInfo.Implements.Count > 0)
            sb.Append(" implements ").Append(string.Join(", ", classInfo.Implements));
        return sb.ToString();
    }

    private static string BuildDeclHeadline(PhpInterfaceInfo interfaceInfo)
    {
        var sb = new StringBuilder();
        sb.Append("interface ").Append(interfaceInfo.Name);
        if (interfaceInfo.Extends.Count > 0)
            sb.Append(" extends ").Append(string.Join(", ", interfaceInfo.Extends));
        return sb.ToString();
    }

    private static string BuildDeclHeadline(PhpTraitInfo traitInfo)
        => $"trait {traitInfo.Name}";

    private static string BuildDeclHeadline(PhpEnumInfo enumInfo)
    {
        var sb = new StringBuilder();
        sb.Append("enum ").Append(enumInfo.Name);
        if (!string.IsNullOrWhiteSpace(enumInfo.BackedType))
            sb.Append(": ").Append(enumInfo.BackedType);
        if (enumInfo.Implements.Count > 0)
            sb.Append(" implements ").Append(string.Join(", ", enumInfo.Implements));
        return sb.ToString();
    }

    private static string BuildDeclHeadline(PhpFunctionInfo functionInfo)
    {
        var parameters = FormatParameters(functionInfo.Parameters, includeNames: true);
        var signature = $"function {functionInfo.Name}({parameters})";
        if (!string.IsNullOrWhiteSpace(functionInfo.ReturnType))
            signature += $": {functionInfo.ReturnType}";
        return signature;
    }

    private static string BuildDeclHeadline(PhpMethodInfo methodInfo)
    {
        var parts = new List<string> { methodInfo.Accessibility ?? "public" };
        if (methodInfo.IsStatic) parts.Add("static");
        if (methodInfo.IsAbstract) parts.Add("abstract");
        if (methodInfo.IsFinal) parts.Add("final");
        parts.Add("function");
        var parameters = FormatParameters(methodInfo.Parameters, includeNames: true);
        var signature = $"{string.Join(" ", parts)} {methodInfo.Name}({parameters})";
        if (!string.IsNullOrWhiteSpace(methodInfo.ReturnType))
            signature += $": {methodInfo.ReturnType}";
        return signature;
    }

    private static string BuildDeclHeadline(PhpPropertyInfo propertyInfo)
    {
        var parts = new List<string> { propertyInfo.Accessibility ?? "public" };
        if (propertyInfo.IsStatic) parts.Add("static");
        if (propertyInfo.IsReadonly) parts.Add("readonly");
        if (!string.IsNullOrWhiteSpace(propertyInfo.Type)) parts.Add(propertyInfo.Type);
        parts.Add(propertyInfo.Name);
        return string.Join(" ", parts);
    }

    private static string BuildDeclHeadline(PhpConstantInfo constantInfo)
        => $"{constantInfo.Accessibility ?? "public"} const {constantInfo.Name}";

    private static string BuildDeclHeadline(PhpEnumCaseInfo enumCaseInfo)
        => $"case {enumCaseInfo.Name}";

    #endregion

    #region Declaration Structures

    private static string? BuildDeclStructure(PhpClassInfo classInfo)
    {
        var lines = new List<string>();
        lines.AddRange(classInfo.Constants.Select(FormatCompactConstant));
        lines.AddRange(classInfo.Properties.Select(FormatCompactProperty));
        lines.AddRange(classInfo.Methods.Select(FormatCompactMethod));
        return lines.Count == 0 ? null : string.Join(Environment.NewLine, lines);
    }

    private static string? BuildDeclStructure(PhpInterfaceInfo interfaceInfo)
    {
        var lines = new List<string>();
        lines.AddRange(interfaceInfo.Constants.Select(FormatCompactConstant));
        lines.AddRange(interfaceInfo.Methods.Select(FormatCompactMethod));
        return lines.Count == 0 ? null : string.Join(Environment.NewLine, lines);
    }

    private static string? BuildDeclStructure(PhpTraitInfo traitInfo)
    {
        var lines = new List<string>();
        lines.AddRange(traitInfo.Properties.Select(FormatCompactProperty));
        lines.AddRange(traitInfo.Methods.Select(FormatCompactMethod));
        return lines.Count == 0 ? null : string.Join(Environment.NewLine, lines);
    }

    private static string? BuildDeclStructure(PhpEnumInfo enumInfo)
    {
        var lines = new List<string>();
        lines.AddRange(enumInfo.Cases.Select(c => $"+case {c.Name}"));
        lines.AddRange(enumInfo.Methods.Select(FormatCompactMethod));
        return lines.Count == 0 ? null : string.Join(Environment.NewLine, lines);
    }

    #endregion

    #region Node Builders

    private static Node CreateClassNode(PhpClassInfo classInfo, Guid artifactId, RepoUri documentUri, int? startLine, int? endLine, Guid spanId, DateTimeOffset now)
    {
        var props = new JsonObject
        {
            [PHPPropertyKeys.Name] = classInfo.Name,
            [PHPPropertyKeys.Kind] = "class"
        };

        if (!string.IsNullOrEmpty(classInfo.Namespace))
        {
            props[PHPPropertyKeys.QualifiedName] = $"{classInfo.Namespace}\\{classInfo.Name}";
            props[PHPPropertyKeys.Namespace] = classInfo.Namespace;
        }
        if (classInfo.IsAbstract) props[PHPPropertyKeys.IsAbstract] = true;
        if (classInfo.IsFinal) props[PHPPropertyKeys.IsFinal] = true;
        if (!string.IsNullOrEmpty(classInfo.Extends))
            props[PHPPropertyKeys.Extends] = classInfo.Extends;
        if (classInfo.Implements.Count > 0)
            props[PHPPropertyKeys.Interfaces] = new JsonArray(classInfo.Implements.Select(i => JsonValue.Create(i)).ToArray());

        return new Node
        {
            Id = Guid.NewGuid(),
            Kind = PHPNodeKinds.Type,
            SpanId = spanId,
            Uri = RepoUri.FromSymbol(documentUri.Container, classInfo.Name, startLine, endLine),
            ArtifactId = artifactId,
            Props = props,
            Headline = BuildDeclHeadline(classInfo),
            Structure = BuildDeclStructure(classInfo),
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static Node CreateInterfaceNode(PhpInterfaceInfo ifaceInfo, Guid artifactId, RepoUri documentUri, int? startLine, int? endLine, Guid spanId, DateTimeOffset now)
    {
        var props = new JsonObject
        {
            [PHPPropertyKeys.Name] = ifaceInfo.Name,
            [PHPPropertyKeys.Kind] = "interface"
        };

        if (!string.IsNullOrEmpty(ifaceInfo.Namespace))
        {
            props[PHPPropertyKeys.QualifiedName] = $"{ifaceInfo.Namespace}\\{ifaceInfo.Name}";
            props[PHPPropertyKeys.Namespace] = ifaceInfo.Namespace;
        }
        if (ifaceInfo.Extends.Count > 0)
            props[PHPPropertyKeys.Extends] = new JsonArray(ifaceInfo.Extends.Select(e => JsonValue.Create(e)).ToArray());

        return new Node
        {
            Id = Guid.NewGuid(),
            Kind = PHPNodeKinds.Type,
            SpanId = spanId,
            Uri = RepoUri.FromSymbol(documentUri.Container, ifaceInfo.Name, startLine, endLine),
            ArtifactId = artifactId,
            Props = props,
            Headline = BuildDeclHeadline(ifaceInfo),
            Structure = BuildDeclStructure(ifaceInfo),
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static Node CreateTraitNode(PhpTraitInfo traitInfo, Guid artifactId, RepoUri documentUri, int? startLine, int? endLine, Guid spanId, DateTimeOffset now)
    {
        var props = new JsonObject
        {
            [PHPPropertyKeys.Name] = traitInfo.Name,
            [PHPPropertyKeys.Kind] = "trait"
        };

        if (!string.IsNullOrEmpty(traitInfo.Namespace))
        {
            props[PHPPropertyKeys.QualifiedName] = $"{traitInfo.Namespace}\\{traitInfo.Name}";
            props[PHPPropertyKeys.Namespace] = traitInfo.Namespace;
        }

        return new Node
        {
            Id = Guid.NewGuid(),
            Kind = PHPNodeKinds.Type,
            SpanId = spanId,
            Uri = RepoUri.FromSymbol(documentUri.Container, traitInfo.Name, startLine, endLine),
            ArtifactId = artifactId,
            Props = props,
            Headline = BuildDeclHeadline(traitInfo),
            Structure = BuildDeclStructure(traitInfo),
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static Node CreateEnumNode(PhpEnumInfo enumInfo, Guid artifactId, RepoUri documentUri, int? startLine, int? endLine, Guid spanId, DateTimeOffset now)
    {
        var props = new JsonObject
        {
            [PHPPropertyKeys.Name] = enumInfo.Name,
            [PHPPropertyKeys.Kind] = "enum"
        };

        if (!string.IsNullOrEmpty(enumInfo.Namespace))
        {
            props[PHPPropertyKeys.QualifiedName] = $"{enumInfo.Namespace}\\{enumInfo.Name}";
            props[PHPPropertyKeys.Namespace] = enumInfo.Namespace;
        }
        if (!string.IsNullOrEmpty(enumInfo.BackedType))
            props[PHPPropertyKeys.BackedType] = enumInfo.BackedType;

        return new Node
        {
            Id = Guid.NewGuid(),
            Kind = PHPNodeKinds.Type,
            SpanId = spanId,
            Uri = RepoUri.FromSymbol(documentUri.Container, enumInfo.Name, startLine, endLine),
            ArtifactId = artifactId,
            Props = props,
            Headline = BuildDeclHeadline(enumInfo),
            Structure = BuildDeclStructure(enumInfo),
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static Node CreateEnumCaseNode(PhpEnumCaseInfo caseInfo, Guid artifactId, RepoUri documentUri, string parentName, int? startLine, int? endLine, Guid spanId, DateTimeOffset now)
    {
        var props = new JsonObject
        {
            [PHPPropertyKeys.Name] = caseInfo.Name
        };

        return new Node
        {
            Id = Guid.NewGuid(),
            Kind = PHPNodeKinds.EnumCase,
            SpanId = spanId,
            Uri = RepoUri.FromSymbol(documentUri.Container, $"{parentName}.{caseInfo.Name}", startLine, endLine),
            ArtifactId = artifactId,
            Props = props,
            Headline = BuildDeclHeadline(caseInfo),
            Structure = BuildDeclHeadline(caseInfo),
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static Node CreateFunctionNode(PhpFunctionInfo funcInfo, Guid artifactId, RepoUri documentUri, int? startLine, int? endLine, Guid spanId, DateTimeOffset now)
    {
        var props = new JsonObject
        {
            [PHPPropertyKeys.Name] = funcInfo.Name,
            [PHPPropertyKeys.Kind] = "function"
        };

        if (!string.IsNullOrEmpty(funcInfo.ReturnType))
            props[PHPPropertyKeys.ReturnType] = funcInfo.ReturnType;
        if (funcInfo.Parameters.Count > 0)
            props[PHPPropertyKeys.Parameters] = new JsonArray(funcInfo.Parameters.Select(p => JsonValue.Create(p)).ToArray());

        return new Node
        {
            Id = Guid.NewGuid(),
            Kind = PHPNodeKinds.Function,
            SpanId = spanId,
            Uri = RepoUri.FromSymbol(documentUri.Container, funcInfo.Name, startLine, endLine),
            ArtifactId = artifactId,
            Props = props,
            Headline = BuildDeclHeadline(funcInfo),
            Structure = BuildDeclHeadline(funcInfo),
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static Node CreateMethodNode(PhpMethodInfo method, Guid artifactId, RepoUri documentUri, string parentName, int? startLine, int? endLine, Guid spanId, DateTimeOffset now)
    {
        var props = new JsonObject
        {
            [PHPPropertyKeys.Name] = method.Name,
            [PHPPropertyKeys.Kind] = "method",
            [PHPPropertyKeys.DeclaringType] = parentName
        };

        if (!string.IsNullOrEmpty(method.Accessibility))
            props[PHPPropertyKeys.Accessibility] = method.Accessibility;
        if (method.IsStatic) props[PHPPropertyKeys.IsStatic] = true;
        if (method.IsAbstract) props[PHPPropertyKeys.IsAbstract] = true;
        if (!string.IsNullOrEmpty(method.ReturnType))
            props[PHPPropertyKeys.ReturnType] = method.ReturnType;
        if (method.Parameters.Count > 0)
            props[PHPPropertyKeys.Parameters] = new JsonArray(method.Parameters.Select(p => JsonValue.Create(p)).ToArray());

        return new Node
        {
            Id = Guid.NewGuid(),
            Kind = PHPNodeKinds.Member,
            SpanId = spanId,
            Uri = RepoUri.FromSymbol(documentUri.Container, $"{parentName}.{method.Name}", startLine, endLine),
            ArtifactId = artifactId,
            Props = props,
            Headline = BuildDeclHeadline(method),
            Structure = FormatCompactMethod(method),
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static Node CreatePropertyNode(PhpPropertyInfo prop, Guid artifactId, RepoUri documentUri, string parentName, int? startLine, int? endLine, Guid spanId, DateTimeOffset now)
    {
        var props = new JsonObject
        {
            [PHPPropertyKeys.Name] = prop.Name
        };

        if (!string.IsNullOrEmpty(prop.Accessibility))
            props[PHPPropertyKeys.Accessibility] = prop.Accessibility;
        if (prop.IsStatic) props[PHPPropertyKeys.IsStatic] = true;
        if (!string.IsNullOrEmpty(prop.Type))
            props[PHPPropertyKeys.Type] = prop.Type;
        if (prop.HasDefault)
            props[PHPPropertyKeys.HasDefault] = true;

        return new Node
        {
            Id = Guid.NewGuid(),
            Kind = PHPNodeKinds.Property,
            SpanId = spanId,
            Uri = RepoUri.FromSymbol(documentUri.Container, $"{parentName}.{prop.Name}", startLine, endLine),
            ArtifactId = artifactId,
            Props = props,
            Headline = BuildDeclHeadline(prop),
            Structure = FormatCompactProperty(prop),
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static Node CreateConstantNode(PhpConstantInfo constant, Guid artifactId, RepoUri documentUri, string parentName, int? startLine, int? endLine, Guid spanId, DateTimeOffset now)
    {
        var props = new JsonObject
        {
            [PHPPropertyKeys.Name] = constant.Name
        };

        if (!string.IsNullOrEmpty(constant.Accessibility))
            props[PHPPropertyKeys.Accessibility] = constant.Accessibility;

        return new Node
        {
            Id = Guid.NewGuid(),
            Kind = PHPNodeKinds.Constant,
            SpanId = spanId,
            Uri = RepoUri.FromSymbol(documentUri.Container, $"{parentName}.{constant.Name}", startLine, endLine),
            ArtifactId = artifactId,
            Props = props,
            Headline = BuildDeclHeadline(constant),
            Structure = FormatCompactConstant(constant),
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    #endregion

    #region Formatting Helpers

    private static string FormatMethodStructureLine(PhpMethodInfo method)
    {
        var visibility = AccessibilitySymbol(method.Accessibility);
        var staticModifier = method.IsStatic ? "static " : string.Empty;
        var abstractModifier = method.IsAbstract ? "abstract " : string.Empty;
        var returnTypePart = string.IsNullOrWhiteSpace(method.ReturnType) ? string.Empty : $"{method.ReturnType} ";
        var parameters = FormatParameters(method.Parameters, includeNames: true);
        return $"{visibility}{staticModifier}{abstractModifier}{returnTypePart}{method.Name}({parameters})    #symbol={NormalizeSymbolName(method.Name)}";
    }

    private static string FormatCompactMethod(PhpMethodInfo method)
    {
        var visibility = AccessibilitySymbol(method.Accessibility);
        var staticModifier = method.IsStatic ? "static " : string.Empty;
        var abstractModifier = method.IsAbstract ? "abstract " : string.Empty;
        var returnTypePart = string.IsNullOrWhiteSpace(method.ReturnType) ? string.Empty : $"{method.ReturnType} ";
        var parameterTypes = FormatParameters(method.Parameters, includeNames: false);
        return $"{visibility}{staticModifier}{abstractModifier}{returnTypePart}{method.Name}({parameterTypes})";
    }

    private static string FormatCompactProperty(PhpPropertyInfo property)
    {
        var visibility = AccessibilitySymbol(property.Accessibility);
        var staticModifier = property.IsStatic ? "static " : string.Empty;
        var typePart = string.IsNullOrWhiteSpace(property.Type) ? string.Empty : $"{property.Type} ";
        return $"{visibility}{staticModifier}{typePart}{property.Name}";
    }

    private static string FormatCompactConstant(PhpConstantInfo constant)
    {
        var declaration = BuildDeclHeadline(constant);
        var constIndex = declaration.IndexOf("const ", StringComparison.Ordinal);
        var constPart = constIndex >= 0 ? declaration[constIndex..] : $"const {constant.Name}";
        return $"{AccessibilitySymbol(constant.Accessibility)}{constPart}";
    }

    private static string FormatConstantStructureLine(PhpConstantInfo constant)
        => $"{FormatCompactConstant(constant)}    #symbol={NormalizeSymbolName(constant.Name)}";

    private static string FormatParameters(IReadOnlyList<string> parameters, bool includeNames)
    {
        if (parameters.Count == 0)
            return string.Empty;

        var formatted = includeNames
            ? parameters.Select(p => p.Trim()).Where(p => p.Length > 0)
            : parameters.Select(ExtractParameterType);
        return string.Join(", ", formatted);
    }

    private static string ExtractParameterType(string parameter)
    {
        if (string.IsNullOrWhiteSpace(parameter))
            return "mixed";

        var normalized = parameter;
        var equalsIndex = normalized.IndexOf("=", StringComparison.Ordinal);
        if (equalsIndex >= 0)
            normalized = normalized[..equalsIndex];
        normalized = normalized.Trim();

        if (normalized.StartsWith("...", StringComparison.Ordinal))
            normalized = normalized[3..].Trim();

        var parts = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "mixed";

        var variableIndex = Array.FindIndex(parts, part => part.IndexOf("$", StringComparison.Ordinal) >= 0);
        if (variableIndex < 0) return parts[0];
        if (variableIndex == 0) return "mixed";

        var typeParts = parts
            .Take(variableIndex)
            .Where(part => !string.Equals(part, "&", StringComparison.Ordinal))
            .ToArray();
        return typeParts.Length == 0 ? "mixed" : string.Join(" ", typeParts);
    }

    private static string? FormatNameList(IEnumerable<string> names, int maxNames)
    {
        var uniqueNames = names
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (uniqueNames.Count == 0) return null;
        if (uniqueNames.Count <= maxNames)
            return string.Join(", ", uniqueNames);
        return $"{string.Join(", ", uniqueNames.Take(maxNames))}, ...";
    }

    private static string NormalizeSymbolName(string name)
        => name.TrimStart('$');

    private static char AccessibilitySymbol(string? accessibility) => accessibility?.ToUpperInvariant() switch
    {
        "PUBLIC" => '+',
        "PROTECTED" => '#',
        "PRIVATE" => '-',
        _ => '+'
    };

    private static string FormatTokenCount(int tokens)
        => $"~{tokens / 1000.0:0.0}k tok";

    #endregion

    #region Span and Edge Builders

    private static Span CreateSpan(PhpByteRange byteRange, Guid docId, Guid spanId, DocumentModel document)
    {
        var start = Math.Clamp(byteRange.StartByte, 0, document.Text.Length);
        var end = Math.Clamp(byteRange.EndByte, start, document.Text.Length);
        var mapped = document.LineMap.GetSpan(start, end);

        return new Span
        {
            Id = spanId,
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

    #endregion

    #region Utilities

    private static string ReadEmbeddedResource(string resourceName)
    {
        using var stream = typeof(PHPLoader).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded SQL resource {resourceName} was not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

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

    [LoggerMessage(LogLevel.Warning, "Failed to build PHP X-ray summaries")]
    static partial void LogXrayBuildError(ILogger<PHPLoader> logger, Exception ex);

    #endregion
}
