using System.Diagnostics.Metrics;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RepoQL.Contracts;
using RepoQL.Contracts.Data;
using RepoQL.Contracts.Embeddings;
using RepoQL.Core.Analysis;
using RepoQL.Core.Analysis.EditorConfig;
using RepoQL.Core.Embeddings;
using RepoQL.Core.Metrics;
using RepoQL.Metrics;
using RepoQL.Data.DuckDB;
using RepoQL.FileSystem;
using RepoQL.FileSystem.Abstractions;
using RepoQL.FileSystem.Classification;
using RepoQL.FileSystem.Embedded;
using RepoQL.FileSystem.Physical;
using RepoQL.Formats.DotNet;
using RepoQL.Formats.Markdown;
using RepoQL.Formats.Mermaid;
using RepoQL.Formats.GraphQL;
using RepoQL.Templating;

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
        services.AddSingleton<IFileSystemRegistry>(sp => new FileSystemRegistry(sp.GetServices<IVirtualFileSystem>()));
        services.AddSingleton<IMultiFileSystem>(sp => new MultiFileSystem(
            sp.GetRequiredService<IFileSystemRegistry>(),
            sp.GetServices<IVirtualFileSystem>()
        ));
        services.AddSingleton<IUriFilter>(_ => new RepoGitIgnoreFilter(resolvedRoot, [dbRelPath]));
        services.AddSingleton<IHasher, XxHasher>();

        // DuckDB connection string uses the repo root + the repo-relative DB path
        var cs = RepoIndexingBootstrap.DuckDbConnectionString(resolvedRoot, dbRelPath);
        var dbFileFullPath = Path.Combine(resolvedRoot, dbRelPath);

        // Provide a connection factory for components that need fresh connections
        services.AddSingleton<IDuckDBConnectionFactory>(_ => new DuckDBConnectionFactory(cs));

        // Single-writer for all write operations
        services.AddSingleton<IDatabaseWriter, SingleThreadedDatabaseWriter>();
        services.AddHostedService(sp => (SingleThreadedDatabaseWriter)sp.GetRequiredService<IDatabaseWriter>());

        // Embeddings provider (local only). Enabled by default; disable via REPOQL_EMBED_ENABLED=0
        services.AddSingleton<IEmbeddingProvider>(sp =>
        {
            var lf = sp.GetService<ILoggerFactory>();
            var log = lf?.CreateLogger("RepoQL.Embeddings");
            var disabled = string.Equals(Environment.GetEnvironmentVariable("REPOQL_EMBED_ENABLED"), "0", StringComparison.Ordinal);
            if (disabled)
            {
                log?.LogInformation("Embedding provider: disabled via REPOQL_EMBED_ENABLED=0");
                return new DisabledEmbeddingProvider();
            }

            // Prefer explicit ONNX model path via env
            var onnxPath = Environment.GetEnvironmentVariable("REPOQL_EMBED_MODEL_PATH");
            var maxTokens = 256;
            if (int.TryParse(Environment.GetEnvironmentVariable("REPOQL_EMBED_MAX_TOKENS"), out var mt) && mt > 0) maxTokens = mt;
            if (!string.IsNullOrWhiteSpace(onnxPath) && File.Exists(onnxPath))
            {
                var onnx = new OnnxEmbeddingProvider(onnxPath, sp.GetService<ILogger<OnnxEmbeddingProvider>>()!, maxTokens);
                if (onnx.Enabled) return onnx;
                onnx.Dispose();
                log?.LogWarning("Embedding provider: ONNX failed to initialize from explicit path {Path}; falling back", onnxPath);
            }

            // Otherwise, attempt to load the shipped model placed by RepoQL project: Embeddings/Model/embedding_model.onnx
            // If not present, extract from embedded resources in the entry assembly on first run.
            try
            {
                var baseDir = AppContext.BaseDirectory;
                var modelDir = Path.Combine(baseDir, "Embeddings", "Model");
                var shipped = Path.Combine(modelDir, "embedding_model.onnx");

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
                    log?.LogInformation("Embedding provider: using shipped model at {Path}", shipped);
                    var onnx = new OnnxEmbeddingProvider(shipped, sp.GetService<ILogger<OnnxEmbeddingProvider>>()!, maxTokens);
                    if (onnx.Enabled) return onnx;
                    onnx.Dispose();
                    log?.LogWarning("Embedding provider: shipped model failed to initialize; falling back");
                }
            }
            catch (Exception ex)
            {
                log?.LogWarning(ex, "Embedding provider: embedding failed to initialize");
                // swallow and fall back to hashed provider
            }

            // Fallback: hashed provider (deterministic, lightweight)
            var dimEnv = Environment.GetEnvironmentVariable("REPOQL_EMBED_DIM");
            var dim = 384;
            if (int.TryParse(dimEnv, out var parsed) && parsed > 0) dim = parsed;
            log?.LogInformation("Embedding provider: using hashed fallback with dim={Dim}", dim);
            return new HashedEmbeddingProvider(dim);
        });

        services.AddSingleton<IAnalysisResultWriter, AnnotationResultWriter>();
        services.AddSingleton<IAnalyzerSettingsProvider>(_ => new EditorConfigSettingsProvider(resolvedRoot));

        // Templating for x-ray summaries (embedded defaults)
        services.AddLiquidTemplatingFromEmbedded(
            assembly: typeof(MarkdownLoader).Assembly,
            resourceRoot: "RepoQL.Formats.Markdown.Templates");
        services.AddLiquidTemplatingFromEmbedded(
            assembly: typeof(CsProjLoader).Assembly,
            resourceRoot: "RepoQL.Formats.DotNet.Templates");

        services.AddSingleton<MarkdownAnalyzer>();
        services.AddSingleton<MarkdownLoader>(sp => new MarkdownLoader());
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
        services.AddSingleton<CSharpAnalyzer>();
        services.AddSingleton<PlainTextLoader>();
        services.AddSingleton<GraphQLLoader>();
        services.AddSingleton<GraphQLAnalyzer>();
        services.AddSingleton<CsProjLoader>(sp => new CsProjLoader(sp.GetRequiredService<ITemplateRenderer>()));

        services.AddSingleton<IFormatRegistry>(sp =>
        {
            var markdownLoader = sp.GetRequiredService<MarkdownLoader>();
            var markdownAnalyzer = sp.GetRequiredService<MarkdownAnalyzer>();
            var mermaidLoader = sp.GetRequiredService<MermaidLoader>();
            var mermaidAnalyzer = sp.GetRequiredService<MermaidAnalyzer>();
            var csprojLoader = sp.GetRequiredService<CsProjLoader>();
            var csprojAnalyzer = sp.GetRequiredService<CsProjAnalyzer>();
            var slnLoader = sp.GetRequiredService<SlnLoader>();
            var slnAnalyzer = new NullAnalyzer(SemanticMediaType.Create("text", "plain").WithKind("dotnet.sln"));
            var csharpLoader = sp.GetRequiredService<CSharpLoader>();
            var csharpAnalyzer = sp.GetRequiredService<CSharpAnalyzer>();
            var graphQlLoader = sp.GetRequiredService<GraphQLLoader>();
            var graphQlAnalyzer = sp.GetRequiredService<GraphQLAnalyzer>();
            var plainLoader = sp.GetRequiredService<PlainTextLoader>();
            var plainAnalyzer = new NullAnalyzer(SemanticMediaType.Create("text", "plain").WithKind("plain.document"));

            var descriptors = new[]
            {
                // Specific formats first
                new FormatDescriptor(
                    SemanticMediaType.Create("text", "markdown").WithKind("markdown.doc"),
                    markdownLoader,
                    markdownAnalyzer,
                    markdownLoader,
                    ["markdown"]),
                new FormatDescriptor(
                    SemanticMediaType.Create("text", "mermaid").WithKind("mermaid.doc"),
                    mermaidLoader,
                    mermaidAnalyzer,
                    mermaidLoader,
                    ["mermaid", "mmd"]),
                new FormatDescriptor(
                    SemanticMediaType.Create("text", "graphql").WithKind("graphql.doc"),
                    graphQlLoader,
                    graphQlAnalyzer,
                    graphQlLoader,
                    ["graphql", "gql"]),
                new FormatDescriptor(
                    SemanticMediaType.Create("text", "xml").WithKind("dotnet.csproj"),
                    csprojLoader,
                    csprojAnalyzer,
                    csprojLoader,
                    ["csproj"]),
                new FormatDescriptor(
                    SemanticMediaType.Create("text", "plain").WithKind("dotnet.sln"),
                    slnLoader,
                    slnAnalyzer,
                    slnLoader,
                    ["sln"]),
                new FormatDescriptor(
                    SemanticMediaType.Create("text", "plain").WithKind("code.csharp"),
                    csharpLoader,
                    csharpAnalyzer,
                    csharpLoader,
                    ["csharp", "cs"]),
                // Catch‑all plain last so it doesn't shadow specific handlers
                new FormatDescriptor(
                    SemanticMediaType.Create("text", "plain").WithKind("plain.document"),
                    plainLoader,
                    plainAnalyzer,
                    plainLoader)
            };

            return new FormatRegistry(descriptors);
        });

        services.AddSingleton<IAnalysisWorkspace>(sp => new AnalysisWorkspace(
            sp.GetRequiredService<IMultiFileSystem>(),
            sp.GetRequiredService<IFileClassifier>(),
            sp.GetRequiredService<IHasher>(),
            sp.GetRequiredService<IFormatRegistry>()));

        // Register metrics
        services.AddSingleton<IndexingMetrics>();

        services.AddSingleton<IGraphStore>(sp =>
        {
            var formatRegistry = sp.GetRequiredService<IFormatRegistry>();
            var scripts = formatRegistry.Formats
                .SelectMany(f => f.Loader.GetSchemaScripts())
                .Where(s => !string.IsNullOrWhiteSpace(s.Sql))
                .ToList();

            return new DuckDbGraphStore(
                dbFileFullPath,
                sp.GetRequiredService<IndexingMetrics>(),
                logger: sp.GetService<ILogger<DuckDbGraphStore>>(),
                embeddingProvider: sp.GetRequiredService<IEmbeddingProvider>(),
                formatSchemaScripts: scripts);
        });
        // In-memory OTEL sink for dashboards/tests
        services.AddSingleton<InMemoryMetricsSink>(_ => new InMemoryMetricsSink("RepoQL.Indexing"));
        services.AddSingleton<InMemoryRateProvider>(sp => new InMemoryRateProvider(sp.GetRequiredService<InMemoryMetricsSink>()));

        // Register RepositoryIndexer as both the concrete type and IRepositoryIndexer interface
        services.AddSingleton<RepositoryIndexer>(sp => ActivatorUtilities.CreateInstance<RepositoryIndexer>(sp, resolvedRoot));
        services.AddSingleton<IRepositoryIndexer>(sp => sp.GetRequiredService<RepositoryIndexer>());
        services.AddHostedService<IRepositoryIndexer>(sp => sp.GetRequiredService<IRepositoryIndexer>());

        return services;
    }

    /// <summary>
    ///     Register an additional <c>embed://</c> content store for tests that index embedded resources from an assembly.
    /// </summary>
    /// <param name="services">Service collection.</param>
    /// <param name="assembly">Assembly containing embedded resources.</param>
    public static IServiceCollection AddEmbedStore(this IServiceCollection services, Assembly assembly)
    {
        services.AddSingleton<IVirtualFileSystem>(_ => new EmbeddedStore(assembly));
        return services;
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
