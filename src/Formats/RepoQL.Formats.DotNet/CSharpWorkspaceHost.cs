using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RepoQL.Contracts;

namespace RepoQL.Formats.DotNet;

/// <summary>
/// Manages MSBuild workspaces for project-aware C# semantic analysis.
/// </summary>
/// <remarks>
/// <para>
/// This class provides caching of loaded projects and their compilations to avoid
/// repeatedly loading the same project when analyzing multiple files. Each project
/// is loaded once and reused for all files in that project.
/// </para>
/// <para>
/// The workspace host also manages source generator execution and analyzer diagnostics,
/// providing a complete semantic analysis environment for C# code.
/// </para>
/// </remarks>
public sealed class CSharpWorkspaceHost : IDisposable, IHostedService
{
    private static readonly object LocatorGate = new();
    private static bool _locatorRegistered;
    private static bool _sdkAvailable;
    private static string? _sdkUnavailableReason;
    private static readonly SemaphoreSlim ConcurrencyLimit = new(Math.Max(1, Math.Min(Environment.ProcessorCount / 2, 4)), Math.Max(1, Math.Min(Environment.ProcessorCount / 2, 4)));

    private readonly ConcurrentDictionary<string, byte> _sessionKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly IMemoryCache _sessionCache;
    private readonly Func<MSBuildWorkspace>? _workspaceFactory;
    private readonly ILogger<CSharpWorkspaceHost> _logger;
    private readonly TimeSpan _sessionSlidingExpiration;
    private readonly TimeSpan _sessionAbsoluteExpiration;
    private readonly int _sessionEntrySize;
    private readonly bool _ownsSessionCache;
    private volatile bool _disposing;

    /// <summary>
    /// Gets whether the .NET SDK is available for semantic analysis.
    /// When false, only syntactic analysis is available.
    /// </summary>
    public static bool IsSdkAvailable => _sdkAvailable;

    /// <summary>
    /// Initializes a new instance of the <see cref="CSharpWorkspaceHost"/> class with default settings.
    /// </summary>
    public CSharpWorkspaceHost()
        : this(null, null, null, null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="CSharpWorkspaceHost"/> class using a shared cache.
    /// </summary>
    public CSharpWorkspaceHost(IMemoryCache sessionCache, IConfiguration? configuration = null, ILogger<CSharpWorkspaceHost>? logger = null)
        : this(null, logger, sessionCache, configuration)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="CSharpWorkspaceHost"/> class with the specified workspace factory and logger.
    /// </summary>
    /// <param name="workspaceFactory">Optional factory for creating MSBuild workspaces.</param>
    /// <param name="logger">Optional logger for diagnostic information.</param>
    internal CSharpWorkspaceHost(
        Func<MSBuildWorkspace>? workspaceFactory,
        ILogger<CSharpWorkspaceHost>? logger = null,
        IMemoryCache? sessionCache = null,
        IConfiguration? configuration = null)
    {
        _logger = logger ?? NullLogger<CSharpWorkspaceHost>.Instance;
        EnsureLocator(_logger);
        _workspaceFactory = _sdkAvailable ? (workspaceFactory ?? (() => CreateWorkspace(_logger))) : null;
        _sessionCache = sessionCache ?? new MemoryCache(new MemoryCacheOptions { SizeLimit = 8 });
        _ownsSessionCache = sessionCache is null;
        _sessionSlidingExpiration = TimeSpan.FromSeconds(ResolveIntSetting(
            configuration,
            "RepoQL:CSharp:WorkspaceSessionSlidingSeconds",
            "REPOQL_CSHARP_WORKSPACE_SESSION_SLIDING_SECONDS",
            60,
            minimum: 1,
            maximum: 3600));
        _sessionAbsoluteExpiration = TimeSpan.FromSeconds(ResolveIntSetting(
            configuration,
            "RepoQL:CSharp:WorkspaceSessionAbsoluteSeconds",
            "REPOQL_CSHARP_WORKSPACE_SESSION_ABSOLUTE_SECONDS",
            600,
            minimum: 10,
            maximum: 14400));
        if (_sessionAbsoluteExpiration < _sessionSlidingExpiration)
            _sessionAbsoluteExpiration = _sessionSlidingExpiration + _sessionSlidingExpiration;
        _sessionEntrySize = ResolveIntSetting(
            configuration,
            "RepoQL:CSharp:WorkspaceSessionEntrySize",
            "REPOQL_CSHARP_WORKSPACE_SESSION_ENTRY_SIZE",
            1,
            minimum: 1,
            maximum: 1024);
    }

    internal int ActiveSessionCount => _sessionKeys.Count;

    internal int GetProjectLoadCount(string projectPath)
    {
        if (_sessionCache.TryGetValue<ProjectSession>(Path.GetFullPath(projectPath), out var session))
            return session.LoadCount;
        return 0;
    }

    internal async Task<CSharpSemanticAnalysis?> TryAnalyzeAsync(
        string filePath,
        CSharpDocumentSurface surface,
        TextLineMap lineMap,
        CancellationToken cancellationToken)
    {
        // Check if SDK is available for semantic analysis
        if (!_sdkAvailable || _workspaceFactory is null)
            return null;

        // Check if we're disposing to prevent adding new sessions
        if (_disposing)
            return null;

        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return null;

        var projectPath = DotNetProjectLocator.FindProject(filePath);
        if (string.IsNullOrWhiteSpace(projectPath) || !File.Exists(projectPath))
            return null;

        var normalizedProjectPath = Path.GetFullPath(projectPath);
        var session = GetOrCreateSession(normalizedProjectPath);

        await ConcurrencyLimit.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await session.AnalyzeAsync(filePath, surface, lineMap, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to analyze {FilePath} in project {ProjectPath}. " +
                "Session will be removed and disposed. This may indicate project loading issues or compilation errors.",
                filePath,
                normalizedProjectPath);

            _sessionCache.Remove(normalizedProjectPath);
            _sessionKeys.TryRemove(normalizedProjectPath, out _);
            return null;
        }
        finally
        {
            ConcurrencyLimit.Release();
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // No initialization needed, resources are created on-demand
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        // Set disposing flag to prevent new sessions from being added
        _disposing = true;

        foreach (var key in _sessionKeys.Keys)
            _sessionCache.Remove(key);
        _sessionKeys.Clear();
        if (_ownsSessionCache)
            _sessionCache.Dispose();

        // NOTE: We do NOT dispose ConcurrencyLimit because it's a static shared resource.
        // Disposing it would break other workspace host instances that are still running.
        // Static semaphores should live for the application lifetime.
    }

    private ProjectSession GetOrCreateSession(string normalizedProjectPath)
    {
        if (_sessionCache.TryGetValue<ProjectSession>(normalizedProjectPath, out var existing) && existing is not null)
            return existing;

        var session = _sessionCache.GetOrCreate(normalizedProjectPath, entry =>
        {
            entry.SetSlidingExpiration(_sessionSlidingExpiration);
            entry.AbsoluteExpirationRelativeToNow = _sessionAbsoluteExpiration;
            entry.SetSize(_sessionEntrySize);
            entry.RegisterPostEvictionCallback((key, value, reason, _) =>
            {
                if (value is ProjectSession evictedSession)
                    evictedSession.Dispose();
                if (key is string projectKey)
                    _sessionKeys.TryRemove(projectKey, out byte _);
            });

            _sessionKeys.TryAdd(normalizedProjectPath, 0);
            return new ProjectSession(normalizedProjectPath, _workspaceFactory!);
        });

        return session!;
    }

    private static int ResolveIntSetting(
        IConfiguration? configuration,
        string configurationKey,
        string envKey,
        int defaultValue,
        int minimum,
        int maximum)
    {
        var raw = configuration?[configurationKey];
        if (int.TryParse(raw, out var configured))
            return Math.Clamp(configured, minimum, maximum);

        var env = Environment.GetEnvironmentVariable(envKey);
        if (int.TryParse(env, out var fromEnv))
            return Math.Clamp(fromEnv, minimum, maximum);

        return defaultValue;
    }

    private static void EnsureLocator(ILogger logger)
    {
        if (_locatorRegistered)
            return;
        lock (LocatorGate)
        {
            if (_locatorRegistered)
                return;

            try
            {
                if (!MSBuildLocator.IsRegistered)
                {
                    var instances = MSBuildLocator.QueryVisualStudioInstances().ToList();
                    if (instances.Count == 0)
                    {
                        _sdkAvailable = false;
                        _sdkUnavailableReason = "No .NET SDK found. Semantic analysis for C# will be disabled.";
                        logger.LogWarning(_sdkUnavailableReason);
                    }
                    else
                    {
                        MSBuildLocator.RegisterDefaults();
                        _sdkAvailable = true;
                        logger.LogDebug("MSBuild SDK registered: {SdkPath}", instances[0].MSBuildPath);
                    }
                }
                else
                {
                    _sdkAvailable = true;
                }
            }
            catch (Exception ex)
            {
                _sdkAvailable = false;
                _sdkUnavailableReason = $"Failed to locate .NET SDK: {ex.Message}";
                logger.LogWarning(ex, "MSBuild locator failed. Semantic analysis for C# will be disabled.");
            }

            _locatorRegistered = true;
        }
    }

    private static MSBuildWorkspace CreateWorkspace(ILogger<CSharpWorkspaceHost> logger)
    {
        var workspace = MSBuildWorkspace.Create(new Dictionary<string, string>
        {
            ["AlwaysCompileMarkupFilesInSeparateDomain"] = "false"
        });
        workspace.WorkspaceFailed += (_, args) =>
        {
            if (args.Diagnostic.Kind == WorkspaceDiagnosticKind.Failure)
            {
                logger.LogError("MSBuild workspace failure: {DiagnosticMessage}", args.Diagnostic.Message);
            }
            else
            {
                logger.LogWarning("MSBuild workspace diagnostic ({Kind}): {DiagnosticMessage}",
                    args.Diagnostic.Kind,
                    args.Diagnostic.Message);
            }
        };
        return workspace;
    }

    private sealed class ProjectSession : IDisposable
    {
        private readonly string _projectPath;
        private readonly Func<MSBuildWorkspace> _workspaceFactory;
        private readonly SemaphoreSlim _initialization = new(1, 1);
        private readonly SemaphoreSlim _compilationGate = new(1, 1);
        private readonly SemaphoreSlim _analyzerGate = new(1, 1);
        private MSBuildWorkspace? _workspace;
        private Project? _project;
        private Compilation? _compilationWithGenerators;
        private ImmutableArray<CSharpGeneratedDocumentState> _generatedDocuments = ImmutableArray<CSharpGeneratedDocumentState>.Empty;
        private ImmutableArray<Diagnostic> _generatorDiagnostics = ImmutableArray<Diagnostic>.Empty;
        private ImmutableArray<Diagnostic> _analyzerDiagnostics = ImmutableArray<Diagnostic>.Empty;
        private bool _analyzersComputed;
        private int _generatorPublishFlag;

        public ProjectSession(string projectPath, Func<MSBuildWorkspace> workspaceFactory)
        {
            _projectPath = projectPath;
            _workspaceFactory = workspaceFactory;
        }

        public int LoadCount { get; private set; }

        public async Task<CSharpSemanticAnalysis?> AnalyzeAsync(
            string filePath,
            CSharpDocumentSurface surface,
            TextLineMap lineMap,
            CancellationToken cancellationToken)
        {
            var project = await EnsureProjectAsync(cancellationToken).ConfigureAwait(false);
            if (project is null)
                return null;

            var compilation = await EnsureCompilationAsync(project, cancellationToken).ConfigureAwait(false);
            if (compilation is null)
                return null;

            var analysis = await AnalyzeCoreAsync(project, compilation, filePath, surface, lineMap, cancellationToken).ConfigureAwait(false);
            await ReleaseCompilationResourcesAsync().ConfigureAwait(false);
            return analysis;
        }

        private async Task<CSharpSemanticAnalysis?> AnalyzeCoreAsync(
            Project project,
            Compilation compilation,
            string filePath,
            CSharpDocumentSurface surface,
            TextLineMap lineMap,
            CancellationToken cancellationToken)
        {
            var normalizedPath = Path.GetFullPath(filePath);
            var document = project.Documents.FirstOrDefault(d =>
                !string.IsNullOrWhiteSpace(d.FilePath) &&
                Path.GetFullPath(d.FilePath!).Equals(normalizedPath, StringComparison.OrdinalIgnoreCase));
            if (document is null)
                return null;

            var syntaxTree = await document.GetSyntaxTreeAsync(cancellationToken).ConfigureAwait(false) as CSharpSyntaxTree;
            if (syntaxTree is null)
                return null;

            var semanticModel = compilation.GetSemanticModel(syntaxTree);
            var root = await syntaxTree.GetRootAsync(cancellationToken).ConfigureAwait(false) as CSharpSyntaxNode;
            if (root is null)
                return null;

            var declarations = BuildDeclarationLookup(surface, root);
            AnnotateSymbolKeys(surface, declarations, semanticModel, cancellationToken);

            var collector = new SymbolReferenceCollector(
                semanticModel,
                declarations.DeclaredNodeIds,
                lineMap,
                surface.DocumentId);
            collector.Visit(root);

            var diagnostics = new List<CSharpDiagnostic>();
            CollectDiagnostics(compilation, syntaxTree, lineMap, diagnostics, cancellationToken);
            CollectDiagnosticsFromSet(_generatorDiagnostics, syntaxTree, lineMap, diagnostics);
            CollectDiagnosticsFromSet(_analyzerDiagnostics, syntaxTree, lineMap, diagnostics);

            var generatedDocuments = TryPublishGeneratedDocuments();

            return new CSharpSemanticAnalysis(collector.References, diagnostics, generatedDocuments);
        }

        private async Task ReleaseCompilationResourcesAsync()
        {
            if (_compilationWithGenerators is null &&
                _generatedDocuments.IsDefaultOrEmpty &&
                _generatorDiagnostics.IsDefaultOrEmpty &&
                _analyzerDiagnostics.IsDefaultOrEmpty)
            {
                return;
            }

            await _compilationGate.WaitAsync().ConfigureAwait(false);
            try
            {
                _compilationWithGenerators = null;
                _generatedDocuments = ImmutableArray<CSharpGeneratedDocumentState>.Empty;
                _generatorDiagnostics = ImmutableArray<Diagnostic>.Empty;
                _analyzerDiagnostics = ImmutableArray<Diagnostic>.Empty;
                _analyzersComputed = false;
            }
            finally
            {
                _compilationGate.Release();
            }
        }

        private async Task<Project?> EnsureProjectAsync(CancellationToken cancellationToken)
        {
            if (_project is not null)
                return _project;

            await _initialization.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_project is not null)
                    return _project;

                _workspace = _workspaceFactory();

                // Add 30-second timeout for project loading
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

                try
                {
                    _project = await _workspace.OpenProjectAsync(_projectPath, cancellationToken: linkedCts.Token).ConfigureAwait(false);
                    LoadCount++;
                    return _project;
                }
                catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    // Timeout occurred
                    Console.Error.WriteLine($"Warning: Project load timeout (30s) for {_projectPath}");
                    return null;
                }
            }
            finally
            {
                _initialization.Release();
            }
        }

        private async Task<Compilation?> EnsureCompilationAsync(Project project, CancellationToken cancellationToken)
        {
            if (_compilationWithGenerators is not null)
                return _compilationWithGenerators;

            await _compilationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_compilationWithGenerators is not null)
                    return _compilationWithGenerators;

                var compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
                if (compilation is null)
                    return null;

                var generatorOutputs = await RunGeneratorsAsync(project, compilation, cancellationToken).ConfigureAwait(false);
                _compilationWithGenerators = generatorOutputs.Compilation;
                _generatedDocuments = generatorOutputs.GeneratedDocuments;
                _generatorDiagnostics = generatorOutputs.Diagnostics;
                await EnsureAnalyzersAsync(project, _compilationWithGenerators, cancellationToken).ConfigureAwait(false);
                AugmentGeneratedDocumentDiagnostics();
                return _compilationWithGenerators;
            }
            finally
            {
                _compilationGate.Release();
            }
        }

        private IReadOnlyList<CSharpGeneratedDocumentState> TryPublishGeneratedDocuments()
        {
            if (_generatedDocuments.IsDefaultOrEmpty)
                return Array.Empty<CSharpGeneratedDocumentState>();

            return Interlocked.Exchange(ref _generatorPublishFlag, 1) == 0
                ? _generatedDocuments
                : Array.Empty<CSharpGeneratedDocumentState>();
        }

        private async Task<(Compilation Compilation, ImmutableArray<CSharpGeneratedDocumentState> GeneratedDocuments, ImmutableArray<Diagnostic> Diagnostics)> RunGeneratorsAsync(
            Project project,
            Compilation compilation,
            CancellationToken cancellationToken)
        {
            if (compilation is not CSharpCompilation csharpCompilation)
                return (compilation, ImmutableArray<CSharpGeneratedDocumentState>.Empty, ImmutableArray<Diagnostic>.Empty);

            var generators = project.AnalyzerReferences
                .SelectMany(r => r.GetGenerators(LanguageNames.CSharp))
                .ToImmutableArray();
            if (generators.IsDefaultOrEmpty)
                return (compilation, ImmutableArray<CSharpGeneratedDocumentState>.Empty, ImmutableArray<Diagnostic>.Empty);

            var additionalTexts = await CreateAdditionalTextsAsync(project, cancellationToken).ConfigureAwait(false);
            var parseOptions = (CSharpParseOptions?)project.ParseOptions ?? new CSharpParseOptions(LanguageVersion.Preview);

            GeneratorDriver driver = CSharpGeneratorDriver.Create(generators.ToArray());
            driver = driver.WithUpdatedParseOptions(parseOptions);
            driver = driver.WithUpdatedAnalyzerConfigOptions(project.AnalyzerOptions.AnalyzerConfigOptionsProvider);
            if (!additionalTexts.IsDefaultOrEmpty)
                driver = driver.AddAdditionalTexts(additionalTexts);
            driver = driver.RunGeneratorsAndUpdateCompilation(csharpCompilation, out var updatedCompilation, out var generatorDiagnostics, cancellationToken);
            var runResult = driver.GetRunResult();

            var generatedDocs = await BuildGeneratedDocumentsAsync(project, updatedCompilation, runResult, cancellationToken).ConfigureAwait(false);

            return (updatedCompilation, generatedDocs, generatorDiagnostics);
        }

        private async Task EnsureAnalyzersAsync(Project project, Compilation compilation, CancellationToken cancellationToken)
        {
            if (_analyzersComputed)
                return;

            await _analyzerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_analyzersComputed)
                    return;

                var analyzers = project.AnalyzerReferences
                    .SelectMany(r => r.GetAnalyzers(LanguageNames.CSharp))
                    .ToImmutableArray();
                if (analyzers.IsDefaultOrEmpty)
                {
                    _analyzerDiagnostics = ImmutableArray<Diagnostic>.Empty;
                    _analyzersComputed = true;
                    return;
                }

                var additionalTexts = await CreateAdditionalTextsAsync(project, cancellationToken).ConfigureAwait(false);
                var analyzerOptions = new AnalyzerOptions(additionalTexts, project.AnalyzerOptions.AnalyzerConfigOptionsProvider);
                var compilationWithAnalyzers = compilation.WithAnalyzers(
                    analyzers,
                    new CompilationWithAnalyzersOptions(
                        analyzerOptions,
                        onAnalyzerException: null,
                        concurrentAnalysis: true,
                        logAnalyzerExecutionTime: false,
                        reportSuppressedDiagnostics: false));

                _analyzerDiagnostics = await compilationWithAnalyzers.GetAnalyzerDiagnosticsAsync(cancellationToken).ConfigureAwait(false);
                _analyzersComputed = true;
            }
            finally
            {
                _analyzerGate.Release();
            }
        }

        public void Dispose()
        {
            _initialization.Dispose();
            _compilationGate.Dispose();
            _analyzerGate.Dispose();
            _workspace?.Dispose();
            _workspace = null;
            _project = null;
            _compilationWithGenerators = null;
            _generatedDocuments = ImmutableArray<CSharpGeneratedDocumentState>.Empty;
            _generatorDiagnostics = ImmutableArray<Diagnostic>.Empty;
            _generatorPublishFlag = 0;
        }

        private static void AnnotateSymbolKeys(
            CSharpDocumentSurface surface,
            DeclarationLookup declarations,
            SemanticModel semanticModel,
            CancellationToken cancellationToken)
        {
            if (surface.Types is List<CSharpTypeInfo> types)
            {
                for (var i = 0; i < types.Count; i++)
                {
                    if (declarations.TypeNodes.TryGetValue(types[i].NodeId, out var syntax))
                    {
                        var symbol = semanticModel.GetDeclaredSymbol(syntax, cancellationToken);
                        if (symbol is not null)
                        {
                            var key = CSharpSemanticUtilities.BuildSymbolKey(symbol);
                            types[i] = types[i] with { SymbolKey = key };
                        }
                    }
                }
            }

            if (surface.Members is List<CSharpMemberInfo> members)
            {
                for (var i = 0; i < members.Count; i++)
                {
                    if (declarations.MemberNodes.TryGetValue(members[i].NodeId, out var syntax))
                    {
                        var symbol = semanticModel.GetDeclaredSymbol(syntax, cancellationToken);
                        if (symbol is not null)
                        {
                            var key = CSharpSemanticUtilities.BuildSymbolKey(symbol);
                            members[i] = members[i] with { SymbolKey = key };
                        }
                    }
                }
            }
        }

        private static void CollectDiagnostics(
            Compilation compilation,
            SyntaxTree syntaxTree,
            TextLineMap lineMap,
            List<CSharpDiagnostic> sink,
            CancellationToken cancellationToken)
        {
            foreach (var diag in compilation.GetDiagnostics(cancellationToken))
            {
                if (!diag.Location.IsInSource)
                    continue;
                if (!ReferenceEquals(diag.Location.SourceTree, syntaxTree))
                    continue;

                sink.Add(ToDiagnostic(diag, lineMap));
            }
        }

        private static void CollectDiagnosticsFromSet(
            ImmutableArray<Diagnostic> diagnostics,
            SyntaxTree syntaxTree,
            TextLineMap lineMap,
            List<CSharpDiagnostic> sink)
        {
            if (diagnostics.IsDefaultOrEmpty)
                return;

            foreach (var diag in diagnostics)
            {
                if (!diag.Location.IsInSource)
                    continue;
                if (!ReferenceEquals(diag.Location.SourceTree, syntaxTree))
                    continue;

                sink.Add(ToDiagnostic(diag, lineMap));
            }
        }

        private static CSharpDiagnostic ToDiagnostic(Diagnostic diag, TextLineMap lineMap)
        {
            var span = lineMap.GetSpan(diag.Location.SourceSpan.Start, diag.Location.SourceSpan.End);
            return new CSharpDiagnostic(
                diag.Id,
                diag.GetMessage(),
                diag.Severity.ToString(),
                diag.Descriptor.Category ?? string.Empty,
                diag.Descriptor.HelpLinkUri,
                span);
        }

        private static async Task<ImmutableArray<AdditionalText>> CreateAdditionalTextsAsync(Project project, CancellationToken cancellationToken)
        {
            var documents = project.AdditionalDocuments.ToImmutableArray();
            if (documents.Length == 0)
                return ImmutableArray<AdditionalText>.Empty;

            var list = new List<AdditionalText>(documents.Length);
            foreach (var doc in documents)
            {
                var text = await doc.GetTextAsync(cancellationToken).ConfigureAwait(false);
                if (text is null)
                    continue;
                list.Add(new InMemoryAdditionalText(doc.FilePath ?? doc.Name, text));
            }
            return list.ToImmutableArray();
        }

        private async Task<ImmutableArray<CSharpGeneratedDocumentState>> BuildGeneratedDocumentsAsync(
            Project project,
            Compilation compilation,
            GeneratorDriverRunResult runResult,
            CancellationToken cancellationToken)
        {
            if (runResult.GeneratedTrees.Length == 0)
                return ImmutableArray<CSharpGeneratedDocumentState>.Empty;

            var generated = new List<CSharpGeneratedDocumentState>();
            var mediaType = SemanticMediaType.Create("text", "plain").WithKind("code.csharp");

            foreach (var generatorResult in runResult.Results)
            {
                var generatorName = generatorResult.Generator.GetType().FullName ?? generatorResult.Generator.GetType().Name;
                foreach (var source in generatorResult.GeneratedSources)
                {
                    var tree = source.SyntaxTree;
                    var sourceText = source.SourceText.ToString();
                    var lineMap = new TextLineMap(sourceText);
                    var repoUri = BuildGeneratedUri(project, generatorName, source.HintName, tree.FilePath);
                    var documentId = CSharpIdFactory.CreateDocumentId(repoUri);
                    var walker = new CSharpInventoryWalker(documentId, lineMap);
                    if (await tree.GetRootAsync(cancellationToken).ConfigureAwait(false) is not CSharpSyntaxNode csharpRoot)
                        continue;
                    walker.Visit(csharpRoot);
                    var documentProps = CSharpLoader.BuildDocumentProperties(lineMap, walker, repoUri);
                    documentProps["is_generated"] = true;
                    documentProps["generator"] = generatorName;
                    documentProps["hint_name"] = source.HintName;

                    var surface = new CSharpDocumentSurface
                    {
                        DocumentId = documentId,
                        DocumentProperties = documentProps,
                        Namespaces = walker.Namespaces,
                        Types = walker.Types,
                        Members = walker.Members,
                        Usings = walker.Usings
                    };

                    var semanticModel = compilation.GetSemanticModel(tree);
                    var declarations = BuildDeclarationLookup(surface, csharpRoot);
                    AnnotateSymbolKeys(surface, declarations, semanticModel, cancellationToken);

                    var collector = new SymbolReferenceCollector(
                        semanticModel,
                        walker.DeclaredNodeIds,
                        lineMap,
                        documentId);
                    collector.Visit(csharpRoot);

                    var diagnostics = new List<CSharpDiagnostic>();
                    CollectDiagnostics(compilation, tree, lineMap, diagnostics, cancellationToken);

                    var textBytes = Encoding.UTF8.GetBytes(sourceText);
                    var digest = ContentDigest.FromBytes(textBytes);

                    generated.Add(new CSharpGeneratedDocumentState(
                        DocumentId: documentId,
                        StoreUri: repoUri.ToString(),
                        GeneratorName: generatorName,
                        HintName: source.HintName,
                        Text: sourceText,
                        FilePath: tree.FilePath,
                        MediaType: mediaType,
                        Digest: digest,
                        Size: textBytes.Length,
                        Surface: surface,
                        References: collector.References,
                        Diagnostics: diagnostics));
                }
            }

            return generated.ToImmutableArray();
        }

        private void AugmentGeneratedDocumentDiagnostics()
        {
            if (_generatedDocuments.IsDefaultOrEmpty)
                return;
            if ((_generatorDiagnostics.IsDefaultOrEmpty || _generatorDiagnostics.Length == 0) &&
                (_analyzerDiagnostics.IsDefaultOrEmpty || _analyzerDiagnostics.Length == 0))
                return;

            var builder = ImmutableArray.CreateBuilder<CSharpGeneratedDocumentState>(_generatedDocuments.Length);
            foreach (var doc in _generatedDocuments)
            {
                var diagList = doc.Diagnostics.ToList();
                var lineMap = new TextLineMap(doc.Text);
                MergeDiagnosticsForFile(_generatorDiagnostics, doc.FilePath, lineMap, diagList);
                MergeDiagnosticsForFile(_analyzerDiagnostics, doc.FilePath, lineMap, diagList);
                builder.Add(doc with { Diagnostics = diagList });
            }
            _generatedDocuments = builder.ToImmutable();
        }

        private static void MergeDiagnosticsForFile(
            ImmutableArray<Diagnostic> diagnostics,
            string? filePath,
            TextLineMap lineMap,
            List<CSharpDiagnostic> sink)
        {
            if (diagnostics.IsDefaultOrEmpty || diagnostics.Length == 0 || string.IsNullOrWhiteSpace(filePath))
                return;

            var targetPath = Path.GetFullPath(filePath);
            foreach (var diag in diagnostics)
            {
                if (!diag.Location.IsInSource)
                    continue;
                var diagPath = diag.Location.SourceTree?.FilePath;
                if (string.IsNullOrWhiteSpace(diagPath))
                    continue;
                if (!Path.GetFullPath(diagPath).Equals(targetPath, StringComparison.OrdinalIgnoreCase))
                    continue;

                var span = lineMap.GetSpan(diag.Location.SourceSpan.Start, diag.Location.SourceSpan.End);
                sink.Add(new CSharpDiagnostic(
                    diag.Id,
                    diag.GetMessage(),
                    diag.Severity.ToString(),
                    diag.Descriptor.Category ?? string.Empty,
                    diag.Descriptor.HelpLinkUri,
                    span));
            }
        }
    }

    private static DeclarationLookup BuildDeclarationLookup(CSharpDocumentSurface surface, CSharpSyntaxNode root)
    {
        var namespaceNodes = new Dictionary<Guid, SyntaxNode>();
        var typeNodes = new Dictionary<Guid, BaseTypeDeclarationSyntax>();
        var memberNodes = new Dictionary<Guid, SyntaxNode>();
        var declaredNodeIds = new Dictionary<SyntaxNode, Guid>(ReferenceEqualityComparer.Instance);

        // Build span-to-node index once to avoid O(n²) behavior
        // This walks the tree once and indexes all declaration nodes by their span
        var spanIndex = new Dictionary<TextSpan, SyntaxNode>();
        BuildSpanIndex(root, spanIndex);

        // Now look up nodes by span in O(1) instead of O(n) per lookup
        foreach (var ns in surface.Namespaces)
        {
            var textSpan = BoundSpan(ns.Span, root.FullSpan.End);
            if (spanIndex.TryGetValue(textSpan, out var syntax) && syntax is BaseNamespaceDeclarationSyntax nsDecl)
            {
                namespaceNodes[ns.NodeId] = nsDecl;
                declaredNodeIds[nsDecl] = ns.NodeId;
            }
        }

        foreach (var type in surface.Types)
        {
            var textSpan = BoundSpan(type.Span, root.FullSpan.End);
            if (spanIndex.TryGetValue(textSpan, out var syntax) && syntax is BaseTypeDeclarationSyntax typeDecl)
            {
                typeNodes[type.NodeId] = typeDecl;
                declaredNodeIds[typeDecl] = type.NodeId;
            }
        }

        foreach (var member in surface.Members)
        {
            var textSpan = BoundSpan(member.Span, root.FullSpan.End);
            if (spanIndex.TryGetValue(textSpan, out var syntax))
            {
                memberNodes[member.NodeId] = syntax;
                declaredNodeIds[syntax] = member.NodeId;
            }
        }

        return new DeclarationLookup(namespaceNodes, typeNodes, memberNodes, declaredNodeIds);
    }

    private static void BuildSpanIndex(SyntaxNode node, Dictionary<TextSpan, SyntaxNode> index)
    {
        // Index namespace declarations
        if (node is BaseNamespaceDeclarationSyntax)
        {
            index.TryAdd(node.Span, node);
        }
        // Index type declarations
        else if (node is BaseTypeDeclarationSyntax)
        {
            index.TryAdd(node.Span, node);
        }
        // Index member declarations (methods, properties, fields, etc.)
        else if (node is BaseMethodDeclarationSyntax or PropertyDeclarationSyntax or
                 FieldDeclarationSyntax or EventDeclarationSyntax or
                 IndexerDeclarationSyntax or OperatorDeclarationSyntax or
                 ConversionOperatorDeclarationSyntax)
        {
            index.TryAdd(node.Span, node);
        }

        // Recursively index child nodes
        foreach (var child in node.ChildNodes())
        {
            BuildSpanIndex(child, index);
        }
    }

    private static bool TryFindNode<TNode>(CSharpSyntaxNode root, DocumentSpan span, out TNode? node)
        where TNode : SyntaxNode
    {
        var textSpan = BoundSpan(span, root.FullSpan.End);
        var match = root.FindNode(textSpan, getInnermostNodeForTie: true, findInsideTrivia: false);
        if (match is TNode typed)
        {
            node = typed;
            return true;
        }

        node = null;
        return false;
    }

    private static TextSpan BoundSpan(DocumentSpan span, int maxLength)
    {
        var start = Math.Clamp(span.StartChar, 0, maxLength);
        var end = Math.Clamp(span.EndChar, start, maxLength);
        return TextSpan.FromBounds(start, end);
    }

    private sealed record DeclarationLookup(
        IReadOnlyDictionary<Guid, SyntaxNode> NamespaceNodes,
        IReadOnlyDictionary<Guid, BaseTypeDeclarationSyntax> TypeNodes,
        IReadOnlyDictionary<Guid, SyntaxNode> MemberNodes,
        IReadOnlyDictionary<SyntaxNode, Guid> DeclaredNodeIds);

    internal sealed record CSharpSemanticAnalysis(
        IReadOnlyList<CSharpSymbolReference> References,
        IReadOnlyList<CSharpDiagnostic> Diagnostics,
        IReadOnlyList<CSharpGeneratedDocumentState> GeneratedDocuments);

    private static RepoUri BuildGeneratedUri(Project project, string generatorName, string hintName, string? treeFilePath)
    {
        var projectName = Path.GetFileNameWithoutExtension(project.FilePath) ?? "project";
        var treeSegment = !string.IsNullOrWhiteSpace(treeFilePath)
            ? SanitizeSegment(Path.GetFileName(treeFilePath))
            : SanitizeSegment(hintName);
        var uri = $"repoql://generated/{SanitizeSegment(projectName)}/{SanitizeSegment(generatorName)}/{treeSegment}";
        return RepoUri.Parse(uri);
    }

    private static string SanitizeSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "generated";

        Span<char> buffer = stackalloc char[value.Length];
        var length = 0;
        foreach (var ch in value)
        {
            buffer[length++] = char.IsLetterOrDigit(ch) ? ch : '_';
        }

        var cleaned = new string(buffer[..length]).Trim('_');
        return string.IsNullOrWhiteSpace(cleaned) ? "generated" : cleaned;
    }

    private sealed class InMemoryAdditionalText : AdditionalText
    {
        private readonly SourceText _text;

        public InMemoryAdditionalText(string path, SourceText text)
        {
            Path = string.IsNullOrWhiteSpace(path) ? $"generated_{Guid.NewGuid():N}" : path;
            _text = text;
        }

        public override string Path { get; }

        public override SourceText GetText(CancellationToken cancellationToken = default) => _text;
    }
}
