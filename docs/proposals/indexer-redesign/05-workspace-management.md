# Workspace Management

Workspace management enables cross-file semantic analysis by providing language-specific compilation contexts.

## Abstractions

### IWorkspaceManager

```csharp
namespace RepoQL.Contracts.Analysis;

/// <summary>
/// Manages language-specific workspace snapshots for cross-file analysis.
/// </summary>
public interface IWorkspaceManager
{
    /// <summary>
    /// Get or build a workspace snapshot for the specified language.
    /// Snapshot is immutable for the duration of analysis.
    /// </summary>
    WorkspaceSnapshot GetOrBuild(string language, CancellationToken ct);

    /// <summary>
    /// Mark a document as changed, invalidating cached workspace state.
    /// </summary>
    void Invalidate(string uri);
}
```

### WorkspaceSnapshot

```csharp
namespace RepoQL.Contracts.Analysis;

/// <summary>
/// Immutable snapshot of a language workspace at a point in time.
/// </summary>
public abstract class WorkspaceSnapshot : IDisposable
{
    /// <summary>All document URIs included in this snapshot</summary>
    public IReadOnlyList<string> Uris { get; init; } = Array.Empty<string>();

    /// <summary>Language identifier (e.g., "csharp", "python")</summary>
    public abstract string Language { get; }

    /// <summary>Timestamp when snapshot was created</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public abstract void Dispose();
}
```

## C# Implementation (Roslyn)

### CSharpWorkspaceSnapshot

```csharp
namespace RepoQL.Formats.DotNet.Analysis;

/// <summary>
/// Roslyn-based workspace snapshot for C# semantic analysis.
/// </summary>
public sealed class CSharpWorkspaceSnapshot : WorkspaceSnapshot
{
    private readonly Workspace _workspace;
    private readonly Solution _solution;

    public override string Language => "csharp";

    /// <summary>Roslyn solution (all projects and documents)</summary>
    public Solution Solution => _solution;

    internal CSharpWorkspaceSnapshot(Workspace workspace, Solution solution, IEnumerable<string> uris)
    {
        _workspace = workspace;
        _solution = solution;
        Uris = uris.ToList();
    }

    /// <summary>
    /// Get Roslyn document by RepoQL URI.
    /// </summary>
    public Document? GetDocumentByUri(string repoUri)
    {
        // Map RepoQL URI to file path
        var filePath = RepoUriToPath(repoUri);

        // Find document in solution
        return _solution.Projects
            .SelectMany(p => p.Documents)
            .FirstOrDefault(d => PathEquals(d.FilePath, filePath));
    }

    /// <summary>
    /// Get all references to a symbol across the solution.
    /// </summary>
    public async Task<IEnumerable<ReferencedSymbol>> FindReferencesAsync(
        ISymbol symbol,
        CancellationToken ct)
    {
        return await SymbolFinder.FindReferencesAsync(symbol, _solution, ct);
    }

    /// <summary>
    /// Get all implementations of an interface or abstract method.
    /// </summary>
    public async Task<IEnumerable<ISymbol>> FindImplementationsAsync(
        ISymbol symbol,
        CancellationToken ct)
    {
        return await SymbolFinder.FindImplementationsAsync(symbol, _solution, ct);
    }

    public override void Dispose()
    {
        _workspace?.Dispose();
    }

    private static string RepoUriToPath(string repoUri)
    {
        // file:///src/Foo.cs -> /src/Foo.cs (or C:\src\Foo.cs on Windows)
        var uri = new Uri(repoUri);
        return uri.LocalPath;
    }

    private static bool PathEquals(string? path1, string? path2)
    {
        if (path1 == null || path2 == null) return false;
        return string.Equals(
            Path.GetFullPath(path1),
            Path.GetFullPath(path2),
            StringComparison.OrdinalIgnoreCase);
    }
}
```

### SimpleWorkspaceManager

First implementation: rebuild workspace on every batch (no caching).

```csharp
namespace RepoQL.Formats.DotNet.Analysis;

public sealed class SimpleWorkspaceManager : IWorkspaceManager
{
    private readonly IGraphStore _store;
    private readonly ILogger<SimpleWorkspaceManager> _logger;

    public SimpleWorkspaceManager(
        IGraphStore store,
        ILogger<SimpleWorkspaceManager>? logger = null)
    {
        _store = store;
        _logger = logger ?? NullLogger<SimpleWorkspaceManager>.Instance;
    }

    public WorkspaceSnapshot GetOrBuild(string language, CancellationToken ct)
    {
        if (language != "csharp")
        {
            throw new NotSupportedException($"Language '{language}' not supported");
        }

        return BuildCSharpWorkspace(ct);
    }

    public void Invalidate(string uri)
    {
        // No-op for simple implementation (no caching)
        _logger.LogDebug("Invalidate called for {Uri} (no-op)", uri);
    }

    private CSharpWorkspaceSnapshot BuildCSharpWorkspace(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        // Find all .sln files
        var slnFiles = _store.Query<string>(@"
            SELECT n.uri
            FROM node n
            JOIN artifact a ON a.id = n.artifact_id
            WHERE n.kind = 'document'
              AND a.media_type LIKE '%sln%'");

        Workspace workspace;
        Solution solution;

        if (slnFiles.Any())
        {
            // Load from .sln
            var slnPath = RepoUriToPath(slnFiles.First());
            workspace = MSBuildWorkspace.Create();
            solution = workspace.CurrentSolution;

            _logger.LogInformation("Loading solution from {Path}", slnPath);
            solution = ((MSBuildWorkspace)workspace).OpenSolutionAsync(slnPath, ct).Result;
        }
        else
        {
            // Fallback: adhoc workspace with all .cs files
            _logger.LogInformation("No .sln found, creating adhoc workspace");
            workspace = new AdhocWorkspace();
            solution = BuildAdhocSolution(workspace, ct);
        }

        // Collect all document URIs
        var uris = solution.Projects
            .SelectMany(p => p.Documents)
            .Select(d => PathToRepoUri(d.FilePath))
            .Where(u => u != null)
            .ToList();

        sw.Stop();
        _logger.LogInformation(
            "Built C# workspace: {Projects} projects, {Documents} documents in {Duration}ms",
            solution.Projects.Count(),
            uris.Count,
            sw.ElapsedMilliseconds);

        return new CSharpWorkspaceSnapshot(workspace, solution, uris!);
    }

    private Solution BuildAdhocSolution(Workspace workspace, CancellationToken ct)
    {
        var solution = workspace.CurrentSolution;

        // Find all .csproj files
        var csprojFiles = _store.Query<(string Uri, string Content)>(@"
            SELECT n.uri, a.text_content
            FROM node n
            JOIN artifact a ON a.id = n.artifact_id
            WHERE n.kind = 'document'
              AND a.media_type LIKE '%csproj%'");

        if (csprojFiles.Any())
        {
            // Build projects from .csproj
            foreach (var (uri, content) in csprojFiles)
            {
                var projectPath = RepoUriToPath(uri);
                var projectName = Path.GetFileNameWithoutExtension(projectPath);

                var projectInfo = ProjectInfo.Create(
                    ProjectId.CreateNewId(),
                    VersionStamp.Default,
                    projectName,
                    projectName,
                    LanguageNames.CSharp,
                    filePath: projectPath);

                solution = solution.AddProject(projectInfo);

                // Add documents from project
                solution = AddDocumentsFromProject(solution, projectInfo.Id, projectPath, ct);
            }
        }
        else
        {
            // Fallback: single project with all .cs files
            _logger.LogWarning("No .csproj found, creating single adhoc project");

            var projectInfo = ProjectInfo.Create(
                ProjectId.CreateNewId(),
                VersionStamp.Default,
                "AdhocProject",
                "AdhocProject",
                LanguageNames.CSharp);

            solution = solution.AddProject(projectInfo);
            solution = AddAllCSharpDocuments(solution, projectInfo.Id, ct);
        }

        return solution;
    }

    private Solution AddDocumentsFromProject(
        Solution solution,
        ProjectId projectId,
        string projectPath,
        CancellationToken ct)
    {
        var projectDir = Path.GetDirectoryName(projectPath)!;

        // Find .cs files in project directory
        var csFiles = _store.Query<(string Uri, string Content)>(@"
            SELECT n.uri, a.text_content
            FROM node n
            JOIN artifact a ON a.id = n.artifact_id
            WHERE n.kind = 'document'
              AND a.media_type LIKE '%csharp%'
              AND n.uri LIKE @pattern",
            new { pattern = $"%{projectDir}%" });

        foreach (var (uri, content) in csFiles)
        {
            var filePath = RepoUriToPath(uri);
            var documentInfo = DocumentInfo.Create(
                DocumentId.CreateNewId(projectId),
                Path.GetFileName(filePath),
                loader: TextLoader.From(
                    TextAndVersion.Create(
                        SourceText.From(content),
                        VersionStamp.Default)),
                filePath: filePath);

            solution = solution.AddDocument(documentInfo);
        }

        return solution;
    }

    private Solution AddAllCSharpDocuments(
        Solution solution,
        ProjectId projectId,
        CancellationToken ct)
    {
        var csFiles = _store.Query<(string Uri, string Content)>(@"
            SELECT n.uri, a.text_content
            FROM node n
            JOIN artifact a ON a.id = n.artifact_id
            WHERE n.kind = 'document'
              AND a.media_type LIKE '%csharp%'");

        foreach (var (uri, content) in csFiles)
        {
            var filePath = RepoUriToPath(uri);
            var documentInfo = DocumentInfo.Create(
                DocumentId.CreateNewId(projectId),
                Path.GetFileName(filePath),
                loader: TextLoader.From(
                    TextAndVersion.Create(
                        SourceText.From(content),
                        VersionStamp.Default)),
                filePath: filePath);

            solution = solution.AddDocument(documentInfo);
        }

        return solution;
    }

    private static string RepoUriToPath(string repoUri)
    {
        var uri = new Uri(repoUri);
        return uri.LocalPath;
    }

    private static string? PathToRepoUri(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var uri = new Uri(path, UriKind.Absolute);
        return uri.ToString();
    }
}
```

## Caching Strategy (Future)

### CachingWorkspaceManager

Second iteration: cache workspace and incrementally update.

```csharp
public sealed class CachingWorkspaceManager : IWorkspaceManager
{
    private readonly IGraphStore _store;
    private readonly ILogger<CachingWorkspaceManager> _logger;
    private readonly Dictionary<string, (WorkspaceSnapshot Snapshot, DateTime Built)> _cache = new();
    private readonly HashSet<string> _invalidatedUris = new();
    private readonly TimeSpan _cacheExpiry = TimeSpan.FromMinutes(5);

    public WorkspaceSnapshot GetOrBuild(string language, CancellationToken ct)
    {
        if (!_cache.TryGetValue(language, out var cached) ||
            cached.Built < DateTime.UtcNow - _cacheExpiry ||
            _invalidatedUris.Any())
        {
            _logger.LogInformation(
                "Rebuilding workspace for {Language} (cache expired or invalidated)",
                language);

            // Rebuild workspace
            cached.Snapshot?.Dispose();
            var newSnapshot = BuildWorkspace(language, ct);
            _cache[language] = (newSnapshot, DateTime.UtcNow);
            _invalidatedUris.Clear();

            return newSnapshot;
        }

        _logger.LogDebug("Using cached workspace for {Language}", language);
        return cached.Snapshot;
    }

    public void Invalidate(string uri)
    {
        _invalidatedUris.Add(uri);

        // If too many invalidations, clear cache immediately
        if (_invalidatedUris.Count > 100)
        {
            _logger.LogInformation("Too many invalidations, clearing workspace cache");
            foreach (var (snapshot, _) in _cache.Values)
            {
                snapshot.Dispose();
            }
            _cache.Clear();
            _invalidatedUris.Clear();
        }
    }

    // ... BuildWorkspace implementation ...
}
```

### Incremental Updates (Future)

For Roslyn, can update solution incrementally instead of rebuilding:

```csharp
private Solution UpdateSolution(
    Solution solution,
    IEnumerable<string> changedUris,
    CancellationToken ct)
{
    foreach (var uri in changedUris)
    {
        var filePath = RepoUriToPath(uri);
        var document = solution.Projects
            .SelectMany(p => p.Documents)
            .FirstOrDefault(d => PathEquals(d.FilePath, filePath));

        if (document == null) continue;

        // Load new content
        var newContent = _store.QuerySingle<string>(
            "SELECT a.text_content FROM node n JOIN artifact a ON a.id=n.artifact_id WHERE n.uri=@uri",
            new { uri });

        // Update document
        var sourceText = SourceText.From(newContent);
        solution = document.WithText(sourceText).Project.Solution;
    }

    return solution;
}
```

## Testing

### Unit Tests

```csharp
[Test]
public void WorkspaceManager_BuildsFromSolution()
{
    // Arrange: Repository with .sln file
    var store = CreateTestStore();
    store.IndexFile("MySolution.sln", "...");
    store.IndexFile("Project1/Project1.csproj", "...");
    store.IndexFile("Project1/Foo.cs", "public class Foo { }");

    var manager = new SimpleWorkspaceManager(store);

    // Act
    var snapshot = manager.GetOrBuild("csharp", CancellationToken.None);

    // Assert
    Assert.Equal("csharp", snapshot.Language);
    Assert.NotEmpty(snapshot.Uris);
    Assert.Contains("Foo.cs", snapshot.Uris[0]);
}

[Test]
public void WorkspaceManager_FallsBackToAdhoc()
{
    // Arrange: Repository with no .sln or .csproj
    var store = CreateTestStore();
    store.IndexFile("src/Foo.cs", "public class Foo { }");
    store.IndexFile("src/Bar.cs", "public class Bar { }");

    var manager = new SimpleWorkspaceManager(store);

    // Act
    var snapshot = manager.GetOrBuild("csharp", CancellationToken.None);

    // Assert
    Assert.Equal("csharp", snapshot.Language);
    Assert.Equal(2, snapshot.Uris.Count);
}

[Test]
public async Task CSharpSnapshot_FindsReferences()
{
    // Arrange
    var store = CreateTestStore();
    store.IndexFile("Foo.cs", @"
        public class Foo {
            public void Method() { }
        }
    ");
    store.IndexFile("Bar.cs", @"
        public class Bar {
            void Test() { new Foo().Method(); }
        }
    ");

    var manager = new SimpleWorkspaceManager(store);
    var snapshot = (CSharpWorkspaceSnapshot)manager.GetOrBuild("csharp", ct);

    // Act: Find references to Foo.Method
    var fooDoc = snapshot.GetDocumentByUri("file:///Foo.cs");
    var semanticModel = await fooDoc.GetSemanticModelAsync(ct);
    var methodSymbol = /* ... find Method symbol ... */;

    var references = await snapshot.FindReferencesAsync(methodSymbol, ct);

    // Assert
    Assert.Equal(2, references.Count()); // Definition + 1 reference
    Assert.Contains(references, r => r.Locations.Any(l => l.Document.FilePath.Contains("Bar.cs")));
}
```

### Integration Tests

```csharp
[Test]
public async Task SemanticAnalysis_UsesWorkspace()
{
    // Arrange: Two files with cross-file reference
    await indexer.IndexFileAsync("IService.cs", @"
        public interface IService {
            void Execute();
        }
    ");
    await indexer.IndexFileAsync("ServiceImpl.cs", @"
        public class ServiceImpl : IService {
            public void Execute() { }
        }
    ");

    await indexer.WaitForIdleAsync(ct);

    // Act: Run semantic analysis
    await semanticBatch.RunAsync(ct);

    // Assert: Cross-file edges created
    var edges = db.Query<Edge>(@"
        SELECT * FROM edge
        WHERE type = 'IMPLEMENTS'
          AND src_uri LIKE '%ServiceImpl.cs%'
          AND dst_uri LIKE '%IService.cs%'");

    Assert.Single(edges);
}
```

## Performance Considerations

### Workspace Build Time

**Solution-based:**
- Small repos (< 10 projects): ~500ms - 1s
- Medium repos (10-50 projects): ~1s - 5s
- Large repos (> 50 projects): ~5s - 30s

**Adhoc:**
- Small repos (< 100 files): ~100ms - 500ms
- Medium repos (100-1000 files): ~500ms - 3s
- Large repos (> 1000 files): ~3s - 15s

### Memory Usage

**Roslyn workspace:**
- ~10-50 MB per project
- ~1-5 MB per document
- Large solutions can use 500MB - 2GB

**Mitigation:**
- Dispose snapshots after analysis
- Consider workspace caching with size limits
- Option to disable semantic analysis for large repos

### Optimization Opportunities

1. **Partial loading** - Only load changed projects
2. **Incremental compilation** - Reuse semantic models
3. **Parallel analysis** - Analyze multiple documents concurrently
4. **Lazy loading** - Build workspace on-demand, not eagerly

## Future: Python Support

```csharp
public class PythonWorkspaceSnapshot : WorkspaceSnapshot
{
    public override string Language => "python";

    // Use Pyright or Jedi for Python analysis
    // Similar patterns to Roslyn, but Python-specific APIs
}

public class PythonWorkspaceManager : IWorkspaceManager
{
    public WorkspaceSnapshot GetOrBuild(string language, CancellationToken ct)
    {
        // Build Python workspace
        // - Find all .py files
        // - Detect virtualenv or poetry environment
        // - Load type stubs
        // - Create analysis context
    }
}
```

## Configuration

```bash
# Workspace caching
REPOQL_WORKSPACE_CACHE_ENABLED=false      # Default: disabled (simple manager)
REPOQL_WORKSPACE_CACHE_EXPIRY_MINUTES=5

# Memory limits
REPOQL_WORKSPACE_MAX_MEMORY_MB=2048       # Max workspace size before disabling

# Fallback behavior
REPOQL_WORKSPACE_ADHOC_MAX_FILES=1000     # Max files for adhoc workspace
```
