using System.Xml;
using System.Xml.Linq;
using RepoQL.Contracts;
using RepoQL.Contracts.Models;
using RepoQL.Templating;
using System.Text.Json.Nodes;

namespace RepoQL.Formats.DotNet;

/// <summary>
/// Loader + materializer for C# project files (*.csproj).
/// Produces a document node with child items for TargetFramework(s), PackageReference(s), and ProjectReference(s).
/// </summary>
public sealed class CsProjLoader(ITemplateRenderer? renderer = null) : IFormatLoader, IFormatMaterializer
{
    internal const string StateKey = "csproj.state";

    private static readonly SemanticMediaType CsProjType = SemanticMediaType
        .Create("text", "xml")
        .WithKind("dotnet.csproj");

    private readonly ITemplateRenderer _renderer = new LiquidTemplateRenderer(
        assembly: typeof(CsProjLoader).Assembly,
        resourceRoot: "RepoQL.Formats.DotNet.Templates");

    /// <inheritdoc />
    public bool Supports(SemanticMediaType mediaType)
    {
        ArgumentNullException.ThrowIfNull(mediaType);
        if (string.Equals(mediaType.Kind, CsProjType.Kind, StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }

    /// <inheritdoc />
    public Task<bool> CanLoadAsync(DiscoveredArtifact artifact, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        var name = artifact.File.Name.ToLowerInvariant();
        if (name.EndsWith(".csproj"))
        {
            artifact.MediaType = CsProjType;
            return Task.FromResult(true);
        }
        return Task.FromResult(artifact.MediaType is not null && string.Equals(artifact.MediaType.Kind, "dotnet.csproj", StringComparison.OrdinalIgnoreCase));
    }

    /// <inheritdoc />
    public async Task<DocumentModel> LoadAsync(DiscoveredArtifact artifact, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        if (artifact.RepoUri is null) throw new InvalidOperationException("RepoUri required for csproj loader.");

        string text;
        await using (var fs = artifact.File.CreateReadStream())
        using (var sr = new StreamReader(fs))
        {
            text = await sr.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }

        var digest = "xxh64:" + Convert.ToHexString(artifact.Hash ?? []).ToLowerInvariant();

        // Parse minimal project facts via XDocument (tolerant)
        var sdk = string.Empty;
        var tfms = new List<string>();
        var props = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var packages = new List<CsPackage>();
        var projRefs = new List<CsProjectRef>();
        string? outputType = null;
        bool? pack = null;

        var parseOptions = LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo;
        var doc = TryParseProject(text, parseOptions, out var parseSource);
        if (doc is not null)
        {
            var root = doc.Root;
            if (root is not null)
            {
                sdk = root.Attribute("Sdk")?.Value ?? string.Empty;

                foreach (var pg in root.Elements().Where(e => e.Name.LocalName == "PropertyGroup"))
                {
                    foreach (var el in pg.Elements())
                    {
                        var name = el.Name.LocalName;
                        var val = el.Value?.Trim();
                        if (string.IsNullOrWhiteSpace(val)) continue;
                        props[name] = val;
                        if (string.Equals(name, "TargetFramework", StringComparison.OrdinalIgnoreCase))
                            tfms.Add(val);
                        else if (string.Equals(name, "TargetFrameworks", StringComparison.OrdinalIgnoreCase))
                            tfms.AddRange(val.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                        else if (string.Equals(name, "OutputType", StringComparison.OrdinalIgnoreCase))
                            outputType = val;
                        else if (string.Equals(name, "IsPackable", StringComparison.OrdinalIgnoreCase))
                            pack = ParseBool(val);
                        else if (string.Equals(name, "GeneratePackageOnBuild", StringComparison.OrdinalIgnoreCase) && ParseBool(val) == true)
                            pack = true;
                    }
                }

                foreach (var ig in root.Elements().Where(e => e.Name.LocalName == "ItemGroup"))
                {
                    foreach (var pr in ig.Elements().Where(e => e.Name.LocalName == "PackageReference"))
                    {
                        var include = pr.Attribute("Include")?.Value ?? string.Empty;
                        var version = pr.Attribute("Version")?.Value ?? pr.Element(pr.Name.Namespace + "Version")?.Value ?? string.Empty;
                        var (line, col) = FindApproxLine(parseSource, pr.ToString());
                        packages.Add(new CsPackage(include, version, line));
                    }

                    foreach (var rr in ig.Elements().Where(e => e.Name.LocalName == "ProjectReference"))
                    {
                        var include = rr.Attribute("Include")?.Value ?? string.Empty;
                        var (line, col) = FindApproxLine(parseSource, rr.ToString());
                        projRefs.Add(new CsProjectRef(include, line));
                    }
                }
            }
        }

        if (string.IsNullOrWhiteSpace(outputType))
            outputType = "Library";

        var state = new CsProjState
        {
            Digest = digest,
            Size = artifact.File.Length,
            MediaType = artifact.MediaType ?? CsProjType,
            StoreUri = artifact.RepoUri.ToString(),
            Sdk = sdk,
            TargetFrameworks = tfms,
            Properties = props,
            Packages = packages,
            ProjectRefs = projRefs,
            OutputType = outputType,
            Pack = pack ?? false
        };

        var metadata = new Dictionary<string, object?>
        {
            [StateKey] = state
        };

        return new DocumentModel(artifact.RepoUri, state.MediaType, text, metadata: metadata);
    }

    /// <inheritdoc />
    public Records Materialize(DocumentModel document)
    {
        var state = document.GetMetadataOrDefault<CsProjState>(StateKey)
                    ?? throw new InvalidOperationException("csproj missing state");

        // Build x-ray strings (concise, deterministic)
        var fileName = GetFileName(document.Uri);
        var tfmText = state.TargetFrameworks.Count > 0 
            ? string.Join(';', state.TargetFrameworks) 
            : state.Properties.GetValueOrDefault("TargetFramework", "");
        var pkgCount = state.Packages.Count;
        var prjCount = state.ProjectRefs.Count;

        var sdkDisplay = string.IsNullOrWhiteSpace(state.Sdk) ? string.Empty : NormalizeSdk(state.Sdk);

        // Prepare template model
        var packageDisplay = BuildPackageDisplay(state.Packages, limit: 8);
        var projectRefDisplay = BuildProjectRefDisplay(state.ProjectRefs, limit: 8);
        var packageItems = state.Packages.Select(p => new { id = p.Id, version = string.IsNullOrWhiteSpace(p.Version) ? null : p.Version }).ToList();
        var projectRefItems = state.ProjectRefs.Select(r => new { include = r.Include, file_name = FileNameOnly(r.Include) }).ToList();
        var packageLines = state.Packages.Select(p => string.IsNullOrWhiteSpace(p.Version) ? p.Id : $"{p.Id} ({p.Version})").ToList();
        var projectRefLines = state.ProjectRefs.Select(r => r.Include).ToList();

        var model = new Dictionary<string, object?>
        {
            ["file_name"] = fileName,
            ["size_bytes"] = state.Size,
            ["sdk"] = sdkDisplay,
            ["output_type"] = state.OutputType,
            ["pack"] = state.Pack,
            ["tfm_text"] = string.IsNullOrEmpty(tfmText) ? "?" : tfmText,
            ["tfms"] = state.TargetFrameworks,
            ["package_count"] = pkgCount,
            ["project_ref_count"] = prjCount,
            ["packages"] = packageItems,
            ["project_refs"] = projectRefItems,
            ["packages_text"] = packageDisplay,
            ["project_refs_text"] = projectRefDisplay,
            ["package_lines"] = packageLines,
            ["project_ref_lines"] = projectRefLines,
        };


        var headline = _renderer.RenderAsync("xray/headline", model).GetAwaiter().GetResult();
        var summary = _renderer.RenderAsync("xray/summary", model).GetAwaiter().GetResult();
        var structure = _renderer.RenderAsync("xray/structure", model).GetAwaiter().GetResult();
        

        var artifact = new Artifact
        {
            Id = Guid.NewGuid(),
            Digest = state.Digest,
            Size = state.Size,
            MediaType = state.MediaType,
            Text = document.Text,
            StoreUri = document.Uri.ToString(),
            Headline = headline,
            Summary = summary,
            Structure = structure
        };

        var now = DateTimeOffset.UtcNow;

        var docNode = new Node
        {
            Id = Guid.NewGuid(),
            Kind = "document",
            Uri = document.Uri,
            ArtifactId = artifact.Id,
            Props = new JsonObject
            {
                ["media_type"] = state.MediaType.ToString(),
                ["sdk"] = state.Sdk,
                ["tfms"] = string.Join(';', state.TargetFrameworks),
                ["output_type"] = state.OutputType,
                ["pack"] = state.Pack
            },
            CreatedAt = now,
            UpdatedAt = now
        };

        var nodes = new List<Node> { docNode };
        var edges = new List<Edge>();
        var spans = new List<Span>();

        var ordinal = 0;

        if (state.TargetFrameworks.Count > 0)
        {
            foreach (var tfm in state.TargetFrameworks)
            {
                var n = new Node
                {
                    Id = Guid.NewGuid(),
                    Kind = "dotnet.tfm",
                    Props = new JsonObject { ["tfm"] = tfm },
                    CreatedAt = now,
                    UpdatedAt = now
                };
                nodes.Add(n);
                edges.Add(HasPart(docNode.Id, n.Id, docNode.Id, ordinal++, now));
            }
        }

        foreach (var pkg in state.Packages)
        {
            Span? span = null;
            if (pkg.Line > 0)
            {
                var s = new Span
                {
                    Id = Guid.NewGuid(),
                    DocumentId = docNode.Id,
                    StartLine = pkg.Line,
                    EndLine = pkg.Line,
                };
                span = s;
                spans.Add(s);
            }
            var node = new Node
            {
                Id = Guid.NewGuid(),
                Kind = "nuget.package",
                SpanId = span?.Id,
                Props = new JsonObject
                {
                    ["id"] = pkg.Id,
                    ["version"] = string.IsNullOrWhiteSpace(pkg.Version) ? null : pkg.Version
                },
                CreatedAt = now,
                UpdatedAt = now
            };
            nodes.Add(node);
            edges.Add(HasPart(docNode.Id, node.Id, docNode.Id, ordinal++, now));
        }

        foreach (var pr in state.ProjectRefs)
        {
            Span? span = null;
            if (pr.Line > 0)
            {
                var s = new Span
                {
                    Id = Guid.NewGuid(),
                    DocumentId = docNode.Id,
                    StartLine = pr.Line,
                    EndLine = pr.Line,
                };
                span = s;
                spans.Add(s);
            }
            var node = new Node
            {
                Id = Guid.NewGuid(),
                Kind = "dotnet.project_reference",
                SpanId = span?.Id,
                Props = new JsonObject
                {
                    ["include"] = pr.Include
                },
                CreatedAt = now,
                UpdatedAt = now
            };
            nodes.Add(node);
            edges.Add(HasPart(docNode.Id, node.Id, docNode.Id, ordinal++, now));
        }

        return new Records
        {
            Artifacts = [artifact],
            Nodes = [.. nodes],
            Spans = [.. spans],
            Edges = [.. edges]
        };
    }

    private static Edge HasPart(Guid documentId, Guid childId, Guid scopeDocumentId, int ordinal, DateTimeOffset ts)
        => new()
        {
            Id = Guid.NewGuid(),
            SrcId = documentId,
            DstId = childId,
            Type = "HAS_PART",
            IsComposition = true,
            Ordinal = ordinal,
            ScopeDocumentId = scopeDocumentId,
            CreatedAt = ts
        };

    private static XDocument? TryParseProject(string text, LoadOptions options, out string parseSource)
    {
        parseSource = text;
        try
        {
            return XDocument.Parse(parseSource, options);
        }
        catch (XmlException)
        {
            var sanitized = SanitizeForXmlParsing(parseSource);
            if (!ReferenceEquals(sanitized, parseSource))
            {
                parseSource = sanitized;
                try
                {
                    return XDocument.Parse(parseSource, options);
                }
                catch (XmlException)
                {
                    parseSource = text;
                }
            }
        }

        return null;
    }

    private static string SanitizeForXmlParsing(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        var needsReplacement = text.AsSpan().IndexOf("\\\"") >= 0 || text.AsSpan().IndexOf("\\'") >= 0;
        if (!needsReplacement)
            return text;

        var sanitized = text.Replace("\\\"", "\"");
        if (sanitized.AsSpan().IndexOf("\\'") >= 0)
            sanitized = sanitized.Replace("\\'", "'");
        return sanitized;
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

    private static (int line, int col) FindApproxLine(string text, string snippet)
    {
        try
        {
            var part = snippet.Length > 64 ? snippet.Substring(0, 64) : snippet;
            var idx = text.IndexOf(part, StringComparison.Ordinal);
            if (idx < 0) return (0, 0);
            var upTo = text.AsSpan(0, idx);
            var line = 1;
            var lastNl = -1;
            for (var i = 0; i < upTo.Length; i++)
            {
                if (upTo[i] == '\n') { line++; lastNl = i; }
            }
            var col = idx - (lastNl + 1) + 1;
            return (line, col);
        }
        catch { return (0, 0); }
    }

    private static bool? ParseBool(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        return s.Trim().Equals("true", StringComparison.OrdinalIgnoreCase) || s.Trim().Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    private static string FileNameOnly(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        try
        {
            var norm = path.Replace('\\', '/');
            var i = norm.LastIndexOf('/');
            return i >= 0 ? norm[(i + 1)..] : norm;
        }
        catch { return path!; }
    }

    private static string NormalizeSdk(string? sdk)
    {
        if (string.IsNullOrWhiteSpace(sdk)) return string.Empty;
        try { return sdk.Replace(".Sdk", string.Empty, StringComparison.OrdinalIgnoreCase); }
        catch { return sdk; }
    }

    private static string BuildPackageDisplay(IReadOnlyList<CsPackage> packages, int limit)
    {
        if (packages.Count == 0) return string.Empty;
        var list = new List<string>();
        for (var i = 0; i < packages.Count && i < limit; i++)
        {
            var pkg = packages[i];
            list.Add(string.IsNullOrWhiteSpace(pkg.Version) ? pkg.Id : $"{pkg.Id} ({pkg.Version})");
        }
        var text = string.Join(", ", list);
        if (packages.Count > limit)
            text += ", …";
        return text;
    }

    private static string BuildProjectRefDisplay(IReadOnlyList<CsProjectRef> refs, int limit)
    {
        if (refs.Count == 0) return string.Empty;
        var list = new List<string>();
        for (var i = 0; i < refs.Count && i < limit; i++)
        {
            list.Add(FileNameOnly(refs[i].Include) ?? refs[i].Include);
        }
        var text = string.Join(", ", list);
        if (refs.Count > limit)
            text += ", …";
        return text;
    }
}

internal sealed class CsProjState
{
    public required string Digest { get; init; }
    public required long Size { get; init; }
    public required SemanticMediaType MediaType { get; init; }
    public required string StoreUri { get; init; }

    public string Sdk { get; init; } = string.Empty;
    public List<string> TargetFrameworks { get; init; } = [];
    public Dictionary<string, string> Properties { get; init; } = new();
    public List<CsPackage> Packages { get; init; } = [];
    public List<CsProjectRef> ProjectRefs { get; init; } = [];
    public string? OutputType { get; init; }
    public bool Pack { get; init; }
}

internal readonly record struct CsPackage(string Id, string Version, int Line);
internal readonly record struct CsProjectRef(string Include, int Line);
