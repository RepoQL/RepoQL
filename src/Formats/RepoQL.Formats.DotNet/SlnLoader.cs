using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using RepoQL.Contracts;
using RepoQL.Contracts.Models;
using RepoQL.Templating;

namespace RepoQL.Formats.DotNet;

/// <summary>
/// Loader + materializer for Visual Studio solution files (*.sln).
/// Produces a document node with child items for solution folders and project references.
/// </summary>
public sealed class SlnLoader(ITemplateRenderer? renderer = null) : IFormatLoader, IFormatMaterializer
{
    internal const string StateKey = "sln.state";

    private static readonly SemanticMediaType SlnType = SemanticMediaType
        .Create("text", "plain")
        .WithKind("dotnet.sln");

    private readonly ITemplateRenderer _renderer = renderer ?? new LiquidTemplateRenderer(
        assembly: typeof(SlnLoader).Assembly,
        resourceRoot: "RepoQL.Formats.DotNet.Templates");

    // Project("{GUID}") = "Name", "Path", "{GUID}"
    private static readonly Regex ProjectRegex = new(
        @"Project\(""{(?<TypeGuid>[^}]+)}""\)\s*=\s*""(?<Name>[^""]+)""\s*,\s*""(?<Path>[^""]+)""\s*,\s*""{(?<ProjectGuid>[^}]+)}""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // {GUID} = {GUID} in NestedProjects section
    private static readonly Regex NestedRegex = new(
        @"{(?<ChildGuid>[^}]+)}\s*=\s*{(?<ParentGuid>[^}]+)}",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Solution folder type GUID
    private const string SolutionFolderTypeGuid = "2150E333-8FDC-42A3-9474-1A3956D46DE8";

    /// <inheritdoc />
    public bool Supports(SemanticMediaType mediaType)
    {
        ArgumentNullException.ThrowIfNull(mediaType);
        if (string.Equals(mediaType.Kind, SlnType.Kind, StringComparison.OrdinalIgnoreCase))
            return true;
        return string.Equals(mediaType.Type, SlnType.Type, StringComparison.OrdinalIgnoreCase)
               && string.Equals(mediaType.Subtype, SlnType.Subtype, StringComparison.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public Task<bool> CanLoadAsync(DiscoveredArtifact artifact, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        var name = artifact.File.Name.ToLowerInvariant();
        if (name.EndsWith(".sln"))
        {
            artifact.MediaType = SlnType;
            return Task.FromResult(true);
        }
        return Task.FromResult(artifact.MediaType is not null && string.Equals(artifact.MediaType.Kind, "dotnet.sln", StringComparison.OrdinalIgnoreCase));
    }

    /// <inheritdoc />
    public async Task<DocumentModel> LoadAsync(DiscoveredArtifact artifact, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        if (artifact.RepoUri is null) throw new InvalidOperationException("RepoUri required for sln loader.");

        string text;
        await using (var fs = artifact.File.CreateReadStream())
        using (var sr = new StreamReader(fs))
        {
            text = await sr.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }

        var digest = "xxh64:" + Convert.ToHexString(artifact.Hash ?? []).ToLowerInvariant();

        // Parse solution file
        var formatVersion = string.Empty;
        var vsVersion = string.Empty;
        var projects = new List<SlnProject>();
        var folders = new List<SlnFolder>();
        var configs = new List<string>();
        var nestedMappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // child GUID -> parent GUID

        try
        {
            var lines = text.Split('\n');
            int lineNumber = 0;

            foreach (var rawLine in lines)
            {
                lineNumber++;
                var line = rawLine.Trim();

                // Format version
                if (line.StartsWith("Microsoft Visual Studio Solution File, Format Version", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = line.Split("Version");
                    if (parts.Length > 1)
                        formatVersion = parts[1].Trim();
                }
                // Visual Studio version
                else if (line.StartsWith("VisualStudioVersion", StringComparison.OrdinalIgnoreCase))
                {
                    var eq = line.IndexOf('=');
                    if (eq > 0)
                        vsVersion = line[(eq + 1)..].Trim();
                }
                // Project entries
                else if (line.StartsWith("Project(", StringComparison.OrdinalIgnoreCase))
                {
                    var match = ProjectRegex.Match(line);
                    if (match.Success)
                    {
                        var typeGuid = match.Groups["TypeGuid"].Value;
                        var name = match.Groups["Name"].Value;
                        var path = match.Groups["Path"].Value;
                        var projectGuid = match.Groups["ProjectGuid"].Value;

                        if (string.Equals(typeGuid, SolutionFolderTypeGuid, StringComparison.OrdinalIgnoreCase))
                        {
                            folders.Add(new SlnFolder(name, projectGuid, lineNumber));
                        }
                        else
                        {
                            projects.Add(new SlnProject(name, path, projectGuid, typeGuid, lineNumber));
                        }
                    }
                }
                // Configuration platforms
                else if (line.Contains("=") && (line.Contains("Debug|") || line.Contains("Release|")) && !line.Contains("ActiveCfg") && !line.Contains("Build.0"))
                {
                    var eq = line.IndexOf('=');
                    if (eq > 0)
                    {
                        var config = line[..eq].Trim();
                        if (!configs.Contains(config))
                            configs.Add(config);
                    }
                }
                // Nested projects
                else if (line.Contains("GlobalSection(NestedProjects)", StringComparison.OrdinalIgnoreCase))
                {
                    // Parse nested mappings in the following lines
                    for (int i = lineNumber; i < lines.Length; i++)
                    {
                        var nestedLine = lines[i].Trim();
                        if (nestedLine.StartsWith("EndGlobalSection", StringComparison.OrdinalIgnoreCase))
                            break;

                        var nestedMatch = NestedRegex.Match(nestedLine);
                        if (nestedMatch.Success)
                        {
                            var childGuid = nestedMatch.Groups["ChildGuid"].Value;
                            var parentGuid = nestedMatch.Groups["ParentGuid"].Value;
                            nestedMappings[childGuid] = parentGuid;
                        }
                    }
                }
            }
        }
        catch
        {
            // tolerate broken solution files; leave lists empty
        }

        var state = new SlnState
        {
            Digest = digest,
            Size = artifact.File.Length,
            MediaType = artifact.MediaType ?? SlnType,
            StoreUri = artifact.RepoUri.ToString(),
            FormatVersion = formatVersion,
            VsVersion = vsVersion,
            Projects = projects,
            Folders = folders,
            Configurations = configs,
            NestedMappings = nestedMappings
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
        var state = document.GetMetadataOrDefault<SlnState>(StateKey)
                    ?? throw new InvalidOperationException("sln missing state");

        // Build x-ray strings
        var fileName = GetFileName(document.Uri);
        var projectCount = state.Projects.Count;
        var folderCount = state.Folders.Count;
        var configCount = state.Configurations.Count;

        // Build display strings
        var configsDisplay = BuildConfigsDisplay(state.Configurations, limit: 6);
        var projectsDisplay = BuildProjectsDisplay(state.Projects, state.NestedMappings, state.Folders, limit: 10);
        var foldersDisplay = BuildFoldersDisplay(state.Folders, state.Projects, state.NestedMappings);

        var projectsModel = state.Projects.Select(p => new Dictionary<string, object?>
        {
            ["name"] = p.Name,
            ["path"] = p.Path,
            ["guid"] = p.Guid
        }).ToList();

        var foldersModel = state.Folders.Select(f => new Dictionary<string, object?>
        {
            ["name"] = f.Name,
            ["guid"] = f.Guid
        }).ToList();

        var model = new Dictionary<string, object?>
        {
            ["file_name"] = fileName,
            ["size_bytes"] = state.Size,
            ["format_version"] = string.IsNullOrEmpty(state.FormatVersion) ? "?" : state.FormatVersion,
            ["vs_version"] = string.IsNullOrEmpty(state.VsVersion) ? "?" : state.VsVersion,
            ["project_count"] = projectCount,
            ["folder_count"] = folderCount,
            ["config_count"] = configCount,
            ["configs"] = state.Configurations,
            ["configs_text"] = configsDisplay,
            ["projects"] = projectsModel,
            ["folders"] = foldersModel,
            ["projects_display"] = projectsDisplay,
            ["folders_display"] = foldersDisplay
        };

        var headline = _renderer.RenderAsync("xray/headline-sln", model).GetAwaiter().GetResult();
        var summary = _renderer.RenderAsync("xray/summary-sln", model).GetAwaiter().GetResult();
        var structure = _renderer.RenderAsync("xray/structure-sln", model).GetAwaiter().GetResult();

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
                ["format_version"] = state.FormatVersion,
                ["vs_version"] = state.VsVersion,
                ["project_count"] = projectCount,
                ["folder_count"] = folderCount,
                ["config_count"] = configCount
            },
            CreatedAt = now,
            UpdatedAt = now
        };

        var nodes = new List<Node> { docNode };
        var edges = new List<Edge>();
        var spans = new List<Span>();

        var ordinal = 0;

        // Create folder nodes
        foreach (var folder in state.Folders)
        {
            Span? span = null;
            if (folder.Line > 0)
            {
                var s = new Span
                {
                    Id = Guid.NewGuid(),
                    DocumentId = docNode.Id,
                    StartLine = folder.Line,
                    EndLine = folder.Line,
                };
                span = s;
                spans.Add(s);
            }

            var node = new Node
            {
                Id = Guid.NewGuid(),
                Kind = "dotnet.solution_folder",
                SpanId = span?.Id,
                Props = new JsonObject
                {
                    ["name"] = folder.Name,
                    ["guid"] = folder.Guid
                },
                CreatedAt = now,
                UpdatedAt = now
            };
            nodes.Add(node);
            edges.Add(HasPart(docNode.Id, node.Id, docNode.Id, ordinal++, now));
        }

        // Create project nodes
        foreach (var project in state.Projects)
        {
            Span? span = null;
            if (project.Line > 0)
            {
                var s = new Span
                {
                    Id = Guid.NewGuid(),
                    DocumentId = docNode.Id,
                    StartLine = project.Line,
                    EndLine = project.Line,
                };
                span = s;
                spans.Add(s);
            }

            var node = new Node
            {
                Id = Guid.NewGuid(),
                Kind = "dotnet.solution_project",
                SpanId = span?.Id,
                Props = new JsonObject
                {
                    ["name"] = project.Name,
                    ["path"] = project.Path,
                    ["guid"] = project.Guid,
                    ["type_guid"] = project.TypeGuid
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

    private static string BuildConfigsDisplay(IReadOnlyList<string> configs, int limit)
    {
        if (configs.Count == 0) return string.Empty;
        var list = new List<string>();
        for (var i = 0; i < configs.Count && i < limit; i++)
        {
            list.Add(configs[i]);
        }
        var text = string.Join(", ", list);
        if (configs.Count > limit)
            text += ", …";
        return text;
    }

    private static string BuildProjectsDisplay(IReadOnlyList<SlnProject> projects,
        Dictionary<string, string> nestedMappings,
        IReadOnlyList<SlnFolder> folders,
        int limit)
    {
        if (projects.Count == 0) return string.Empty;
        var list = new List<string>();
        for (var i = 0; i < projects.Count && i < limit; i++)
        {
            var proj = projects[i];
            var name = proj.Name;

            // Add folder context if nested
            if (nestedMappings.TryGetValue(proj.Guid, out var parentGuid))
            {
                var folder = folders.FirstOrDefault(f => string.Equals(f.Guid, parentGuid, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrEmpty(folder.Name))
                    name = $"{folder.Name}/{name}";
            }

            list.Add(name);
        }
        var text = string.Join(", ", list);
        if (projects.Count > limit)
            text += ", …";
        return text;
    }

    private static List<object> BuildFoldersDisplay(IReadOnlyList<SlnFolder> folders,
        IReadOnlyList<SlnProject> projects,
        Dictionary<string, string> nestedMappings)
    {
        var result = new List<object>();
        foreach (var folder in folders)
        {
            var projectsInFolder = projects
                .Where(p => nestedMappings.TryGetValue(p.Guid, out var parent) &&
                           string.Equals(parent, folder.Guid, StringComparison.OrdinalIgnoreCase))
                .Select(p => p.Name)
                .ToList();

            result.Add(new
            {
                name = folder.Name,
                project_count = projectsInFolder.Count,
                projects = projectsInFolder
            });
        }
        return result;
    }
}

internal sealed class SlnState
{
    public required string Digest { get; init; }
    public required long Size { get; init; }
    public required SemanticMediaType MediaType { get; init; }
    public required string StoreUri { get; init; }

    public string FormatVersion { get; init; } = string.Empty;
    public string VsVersion { get; init; } = string.Empty;
    public List<SlnProject> Projects { get; init; } = [];
    public List<SlnFolder> Folders { get; init; } = [];
    public List<string> Configurations { get; init; } = [];
    public Dictionary<string, string> NestedMappings { get; init; } = new();
}

internal readonly record struct SlnProject(string Name, string Path, string Guid, string TypeGuid, int Line);
internal readonly record struct SlnFolder(string Name, string Guid, int Line);
