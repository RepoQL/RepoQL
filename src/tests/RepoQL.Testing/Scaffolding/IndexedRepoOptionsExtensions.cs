using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RepoQL.Contracts;
using RepoQL.Contracts.Analysis;
using RepoQL.Contracts.Models;
using RepoQL.Core;
using RepoQL.Core.Analysis;
using RepoQL.Formats.Markdown;
using RepoQL.Formats.Mermaid;
using RepoQL.Formats.DotNet;
using RepoQL.Indexing.Indexing.Pipelines;
using RepoQL.Indexing.Indexing.Pipelines.Parsing;

namespace RepoQL.Testing.Scaffolding;

/// <summary>
/// Extension methods for adding format support to IndexedRepoOptions.
/// </summary>
public static class IndexedRepoOptionsExtensions
{
    /// <summary>
    /// Adds Markdown format support.
    /// </summary>
    public static IndexedRepoOptions AddMarkdownFormat(
        this IndexedRepoOptions options,
        string? repositoryRoot = null,
        IAnalyzerSettingsProvider? settingsProvider = null)
    {
        var loader = new MarkdownLoader();
        var analyzer = new MarkdownAnalyzer();

        options.AddParser(new MarkdownParser(loader));
        options.AddAnalyzer(new AnalyzerPipelineAdapter(
            analyzer,
            repositoryRoot ?? Directory.GetCurrentDirectory(),
            settingsProvider));
        options.AddSchemaProvider(loader);
        options.MapExtension("md", SemanticMediaType.Create("text", "markdown").WithKind("markdown.doc"));
        options.MapExtension("markdown", SemanticMediaType.Create("text", "markdown").WithKind("markdown.doc"));
        options.MapExtension("mdown", SemanticMediaType.Create("text", "markdown").WithKind("markdown.doc"));
        options.MapExtension("mkd", SemanticMediaType.Create("text", "markdown").WithKind("markdown.doc"));

        return options;
    }

    /// <summary>
    /// Adds Mermaid format support.
    /// </summary>
    public static IndexedRepoOptions AddMermaidFormat(this IndexedRepoOptions options)
    {
        var loader = new MermaidLoader();

        options.AddParser(new MermaidParser(loader));
        // MermaidLoader doesn't have custom SQL views
        options.MapExtension("mermaid", SemanticMediaType.Create("text", "mermaid").WithKind("mermaid.doc"));
        options.MapExtension("mmd", SemanticMediaType.Create("text", "mermaid").WithKind("mermaid.doc"));

        return options;
    }

    /// <summary>
    /// Adds C# format support.
    /// </summary>
    public static IndexedRepoOptions AddCSharpFormat(
        this IndexedRepoOptions options,
        string? repositoryRoot = null,
        IAnalyzerSettingsProvider? settingsProvider = null)
    {
        var loader = new CSharpLoader();
        var analyzer = new CSharpAnalyzer();

        options.AddParser(new CSharpParser(loader));
        options.AddAnalyzer(new AnalyzerPipelineAdapter(
            analyzer,
            repositoryRoot ?? Directory.GetCurrentDirectory(),
            settingsProvider));
        options.AddSchemaProvider(loader);
        options.MapExtension("cs", SemanticMediaType.Create("text", "x-csharp").WithKind(CSharpLoader.MediaKind));

        return options;
    }

    /// <summary>
    /// Adds .sln format support.
    /// </summary>
    public static IndexedRepoOptions AddSlnFormat(this IndexedRepoOptions options)
    {
        var loader = new SlnLoader();

        options.AddParser(new SlnParser(loader));
        // SlnLoader doesn't have custom SQL views
        options.MapExtension("sln", SemanticMediaType.Create("text", "plain").WithKind("dotnet.sln"));

        return options;
    }

    /// <summary>
    /// Adds .csproj format support.
    /// </summary>
    public static IndexedRepoOptions AddCsProjFormat(this IndexedRepoOptions options)
    {
        var loader = new CsProjLoader();

        options.AddParser(new CsProjParser(loader));
        // CsProjLoader doesn't have custom SQL views
        options.MapExtension("csproj", SemanticMediaType.Create("application", "xml").WithKind("dotnet.csproj"));

        return options;
    }

}

/// <summary>
/// Adapts an IFormatAnalyzer to work as a pipeline processor.
/// </summary>
internal sealed class AnalyzerPipelineAdapter : IAsyncPipeline<IParsedArtifact, Annotation[]>
{
    private readonly IFormatAnalyzer _analyzer;
    private readonly string _repositoryRoot;
    private readonly IAnalyzerSettingsProvider? _settingsProvider;
    private readonly ILogger<AnalyzerPipelineAdapter> _logger;

    public AnalyzerPipelineAdapter(
        IFormatAnalyzer analyzer,
        string repositoryRoot,
        IAnalyzerSettingsProvider? settingsProvider = null,
        ILogger<AnalyzerPipelineAdapter>? logger = null)
    {
        _analyzer = analyzer ?? throw new ArgumentNullException(nameof(analyzer));
        _repositoryRoot = repositoryRoot ?? Directory.GetCurrentDirectory();
        _settingsProvider = settingsProvider;
        _logger = logger ?? NullLogger<AnalyzerPipelineAdapter>.Instance;
    }

    public async Task<(Annotation[]? Result, PipelineResult PipelineStatus)> ProcessAsync(
        IParsedArtifact item,
        CallNextPipeline<IParsedArtifact, Annotation[]> next,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();

        var media = item.MediaType;
        if (media is null || !_analyzer.Supports(media))
        {
            return await next(item).ConfigureAwait(false);
        }

        if (!item.TryGetValue("document_model", out var documentModel) || documentModel is not DocumentModel document)
        {
            return await next(item).ConfigureAwait(false);
        }

        if (item.Records is null)
        {
            return await next(item).ConfigureAwait(false);
        }

        var documentNode = item.Records.Nodes.FirstOrDefault(n =>
            string.Equals(n.Kind, "document", StringComparison.OrdinalIgnoreCase));
        if (documentNode is null)
        {
            _logger.LogWarning("Document node missing for {Uri}; skipping analyzer", item.Uri);
            return await next(item).ConfigureAwait(false);
        }

        try
        {
            var settings = _settingsProvider?.Resolve(item.Uri.AbsoluteUri, media, documentNode)
                ?? new AnalyzerSettings();
            var context = new AnalyzerContext(settings, _repositoryRoot);
            var annotations = new List<Annotation>();

            await foreach (var result in _analyzer.AnalyzeAsync(document, context, token).ConfigureAwait(false))
            {
                annotations.Add(new Annotation
                {
                    SemanticKey = result.SemanticKey,
                    Kind = result.Kind,
                    Severity = result.Severity.ToString().ToLowerInvariant(),
                    Source = result.Source,
                    RuleId = result.RuleId,
                    Message = result.Message,
                    Data = result.Data ?? new JsonObject(),
                    ScopeDocumentId = documentNode.Id,
                    TargetNodeId = result.Target?.NodeId,
                    TargetEdgeId = result.Target?.EdgeId,
                    TargetSpanId = result.Target?.SpanId,
                    TargetUri = result.Target?.TargetUri
                });
            }

            return (annotations.ToArray(), PipelineResult.Success);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Analyzer failed for {Uri}", item.Uri);
            return (null, PipelineResult.Error);
        }
    }
}
