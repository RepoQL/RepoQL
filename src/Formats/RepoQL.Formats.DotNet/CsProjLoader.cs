using System.Text;
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
public sealed class CsProjLoader(ITemplateRenderer? renderer) : IFormatLoader, IFormatMaterializer
{
    internal const string StateKey = "csproj.state";

    private static readonly SemanticMediaType CsProjType = SemanticMediaType
        .Create("text", "xml")
        .WithKind("dotnet.csproj");

    private readonly ITemplateRenderer? _renderer = renderer ?? new LiquidTemplateRenderer(
        assembly: typeof(CsProjLoader).Assembly,
        resourceRoot: "RepoQL.Formats.DotNet.Templates");

    public CsProjLoader() : this(null) { }

    public bool Supports(SemanticMediaType mediaType)
    {
        ArgumentNullException.ThrowIfNull(mediaType);
        if (string.Equals(mediaType.Kind, CsProjType.Kind, StringComparison.OrdinalIgnoreCase))
            return true;
        return string.Equals(mediaType.Type, CsProjType.Type, StringComparison.OrdinalIgnoreCase)
               && string.Equals(mediaType.Subtype, CsProjType.Subtype, StringComparison.OrdinalIgnoreCase);
    }

    public Task<bool> CanLoadAsync(DiscoveredArtifact artifact, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        var name = artifact.File.Name.ToLowerInvariant();
        if (name.EndsWith(".csproj"))
        {
            artifact.MediaType = CsProjType;
            return Task.FromResult(true);
        }

        if (artifact.MediaType is not null && string.Equals(artifact.MediaType.Kind, "dotnet.csproj", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(true);

        return Task.FromResult(false);
    }

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

        var digest = "xxh64:" + Convert.ToHexString(artifact.Hash ?? Array.Empty<byte>()).ToLowerInvariant();

        // Parse minimal project facts via XDocument (tolerant)
        var sdk = string.Empty;
        var tfms = new List<string>();
        var props = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var packages = new List<CsPackage>();
        var projRefs = new List<CsProjectRef>();
        string? outputType = null;
        bool? pack = null;

        try
        {
            var doc = XDocument.Parse(text, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
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
                        var (line, col) = FindApproxLine(text, pr.ToString());
                        packages.Add(new CsPackage(include, version, line));
                    }

                    foreach (var rr in ig.Elements().Where(e => e.Name.LocalName == "ProjectReference"))
                    {
                        var include = rr.Attribute("Include")?.Value ?? string.Empty;
                        var (line, col) = FindApproxLine(text, rr.ToString());
                        projRefs.Add(new CsProjectRef(include, line));
                    }
                }
            }
        }
        catch
        {
            // tolerate broken XML; leave lists empty
        }

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

    public Records Materialize(DocumentModel document)
    {
        var state = document.GetMetadata<CsProjState>(StateKey)
                    ?? throw new InvalidOperationException("csproj missing state");

        // Build x-ray strings (concise, deterministic)
        var fileName = GetFileName(document.Uri);
        var tfmText = state.TargetFrameworks.Count > 0 ? string.Join(';', state.TargetFrameworks) : (state.Properties.TryGetValue("TargetFramework", out var tf) ? tf : "");
        var pkgCount = state.Packages.Count;
        var prjCount = state.ProjectRefs.Count;
        var sizeHuman = FormatBytes(state.Size);

        var sdkDisplay = string.IsNullOrWhiteSpace(state.Sdk) ? string.Empty : NormalizeSdk(state.Sdk);
        var sdkPart = string.IsNullOrWhiteSpace(sdkDisplay) ? string.Empty : $" | sdk:{sdkDisplay}";
        var outPart = string.IsNullOrWhiteSpace(state.OutputType)
            ? string.Empty
            : $" | output:{state.OutputType}{(state.Pack ? "+pack" : string.Empty)}";

        string? headline = null;
        string? summary = null;
        string? structure = null;

        // Prepare template model
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
            ["packages"] = state.Packages.Select(p => new Dictionary<string, object?> { ["id"] = p.Id, ["version"] = string.IsNullOrWhiteSpace(p.Version) ? null : p.Version }).ToList(),
            ["project_refs"] = state.ProjectRefs.Select(r => new Dictionary<string, object?> { ["include"] = r.Include, ["file_name"] = FileNameOnly(r.Include) }).ToList(),
        };

        try
        {
            if (_renderer is not null)
            {
                headline = _renderer.RenderAsync("xray/headline", model).GetAwaiter().GetResult();
                summary = _renderer.RenderAsync("xray/summary", model).GetAwaiter().GetResult();
                structure = _renderer.RenderAsync("xray/structure", model).GetAwaiter().GetResult();
            }
        }
        catch { /* fall back below */ }

        // Fallbacks if templating is unavailable
        headline ??= $"{fileName} | dotnet.csproj | {sizeHuman}{sdkPart}{outPart} | tfm:{(string.IsNullOrEmpty(tfmText) ? "?" : tfmText)} | packages:{pkgCount} projrefs:{prjCount}";
        summary ??= BuildSummary(state, fileName);
        structure ??= BuildStructure(state);

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

        int ordinal = 0;

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

    private static string FormatBytes(long bytes)
    {
        const long KB = 1024;
        const long MB = KB * 1024;
        const long GB = MB * 1024;
        if (bytes >= GB) return ($"{bytes / (double)GB:0.##} GB");
        if (bytes >= MB) return ($"{bytes / (double)MB:0.##} MB");
        if (bytes >= KB) return ($"{bytes / (double)KB:0.##} KB");
        return ($"{bytes} B");
    }

    private static string? BuildSummary(CsProjState s, string fileName)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Name: {fileName}");
            if (!string.IsNullOrWhiteSpace(s.Sdk)) sb.AppendLine($"SDK: {NormalizeSdk(s.Sdk)}");
            if (!string.IsNullOrWhiteSpace(s.OutputType)) sb.AppendLine($"OutputType: {s.OutputType}{(s.Pack ? " + pack" : string.Empty)}");
            if (s.TargetFrameworks.Count > 0) sb.AppendLine($"TargetFrameworks: {string.Join(", ", s.TargetFrameworks)}");
            var pkgs = s.Packages.Take(8).Select(p => string.IsNullOrWhiteSpace(p.Version) ? p.Id : $"{p.Id} ({p.Version})");
            if (s.Packages.Count > 0) sb.AppendLine($"Packages: {string.Join(", ", pkgs)}{(s.Packages.Count > 8 ? ", …" : string.Empty)}");
            var prs = s.ProjectRefs.Take(8).Select(r => r.Include);
            if (s.ProjectRefs.Count > 0) sb.AppendLine($"ProjectRefs: {string.Join(", ", prs)}{(s.ProjectRefs.Count > 8 ? ", …" : string.Empty)}");
            return sb.ToString().TrimEnd();
        }
        catch { return null; }
    }

    private static string? BuildStructure(CsProjState s)
    {
        try
        {
            var lines = new List<string>();
            lines.Add("Project");
            if (!string.IsNullOrWhiteSpace(s.Sdk)) lines.Add($"  SDK: {NormalizeSdk(s.Sdk)}");
            if (!string.IsNullOrWhiteSpace(s.OutputType)) lines.Add($"  OutputType: {s.OutputType}");
            lines.Add($"  Pack: {(s.Pack ? "Yes" : "No")}");
            if (s.TargetFrameworks.Count > 0)
            {
                lines.Add("  TargetFrameworks:");
                foreach (var tf in s.TargetFrameworks.Take(15)) lines.Add($"    - {tf}");
                if (s.TargetFrameworks.Count > 15) lines.Add($"    - [... {s.TargetFrameworks.Count - 15} more]");
            }
            if (s.Packages.Count > 0)
            {
                lines.Add("  PackageReference:");
                foreach (var p in s.Packages.Take(20)) lines.Add($"    - {p.Id}{(string.IsNullOrWhiteSpace(p.Version) ? string.Empty : $" ({p.Version})")}");
                if (s.Packages.Count > 20) lines.Add($"    - [... {s.Packages.Count - 20} more]");
            }
            if (s.ProjectRefs.Count > 0)
            {
                lines.Add("  ProjectReference:");
                foreach (var r in s.ProjectRefs.Take(20)) lines.Add($"    - {r.Include}");
                if (s.ProjectRefs.Count > 20) lines.Add($"    - [... {s.ProjectRefs.Count - 20} more]");
            }
            return string.Join('\n', lines);
        }
        catch { return null; }
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
            int lastNl = -1;
            for (int i = 0; i < upTo.Length; i++)
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
}

internal sealed class CsProjState
{
    public required string Digest { get; init; }
    public required long Size { get; init; }
    public required SemanticMediaType MediaType { get; init; }
    public required string StoreUri { get; init; }

    public string Sdk { get; init; } = string.Empty;
    public List<string> TargetFrameworks { get; init; } = new();
    public Dictionary<string, string> Properties { get; init; } = new();
    public List<CsPackage> Packages { get; init; } = new();
    public List<CsProjectRef> ProjectRefs { get; init; } = new();
    public string? OutputType { get; init; }
    public bool Pack { get; init; }
}

internal readonly record struct CsPackage(string Id, string Version, int Line);
internal readonly record struct CsProjectRef(string Include, int Line);
