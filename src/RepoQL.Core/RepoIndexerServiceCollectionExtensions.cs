using System.Diagnostics.Metrics;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RepoQL.Contracts;
using RepoQL.Contracts.Analysis;
using RepoQL.Contracts.Data;
using RepoQL.Contracts.Embeddings;
using RepoQL.Contracts.Models;
using RepoQL.Core.Analysis;
using RepoQL.Core.Analysis.EditorConfig;
using RepoQL.Core.Operations;
using RepoQL.Core.PlainText;
using RepoQL.Core.Metrics;
using RepoQL.Data.DuckDB;
using RepoQL.Embeddings;
using RepoQL.FileSystem;
using RepoQL.FileSystem.Abstractions;
using RepoQL.FileSystem.Classification;
using RepoQL.FileSystem.Embedded;
using RepoQL.FileSystem.Physical;
using RepoQL.Documentation;
using RepoQL.Formats.DotNet;
using RepoQL.Formats.GraphQL;
using RepoQL.Formats.Markdown;
using RepoQL.Formats.Mermaid;
using RepoQL.Formats.PHP;
using RepoQL.Formats.Terraform;
using RepoQL.Formats.TypeScript;
using RepoQL.Formats.Sql;
using RepoQL.Formats.CSS;
using RepoQL.Formats.Xlsx;
using RepoQL.Formats.Docx;
using RepoQL.Formats.Pdf;
using RepoQL.Formats.Csv;
using RepoQL.Formats.Ruby;
using RepoQL.Indexing.FileSystems;
using RepoQL.Indexing.FileSystems.Imports;
using RepoQL.Indexing.Git;
using RepoQL.Indexing.Hosting;
using RepoQL.Indexing.Indexing;
using RepoQL.Indexing.Indexing.Commit;
using RepoQL.Indexing.Indexing.Pipelines;
using RepoQL.Indexing.Indexing.Pipelines.Analysis;
using RepoQL.Indexing.Indexing.Pipelines.Classification;
using RepoQL.Indexing.Indexing.Pipelines.Discovery;
using RepoQL.Indexing.Indexing.Pipelines.Parsing;
using RepoQL.Indexing.Indexing.PostProcessing;
using RepoQL.Indexing.Indexing.State;
using RepoQL.Metrics;
using RepoQL.Templating;
using RepoQL.Mcp.Client;
using RepoQL.Mcp.Client.Configuration;

namespace RepoQL.Core;

/// <summary>
///     DI extensions to register the repo indexer, stores, state, and engine.
/// </summary>
public static class RepoIndexerServiceCollectionExtensions
{
    /// <summary>
    ///     Register a complete repo indexer stack backed by a local repository and DuckDB file at the repo root.
    /// </summary>
    /// <param name="services">Service collection.</param>
    /// <param name="repoRootPath">Absolute or relative filesystem path to the repository root.</param>
    /// <returns>The same <see cref="IServiceCollection" /> to allow chaining.</returns>
    public static IServiceCollection AddRepoIndexer(
        this IServiceCollection services,
        string? repoRootPath = null)
    {
        // discover repo root if not provided
        var resolvedRoot = string.IsNullOrWhiteSpace(repoRootPath)
            ? RepoLocator.FindRepoRoot()
            : Path.GetFullPath(repoRootPath);

        // ensure .repoql directory exists and compute repo-relative db path
        var dbName = "index.duckdb";
        var dbRelPath = RepoLocator.DefaultDbRelativePath(dbName); // ".repoql/index.duckdb"
        var dbDirFull = RepoLocator.EnsureRepoqlDirectory(resolvedRoot);

        // Stores - RepoStore should be given the repo root and the repo-relative DB path so it can exclude the DB
        services.AddSingleton(new Meter("RepoQL"));
        services.AddSingleton(_ => new PhysicalFileSystem(resolvedRoot));
        services.AddSingleton<IFileClassifier, FileClassifier>();
        services.AddSingleton<IVirtualFileSystem>(sp => sp.GetRequiredService<PhysicalFileSystem>());
        services.AddSingleton<IUriFilter>(_ => new RepoGitIgnoreFilter(resolvedRoot, [dbRelPath]));
        services.AddSingleton<IHasher, XxHasher>();

        services.AddSingleton<DocumentationFileSystem>();
        services.AddSingleton<CompositeFileSystemMount>(sp => new CompositeFileSystemMount
        {
            Id = "help",
            FileSystem = sp.GetRequiredService<DocumentationFileSystem>(),
            IncludeInEnumeration = true,
            EnableWatching = false,
            UriPredicate = uri => string.Equals(uri.Scheme, DocumentationFileSystem.Scheme, StringComparison.OrdinalIgnoreCase)
        });

        services.AddSingleton<ICompositeFileSystemManager>(sp =>
        {
            var primary = sp.GetRequiredService<PhysicalFileSystem>();
            var mounts = sp.GetServices<CompositeFileSystemMount>();
            var managerLogger = sp.GetService<ILogger<CompositeFileSystemManager>>();
            var compositeLogger = sp.GetService<ILogger<CompositeFileSystem>>();
            return new CompositeFileSystemManager(primary, mounts, managerLogger, compositeLogger);
        });
        services.AddSingleton(sp => sp.GetRequiredService<ICompositeFileSystemManager>().FileSystem);
        services.AddSingleton<IMultiFileSystem>(sp => sp.GetRequiredService<CompositeFileSystem>());

        var dbFileFullPath = Path.Combine(resolvedRoot, dbRelPath);

        services.AddOptions<IndexingEngineOptions>();
        services.AddOptions<RepoqlHostOptions>();
        services.AddMemoryCache(options =>
        {
            // Shared process cache for expensive reusable resources (Roslyn sessions, etc.).
            // Entries must set Size to participate in this cap.
            options.SizeLimit = ResolveSharedCacheSizeLimit();
            options.CompactionPercentage = 0.2;
        });

        // Embedding mode: controls resource usage for constrained hardware
        // REPOQL_EMBED_MODE: none|structure|full (default: full)
        // Legacy REPOQL_EMBED_ENABLED=0 maps to mode=none
        var embeddingMode = EmbeddingModeExtensions.ParseEmbeddingMode(
            Environment.GetEnvironmentVariable("REPOQL_EMBED_MODE"));

        // Legacy: REPOQL_EMBED_ENABLED=0 forces None mode
        if (string.Equals(Environment.GetEnvironmentVariable("REPOQL_EMBED_ENABLED"), "0", StringComparison.Ordinal))
            embeddingMode = EmbeddingMode.None;

        // Check for OpenRouter API key - used for LLM provider
        var openRouterKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        var useOpenRouter = !string.IsNullOrWhiteSpace(openRouterKey);
        if (embeddingMode != EmbeddingMode.None)
        {
            // Always use full mode for best quality
            embeddingMode = EmbeddingMode.Full;
        }

        services.AddSingleton(new EmbeddingModeOptions(embeddingMode));

        // LLM provider: OpenRouter (cloud) if API key present, otherwise disabled
        services.AddSingleton<ILlmProvider>(sp =>
        {
            if (!useOpenRouter)
            {
                return new DisabledLlmProvider();
            }

            return new RepoQL.LLM.Client.OpenRouterLlmProvider(
                apiKey: openRouterKey,
                logger: sp.GetService<ILogger<RepoQL.LLM.Client.OpenRouterLlmProvider>>());
        });

        // Embeddings provider: OpenRouter (cloud) if API key present, otherwise local ONNX
        services.AddSingleton<IEmbeddingProvider>(sp =>
        {
            var lf = sp.GetService<ILoggerFactory>();
            var log = lf?.CreateLogger("RepoQL.Embeddings");
            var mode = sp.GetRequiredService<EmbeddingModeOptions>().Mode;
            var degradation = sp.GetService<IServiceDegradationTracker>();
            string? failureMessage = null;
            var onnxLogger = sp.GetService<ILogger<OnnxEmbeddingProvider>>();

            if (mode == EmbeddingMode.None)
            {
                log?.LogInformation("Embedding provider: disabled (mode=None)");
                return new DisabledEmbeddingProvider();
            }

            // Use OpenRouter cloud embeddings if API key is present
            if (useOpenRouter)
            {
                try
                {
                    log?.LogInformation("Embedding provider: using OpenRouter (all-MiniLM-L6-v2, 384 dims, mode=Full)");
                    return new RepoQL.LLM.Client.OpenRouterEmbeddingProvider(
                        apiKey: openRouterKey,
                        logger: sp.GetService<ILogger<RepoQL.LLM.Client.OpenRouterEmbeddingProvider>>());
                }
                catch (Exception ex)
                {
                    failureMessage = $"OpenRouter embeddings failed: {ex.Message}";
                    log?.LogWarning(ex, "Embedding provider: OpenRouter initialization failed; falling back");
                }
            }

            // No API key - use local ONNX embeddings
            // Prefer explicit ONNX model path via env
            var onnxPath = GetEmbeddingModelPath();
            var maxTokens = GetEmbeddingMaxTokens();
            if (!string.IsNullOrWhiteSpace(onnxPath) && File.Exists(onnxPath))
            {
                var onnx = TryCreateOnnxProvider(onnxPath, onnxLogger, maxTokens, out var error);
                if (onnx is not null)
                    return onnx;

                if (error is null)
                {
                    failureMessage = $"ONNX failed to initialize from explicit path {onnxPath}";
                    log?.LogWarning("Embedding provider: ONNX failed to initialize from explicit path {Path}; falling back", onnxPath);
                }
                else
                {
                    failureMessage = $"ONNX failed to initialize from explicit path {onnxPath}: {error.Message}";
                    log?.LogWarning(error, "Embedding provider: ONNX failed to initialize from explicit path {Path}; falling back", onnxPath);
                }
            }

            // Load the shipped model: Embeddings/Model/embedding_model.onnx (quantized int8)
            // If not present, extract from embedded resources in the entry assembly on first run.
            try
            {
                var (_, modelDir, shipped) = GetEmbeddingModelPaths();

                if (!File.Exists(shipped))
                {
                    try
                    {
                        ExtractEmbeddedModelIfAvailable(modelDir, log);
                    }
                    catch (Exception ex)
                    {
                        log?.LogWarning(ex, "Embedding provider: failed to extract embedded model resources");
                    }
                }

                if (File.Exists(shipped))
                {
                    log?.LogInformation("Embedding provider: using model at {Path}", shipped);
                    var onnx = TryCreateOnnxProvider(shipped, onnxLogger, maxTokens, out var error);
                    if (onnx is not null)
                        return onnx;

                    if (error is null)
                    {
                        failureMessage = "Shipped ONNX model failed to initialize";
                        log?.LogWarning("Embedding provider: shipped model failed to initialize; falling back");
                    }
                    else
                    {
                        failureMessage = $"Shipped ONNX model failed to initialize: {error.Message}";
                        log?.LogWarning(error, "Embedding provider: shipped model failed to initialize; falling back");
                    }
                }
                else
                {
                    failureMessage ??= "No shipped ONNX model found";
                }
            }
            catch (Exception ex)
            {
                failureMessage = $"Embedding provider failed to initialize: {ex.Message}";
                log?.LogWarning(ex, "Embedding provider: embedding failed to initialize");
                // swallow and fall back to hashed provider
            }

            // Fallback: hashed provider (deterministic, lightweight)
            var dimEnv = Environment.GetEnvironmentVariable("REPOQL_EMBED_DIM");
            var dim = 384;
            if (int.TryParse(dimEnv, out var parsed) && parsed > 0) dim = parsed;
            log?.LogInformation("Embedding provider: using hashed fallback with dim={Dim}", dim);
            if (!string.IsNullOrWhiteSpace(failureMessage))
            {
                degradation?.MarkDegraded(ServiceDegradationKind.Embeddings,
                    $"Embeddings degraded; using hashed fallback. {failureMessage}");
            }
            return new HashedEmbeddingProvider(dim);
        });

        // Local ONNX embedding provider for fast interactive search (JIT embeddings)
        services.AddKeyedSingleton<IEmbeddingProvider>("local", (sp, _) =>
        {
            var lf = sp.GetService<ILoggerFactory>();
            var log = lf?.CreateLogger("RepoQL.Embeddings.Local");
            var mode = sp.GetRequiredService<EmbeddingModeOptions>().Mode;
            var degradation = sp.GetService<IServiceDegradationTracker>();
            string? failureMessage = null;
            var onnxLogger = sp.GetService<ILogger<OnnxEmbeddingProvider>>();

            if (mode == EmbeddingMode.None)
            {
                log?.LogDebug("Local embedding provider: disabled (mode=None)");
                return new DisabledEmbeddingProvider();
            }

            // Always use local ONNX for speed
            var onnxPath = GetEmbeddingModelPath();
            var maxTokens = GetEmbeddingMaxTokens();

            if (!string.IsNullOrWhiteSpace(onnxPath) && File.Exists(onnxPath))
            {
                var onnx = TryCreateOnnxProvider(onnxPath, onnxLogger, maxTokens, out var error);
                if (onnx is not null)
                {
                    log?.LogInformation("Local embedding provider: using ONNX from explicit path");
                    return onnx;
                }

                if (error is null)
                {
                    failureMessage = $"Local ONNX failed to initialize from explicit path {onnxPath}";
                    log?.LogError("Local embedding provider: ONNX from explicit path failed to initialize");
                }
                else
                {
                    failureMessage = $"Local ONNX failed to initialize from explicit path {onnxPath}: {error.Message}";
                    log?.LogError(error, "Local embedding provider: ONNX from explicit path failed to initialize");
                }
            }

            // Load shipped model
            var (baseDir, modelDir, shipped) = GetEmbeddingModelPaths();
            log?.LogInformation("Local embedding provider: looking for ONNX at {Path} (baseDir={BaseDir})", shipped, baseDir);

            // Extract from embedded resources if not already present
            if (!File.Exists(shipped))
            {
                try
                {
                    ExtractEmbeddedModelIfAvailable(modelDir, log);
                }
                catch (Exception ex)
                {
                    log?.LogWarning(ex, "Local embedding provider: failed to extract embedded model resources");
                }
            }

            if (File.Exists(shipped))
            {
                var onnx = TryCreateOnnxProvider(shipped, onnxLogger, maxTokens, out var error);
                if (onnx is not null)
                {
                    log?.LogInformation("Local embedding provider: using shipped ONNX model from {Path}", shipped);
                    return onnx;
                }

                if (error is null)
                {
                    failureMessage = $"Local ONNX model at {shipped} failed to initialize";
                    log?.LogWarning("Local embedding provider: ONNX model at {Path} failed to initialize, returning disabled", shipped);
                }
                else
                {
                    failureMessage = $"Local ONNX model at {shipped} failed to initialize: {error.Message}";
                    log?.LogWarning(error, "Local embedding provider: ONNX model at {Path} failed to initialize", shipped);
                }
            }

            log?.LogWarning("Local embedding provider: no ONNX model found at {Path}, returning disabled", shipped);
            failureMessage ??= "No local ONNX model found";
            if (!string.IsNullOrWhiteSpace(failureMessage))
            {
                degradation?.MarkDegraded(ServiceDegradationKind.Embeddings,
                    $"Local embeddings disabled. {failureMessage}");
            }
            return new DisabledEmbeddingProvider();
        });

        services.AddSingleton<IAnalysisResultWriter, AnnotationResultWriter>();
        services.AddSingleton<IAnalyzerSettingsProvider>(_ => new EditorConfigSettingsProvider(resolvedRoot));
        services.AddSingleton<Func<AnalyzerContext>>(_ =>
            () => new AnalyzerContext(new AnalyzerSettings(), resolvedRoot));

        // Templating for x-ray summaries (embedded defaults)
        services.AddLiquidTemplatingFromEmbedded(
            assembly: typeof(MarkdownLoader).Assembly,
            resourceRoot: "RepoQL.Formats.Markdown.Templates");
        services.AddLiquidTemplatingFromEmbedded(
            assembly: typeof(CsProjLoader).Assembly,
            resourceRoot: "RepoQL.Formats.DotNet.Templates");

        services.AddMarkdownFormat();
        services.AddTypeScriptFormat();
        services.AddPHPFormat();
        services.AddRubyFormat();
        services.AddTerraformFormat();
        services.AddCSSFormat();
        services.AddXlsxFormat();
        services.AddDocxFormat();
        services.AddPdfFormat();
        services.AddCsvFormat();
        services.AddSingleton<MermaidLoader>();
        services.AddSingleton<MermaidAnalyzer>();
        services.AddSingleton<CsProjAnalyzer>();
        services.AddSingleton<SlnLoader>(sp => new SlnLoader(sp.GetRequiredService<ITemplateRenderer>()));
        services.AddSingleton<CSharpWorkspaceHost>();
        services.AddHostedService(sp => sp.GetRequiredService<CSharpWorkspaceHost>());
        services.AddSingleton<CSharpLoader>(sp =>
        {
            var host = sp.GetRequiredService<CSharpWorkspaceHost>();
            var configuration = sp.GetService<IConfiguration>();
            var logger = sp.GetService<ILogger<CSharpLoader>>();
            return new CSharpLoader(host, configuration, logger);
        });
        services.AddSingleton<IFormatSchemaProvider>(sp => sp.GetRequiredService<CSharpLoader>());
        services.AddSingleton<CSharpAnalyzer>();
        services.AddSingleton<PlainTextLoader>();
        services.AddGraphQLFormat();
        services.AddSingleton<CsProjLoader>(sp => new CsProjLoader());
        services.AddSingleton<AppSettingsLoader>(sp => new AppSettingsLoader(sp.GetRequiredService<ITemplateRenderer>()));
        services.AddSingleton<AppSettingsAnalyzer>();
        services.AddSingleton<SqlLoader>();

        services.AddSingleton<FormatDescriptor>(sp =>
        {
            var loader = sp.GetRequiredService<MermaidLoader>();
            var analyzer = sp.GetRequiredService<MermaidAnalyzer>();
            return new FormatDescriptor(
                SemanticMediaType.Create("text", "mermaid").WithKind("mermaid.doc"),
                loader,
                analyzer,
                loader,
                new[] { "mermaid", "mmd" });
        });
        services.AddSingleton<FormatDescriptor>(sp =>
        {
            var loader = sp.GetRequiredService<CsProjLoader>();
            var analyzer = sp.GetRequiredService<CsProjAnalyzer>();
            return new FormatDescriptor(
                SemanticMediaType.Create("application", "xml").WithKind("dotnet.csproj"),
                loader,
                analyzer,
                loader,
                new[] { "csproj" });
        });
        services.AddSingleton<FormatDescriptor>(sp =>
        {
            var loader = sp.GetRequiredService<SlnLoader>();
            var analyzer = new NullAnalyzer(SemanticMediaType.Create("text", "plain").WithKind("dotnet.sln"));
            return new FormatDescriptor(
                SemanticMediaType.Create("text", "plain").WithKind("dotnet.sln"),
                loader,
                analyzer,
                loader,
                new[] { "sln" });
        });
        services.AddSingleton<FormatDescriptor>(sp =>
        {
            var loader = sp.GetRequiredService<CSharpLoader>();
            var analyzer = sp.GetRequiredService<CSharpAnalyzer>();
            return new FormatDescriptor(
                SemanticMediaType.Create("text", "plain").WithKind("code.csharp"),
                loader,
                analyzer,
                loader,
                new[] { "csharp", "cs" });
        });
        services.AddSingleton<FormatDescriptor>(sp =>
        {
            var loader = sp.GetRequiredService<AppSettingsLoader>();
            var analyzer = sp.GetRequiredService<AppSettingsAnalyzer>();
            return new FormatDescriptor(
                SemanticMediaType.Create("application", "json").WithKind("config.appsettings"),
                loader,
                analyzer,
                loader,
                new[] { "appsettings", "config" });
        });
        services.AddSingleton<FormatDescriptor>(sp =>
        {
            var loader = sp.GetRequiredService<SqlLoader>();
            var analyzer = new NullAnalyzer(SemanticMediaType.Create("text", "plain").WithKind("query.sql"));
            return new FormatDescriptor(
                SemanticMediaType.Create("text", "plain").WithKind("query.sql"),
                loader,
                analyzer,
                loader,
                new[] { "sql" });
        });
        services.AddSingleton<FormatDescriptor>(sp =>
        {
            var loader = sp.GetRequiredService<PlainTextLoader>();
            var analyzer = new NullAnalyzer(SemanticMediaType.Create("text", "plain").WithKind("plain.document"));
            return new FormatDescriptor(
                SemanticMediaType.Create("text", "plain").WithKind("plain.document"),
                loader,
                analyzer,
                loader);
        });

        services.AddSingleton<IFormatRegistry>(sp =>
        {
            var descriptors = sp.GetServices<FormatDescriptor>();
            return new FormatRegistry(descriptors);
        });

        // Register metrics
        services.AddSingleton<IndexingMetrics>();

        // URI registry - tracks file status, embedding status, and symbols for pattern matching
        services.AddUriRegistry();

        // Operations - tracks indexing work batches for observability
        services.AddSingleton<IOperationManager, OperationManager>();

        // Unified database - DuckDbDataStore handles all reads and writes
        services.AddSingleton<DuckDbDataStore>(sp =>
        {
            var scripts = sp.GetServices<IFormatSchemaProvider>()
                .SelectMany(p => p.GetSchemaScripts())
                .Where(s => !string.IsNullOrWhiteSpace(s.Sql))
                .ToList();

            // Use primary embedding provider for embed_text() UDF - ensures query embeddings
            // match document embeddings for consistent similarity comparison in search() macro.
            var db = new DuckDbDataStore(
                dbFileFullPath,
                sp.GetRequiredService<IEmbeddingProvider>(),
                scripts,
                sp.GetService<ILogger<DuckDbDataStore>>(),
                sp,  // Pass IServiceProvider for UDF service resolution
                sp.GetService<DuckDbStartupOptions>());

            return db;
        });

        // MCP client integration - connects to external MCP servers and exposes their tools via SQL
        // Loads from both repo-level configs and global agent configs (Claude Code, Claude Desktop)
        services.AddSingleton(sp =>
        {
            var logger = sp.GetService<ILogger<McpClientRegistry>>();
            var options = McpConfigOptions.FromEnvironment();
            var degradation = sp.GetService<IServiceDegradationTracker>();

            try
            {
                return McpClientRegistry.CreateFromDirectoryWithGlobals(
                    resolvedRoot,
                    includeGlobalAgents: options.IncludeGlobalAgents,
                    selfServerName: options.SelfServerName,
                    logger);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "MCP configuration failed; MCP tools will be disabled.");
                degradation?.MarkDegraded(ServiceDegradationKind.Mcp,
                    $"MCP configuration failed: {ex.Message}");
                return McpClientRegistry.CreateFromConfigs(
                    new Dictionary<string, McpServerConfig>(StringComparer.OrdinalIgnoreCase),
                    selfServerName: options.SelfServerName,
                    logger);
            }
        });
        // Register IMcpToolCaller for UDF resolution - McpClientRegistry implements this interface
        services.AddSingleton<IMcpToolCaller>(sp => sp.GetRequiredService<McpClientRegistry>());
        services.AddHostedService<McpHostedService>();

        // In-memory OTEL sink for dashboards/tests
        services.AddSingleton<InMemoryMetricsSink>(_ => new InMemoryMetricsSink("RepoQL.Indexing"));
        services.AddSingleton<InMemoryRateProvider>(sp => new InMemoryRateProvider(sp.GetRequiredService<InMemoryMetricsSink>()));
        services.AddSingleton<StageMetricsListener>();

        services.AddSingleton<IDocumentCatalogDataSource>(sp => new DuckDbDocumentCatalogDataSource(
            sp.GetRequiredService<DuckDbDataStore>(),
            sp.GetService<ILogger<DuckDbDocumentCatalogDataSource>>()));
        services.AddSingleton<IDocumentCatalog>(sp => new DocumentCatalog(sp.GetRequiredService<IDocumentCatalogDataSource>()));
        services.AddSingleton<IArtifactPruner>(sp =>
        {
            var store = sp.GetRequiredService<DuckDbDataStore>();
            var logger = sp.GetService<ILogger<StorageBackedArtifactPruner>>();
            var coordinatorLazy = new Lazy<IIndexingCoordinator>(() => sp.GetRequiredService<IIndexingCoordinator>());
            Func<bool> isReindexing = () => coordinatorLazy.Value.IsReindexing;
            return new StorageBackedArtifactPruner(store, isReindexing, logger);
        });
        services.AddSingleton<IVectorIndexCoordinator>(sp => new VectorIndexCoordinator(
            sp.GetRequiredService<DuckDbDataStore>(),
            sp.GetRequiredService<IEmbeddingProvider>(),
            sp.GetRequiredService<EmbeddingModeOptions>().Mode,
            sp.GetService<ILogger<VectorIndexCoordinator>>(),
            sp.GetService<UriRegistry>()));
        services.AddSingleton<IIndexingCommitter>(sp => new IndexingCommitter(
            sp.GetRequiredService<DuckDbDataStore>(),
            sp.GetRequiredService<IDocumentCatalog>(),
            sp.GetService<ILogger<IndexingCommitter>>(),
            sp.GetService<UriRegistry>(),
            sp.GetRequiredService<IEmbeddingProvider>(),
            sp.GetRequiredService<EmbeddingModeOptions>().Mode));

        services.AddSingleton<IAsyncPipeline<IClassifiedArtifact, Records?>, CSharpParser>();
        services.AddSingleton<IAsyncPipeline<IClassifiedArtifact, Records?>, CsProjParser>();
        services.AddSingleton<IAsyncPipeline<IClassifiedArtifact, Records?>, SlnParser>();
        // Catch-all parser should run last in the parsing pipeline
        services.AddPlainTextFormat();

        services.AddSingleton(sp => new ClassificationPipeline(
            sp.GetServices<IAsyncPipeline<IDiscoveredArtifact, SemanticMediaType?>>(),
            sp.GetService<ILogger<ClassificationPipeline>>()));
        services.AddSingleton(sp => new ParsingPipeline(
            sp.GetServices<IAsyncPipeline<IClassifiedArtifact, Records?>>(),
            sp.GetService<ILogger<ParsingPipeline>>()));
        services.AddSingleton(sp => new SingleFileAnalysisPipeline(
            sp.GetServices<IAsyncPipeline<IParsedArtifact, Annotation[]>>(),
            sp.GetService<ILogger<SingleFileAnalysisPipeline>>()));
        services.AddSingleton(sp => new MultiFileAnalysisPipeline(
            sp.GetServices<IAsyncPipeline<IAnnotatedArtifact, Annotation[]>>(),
            sp.GetService<ILogger<MultiFileAnalysisPipeline>>()));
        services.AddSingleton(sp => new IndexRebuildPipeline(
            sp.GetServices<IAsyncPipeline<IAnnotatedArtifact, string>>(),
            sp.GetService<ILogger<IndexRebuildPipeline>>()));
        services.AddSingleton<DocumentPreviewService>();

        services.AddSingleton(sp =>
        {
            var engine = new IndexingEngine(
                sp.GetRequiredService<DuckDbDataStore>(),
                sp.GetRequiredService<IUriFilter>(),
                sp.GetRequiredService<ClassificationPipeline>(),
                sp.GetRequiredService<ParsingPipeline>(),
                sp.GetRequiredService<SingleFileAnalysisPipeline>(),
                sp.GetRequiredService<MultiFileAnalysisPipeline>(),
                sp.GetRequiredService<IndexRebuildPipeline>(),
                sp.GetRequiredService<IDocumentCatalog>(),
                sp.GetRequiredService<IIndexingCommitter>(),
                sp.GetRequiredService<IArtifactPruner>(),
                sp.GetRequiredService<IVectorIndexCoordinator>(),
                sp.GetService<IOptions<IndexingEngineOptions>>()?.Value,
                sp.GetService<ILogger<IndexingEngine>>(),
                sp.GetRequiredService<IndexingMetrics>(),
                sp.GetService<UriRegistry>(),
                sp.GetRequiredKeyedService<IEmbeddingProvider>("local"),
                sp.GetRequiredService<EmbeddingModeOptions>().Mode);

            // Set static provider for UDFs (they can't use DI)
            var diagnosticsProvider = new RepoQL.Indexing.Indexing.IndexingEngineDiagnosticsProvider(engine);
            RepoQL.Contracts.Diagnostics.IndexingDiagnostics.SetProvider(diagnosticsProvider);

            return engine;
        });

        // Also register provider in DI for tools that can use it
        services.AddSingleton<RepoQL.Contracts.Diagnostics.IIndexingDiagnosticsProvider>(sp =>
        {
            var engine = sp.GetRequiredService<IndexingEngine>();
            return new RepoQL.Indexing.Indexing.IndexingEngineDiagnosticsProvider(engine);
        });

        // Git history indexer - indexes commits/file changes for code archaeology
        services.AddSingleton<GitHistoryIndexer>();

        services.AddSingleton<IIndexingCoordinator>(sp => new IndexingCoordinator(
            sp.GetRequiredService<CompositeFileSystem>(),
            sp.GetRequiredService<IndexingEngine>(),
            sp.GetRequiredService<DuckDbDataStore>(),
            sp.GetService<ILogger<IndexingCoordinator>>(),
            sp.GetRequiredService<ICompositeFileSystemManager>(),
            sp.GetRequiredService<GitHistoryIndexer>(),
            sp.GetService<IOperationManager>(),
            sp.GetService<UriRegistry>()));

        services.AddSingleton<IVirtualFileSystemImporter>(sp => new GithubRepositoryImporter(
            sp.GetRequiredService<PhysicalFileSystem>(),
            sp.GetRequiredService<DuckDbDataStore>(),
            sp.GetRequiredService<ILogger<GithubRepositoryImporter>>()));
        services.AddSingleton<IVirtualFileSystemImporter>(sp => new LocalDirectoryImporter(
            sp.GetRequiredService<PhysicalFileSystem>(),
            sp.GetRequiredService<DuckDbDataStore>(),
            sp.GetService<ILogger<LocalDirectoryImporter>>()));
        services.AddSingleton<IFileSystemImportService, FileSystemImportService>();

        // Explicit factory needed: ActivatorUtilities doesn't reliably resolve optional parameters
        // after other optional parameters (ILogger? before IIndexingCoordinator?)
        services.AddSingleton(sp => new RepoqlHost(
            sp.GetRequiredService<CompositeFileSystem>(),
            sp.GetRequiredService<IndexingEngine>(),
            sp.GetRequiredService<IOptions<RepoqlHostOptions>>(),
            sp.GetService<ILogger<RepoqlHost>>(),
            sp.GetRequiredService<IIndexingCoordinator>(),
            sp.GetService<IServiceDegradationTracker>(),
            sp.GetService<IOperationManager>(),
            sp.GetService<UriRegistry>()));
        services.AddHostedService(sp => sp.GetRequiredService<RepoqlHost>());

        return services;
    }

    /// <summary>
    ///     Register an additional <c>embed://</c> content store for tests that index embedded resources from an assembly.
    /// </summary>
    /// <param name="services">Service collection.</param>
    /// <param name="assembly">Assembly containing embedded resources.</param>
    /// <param name="scheme">Optional custom scheme for the mount (defaults to <c>embed</c>).</param>
    /// <param name="mountId">Optional mount identifier.</param>
    /// <param name="includeInEnumeration">Whether to enumerate resources from this mount.</param>
    /// <param name="enableWatching">Whether file watching should be enabled.</param>
    public static IServiceCollection AddEmbedStore(
        this IServiceCollection services,
        Assembly assembly,
        string? scheme = null,
        string? mountId = null,
        bool includeInEnumeration = true,
        bool enableWatching = false)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var store = new EmbeddedStore(assembly, scheme);
        services.AddSingleton<IVirtualFileSystem>(store);
        var resolvedId = string.IsNullOrWhiteSpace(mountId)
            ? $"embed:{assembly.GetName().Name}".ToLowerInvariant()
            : mountId;
        services.AddSingleton(new CompositeFileSystemMount
        {
            Id = resolvedId,
            FileSystem = store,
            IncludeInEnumeration = includeInEnumeration,
            EnableWatching = enableWatching,
            UriPredicate = uri => string.Equals(uri.Scheme, store.Scheme, StringComparison.OrdinalIgnoreCase)
        });
        return services;
    }

    private static int GetEmbeddingMaxTokens()
    {
        var maxTokens = 256;
        if (int.TryParse(Environment.GetEnvironmentVariable("REPOQL_EMBED_MAX_TOKENS"), out var parsed) && parsed > 0)
            maxTokens = parsed;
        return maxTokens;
    }

    private static long ResolveSharedCacheSizeLimit()
    {
        const long defaultLimit = 128;
        var raw = Environment.GetEnvironmentVariable("REPOQL_SHARED_CACHE_SIZE_LIMIT");
        if (long.TryParse(raw, out var configured) && configured > 0)
            return configured;
        return defaultLimit;
    }

    private static string? GetEmbeddingModelPath()
        => Environment.GetEnvironmentVariable("REPOQL_EMBED_MODEL_PATH");

    private static (string BaseDir, string ModelDir, string ShippedPath) GetEmbeddingModelPaths()
    {
        var baseDir = AppContext.BaseDirectory;
        var modelDir = Path.Combine(baseDir, "Embeddings", "Model");
        var shipped = Path.Combine(modelDir, "embedding_model.onnx");
        return (baseDir, modelDir, shipped);
    }

    private static OnnxEmbeddingProvider? TryCreateOnnxProvider(
        string path,
        ILogger<OnnxEmbeddingProvider>? logger,
        int maxTokens,
        out Exception? error)
    {
        error = null;
        try
        {
            var onnx = new OnnxEmbeddingProvider(path, logger, maxTokens);
            if (onnx.Enabled)
                return onnx;

            onnx.Dispose();
            return null;
        }
        catch (Exception ex)
        {
            error = ex;
            return null;
        }
    }

    private static void ExtractEmbeddedModelIfAvailable(string destinationModelDir, ILogger? log)
    {
        var entry = System.Reflection.Assembly.GetEntryAssembly();
        if (entry == null)
        {
            log?.LogInformation("Embedding extract: no entry assembly");
            return;
        }

        var resourceNames = entry.GetManifestResourceNames();
        var zipName = resourceNames.FirstOrDefault(n => n.EndsWith("Embeddings.Model.embeddings.zip", StringComparison.OrdinalIgnoreCase));
        if (zipName is null)
        {
            log?.LogInformation("Embedding extract: no embedded model zip found (resource name ends with 'Embeddings.Model.embeddings.zip')");
            return;
        }

        Directory.CreateDirectory(destinationModelDir);
        using var s = entry.GetManifestResourceStream(zipName);
        if (s is null)
        {
            log?.LogWarning("Embedding extract: zip resource stream missing: {Res}", zipName);
            return;
        }

        try
        {
            using var za = new System.IO.Compression.ZipArchive(s, System.IO.Compression.ZipArchiveMode.Read, leaveOpen: true);
            foreach (var entryItem in za.Entries)
            {
                if (string.IsNullOrEmpty(entryItem.Name)) continue; // skip directories
                var dst = Path.Combine(destinationModelDir, entryItem.Name);
                if (File.Exists(dst)) continue;
                using var es = entryItem.Open();
                using var fs = File.Create(dst);
                es.CopyTo(fs);
                log?.LogInformation("Embedding extract: wrote {File}", dst);
            }
        }
        catch (Exception ex)
        {
            log?.LogWarning(ex, "Embedding extract: failed to read embedded model zip");
        }
    }
}
