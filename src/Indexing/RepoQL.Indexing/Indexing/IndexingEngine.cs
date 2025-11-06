using System;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using DotNext.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RepoQL.Contracts;
using RepoQL.Contracts.Data;
using RepoQL.FileSystem;
using RepoQL.FileSystem.Abstractions;
using RepoQL.Indexing.Indexing.Pipelines;
using RepoQL.Indexing.Indexing.Pipelines.Analysis;
using RepoQL.Indexing.Indexing.Pipelines.Classification;
using RepoQL.Indexing.Indexing.Pipelines.Parsing;
using RepoQL.Indexing.Indexing.State;

namespace RepoQL.Indexing.Indexing;

public class IndexingEngineOptions
{
    public int IndexingWorkers { get; init; } = Environment.ProcessorCount;
    public int IndexingQueueSize {  get; init; } = 10_000;
    public int AnalysisWorkers { get; init; } = Environment.ProcessorCount;
    public int AnalysisQueueSize {  get; init; } = 100_000;
}

public partial class IndexingEngine
{
    private const string TelemetrySourceName = "RepoQL.Indexing";
    internal static readonly ActivitySource ActivitySource = new(TelemetrySourceName);
    internal static readonly Meter Meter = new(TelemetrySourceName);
    
    public ClassificationPipeline Classifier { get; }
    public ParsingPipeline Parser { get; }
    public SingleFileAnalysisPipeline SingleFileAnalyzer { get; }
    public MultiFileAnalysisPipeline MultiFileAnalyzer { get; }
    public IndexRebuildPipeline IndexRebuilder { get; }

    private IndexingEngineOptions Options { get; }
    private ILogger<IndexingEngine> Logger { get; }
    private CancellationTokenSource Shutdown { get; } = new();
    private IDocumentCatalog DocumentCatalog { get; }

    public async Task EnqueueItemAsync(RawArtifact artifact, IndexItemOptions options = IndexItemOptions.Default, CancellationToken cancellationToken = default)
    {
        await IndexerQueue.EnqueueAsync(new IndexItem(artifact, options), cancellationToken);
    }

    private WorkQueue<IndexItem> IndexerQueue { get; }

    public IndexingEngine(
        IDatabaseWriter? databaseWriter,
        IUriFilter? filter,
        ClassificationPipeline? classifier = null, 
        ParsingPipeline? parser = null, 
        SingleFileAnalysisPipeline? singleFileAnalyzer = null, 
        MultiFileAnalysisPipeline? multiFileAnalyzer = null, 
        IndexRebuildPipeline? indexRebuilder = null,
        IDocumentCatalog? documentCatalog = null,
        IndexingEngineOptions? options = null,
        ILogger<IndexingEngine>? logger = null)
    {
        Writer =  databaseWriter;
        Filter = filter ?? new RepoGitIgnoreFilter(".");
        Classifier = classifier ?? new ClassificationPipeline( []);
        Parser = parser ?? new ParsingPipeline([]);
        SingleFileAnalyzer = singleFileAnalyzer ?? new SingleFileAnalysisPipeline([]);
        MultiFileAnalyzer = multiFileAnalyzer ??  new MultiFileAnalysisPipeline([]);
        IndexRebuilder = indexRebuilder ?? new IndexRebuildPipeline([]);
        DocumentCatalog = documentCatalog ?? new DocumentCatalog(NullDocumentCatalogDataSource.Instance);
        Options = options ??  new IndexingEngineOptions();
        Logger = logger ?? NullLogger<IndexingEngine>.Instance;
        IndexerQueue = new WorkQueue<IndexItem>(
            "IndexingQueue",
            Options.IndexingQueueSize,
            Options.IndexingWorkers,
            async (item, c) =>
            {
                await IndexItemAsync(item, c);
            }, Shutdown.Token);
        AnalysisQueue = new WorkQueue<IndexItem>(
            "AnalysisQueue",
            Options.AnalysisQueueSize,
            Options.AnalysisWorkers,
            async (item, c) =>
            {
                await AnalyzeItemAsync(item, c);
            }, Shutdown.Token);
    }

    public IUriFilter Filter { get; }

    public IDatabaseWriter? Writer { get; }

    public WorkQueue<IndexItem> AnalysisQueue { get; }

    internal async Task IndexItemAsync(IndexItem item, CancellationToken cancellationToken = default)
    {
        // ReSharper disable once ExplicitCallerInfoArgument
        using var activity = ActivitySource.StartActivity(ActivityKind.Internal, name: "Index", tags: new TagList
        {
            { "item.name", item.Name },
            { "item.uri", item.Uri.ToString() },
            { "item.media_type", item.MediaType },
            { "item.last_modified", item.LastModified.ToString() },
            { "item.provisional_media_type", item.RawArtifact.ProvisionalMediaType }
        });
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item.Options.HasFlag(IndexItemOptions.OnlyIfNotExcluded) && Filter.IncludeFile(item.Uri))
            {
                RecordResult(PipelineResult.Filtered);
                return;
            }
            await DocumentCatalog.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
            var digestBytes = await item.RawArtifact.Digest.WithCancellation(cancellationToken).ConfigureAwait(false);
            var digestHex = Convert.ToHexString(digestBytes);
            item.DigestHex = digestHex;

            var evaluation = DocumentCatalog.Evaluate(item.Uri, digestHex);
            item.ExistingEntry = evaluation.Existing;
            Activity.Current?.AddTag("index.catalog.decision", evaluation.Decision.ToString());

            if (item.Options.HasFlag(IndexItemOptions.OnlyIfStale) &&
                evaluation.Decision == DocumentCatalogDecision.SkipUpToDate)
            {
                Activity.Current?.AddTag("index.catalog", "skip_up_to_date");
                RecordResult(PipelineResult.Filtered);
                return;
            }

            var inFlightRegistered = false;
            try
            {
                DocumentCatalog.BeginProcessing(item.Uri, digestHex);
                inFlightRegistered = true;

                var result = await ApplyIndexerPipeline(item, cancellationToken);
                RecordResult(result);
                if (result != PipelineResult.Success)
                    return;
                // TODO - Save records + annotations
                // NOTE: Once WriteOperation dispatch is in place, hook DocumentCatalog.ApplyUpsert/Delete
                //       through the writer's OnCommitted callback to keep the cache authoritative.
            }
            finally
            {
                if (inFlightRegistered)
                    DocumentCatalog.CompleteProcessing(item.Uri);
            }
        }
        catch (OperationCanceledException)
        {
            LogIndexingCancelledForItem(Logger, item.Name);
            return;
        }
        catch (Exception ex)
        {
            LogUriFailedDuringIndexing(Logger, ex, item.Uri);
            return;
        }
        Activity.Current?.AddTag("result", "Success");
    }

    private static void RecordResult(PipelineResult result)
    {
        Activity.Current?.AddTag("index.result", result);
    }

    internal async Task<PipelineResult> ApplyIndexerPipeline(IndexItem item, CancellationToken cancellationToken)
    {
        var pipelineResult = await Classifier.ProcessItemAsync(item, cancellationToken);
        if (pipelineResult != PipelineResult.Success)
            return  pipelineResult;
        pipelineResult = await Parser.ProcessItemAsync(item, cancellationToken);
        if (pipelineResult != PipelineResult.Success)
            return pipelineResult;
        return await SingleFileAnalyzer.ProcessItemAsync(item, cancellationToken);
    }

    

    internal async Task AnalyzeItemAsync(IndexItem item, CancellationToken cancellationToken)
    {
        // ReSharper disable once ExplicitCallerInfoArgument
        using var activity = ActivitySource.StartActivity(ActivityKind.Internal, name: "Analyze", tags: new TagList
        {
            { "item.name", item.Name },
            { "item.uri", item.Uri.ToString() },
            { "item.media_type", item.MediaType },
            { "item.last_modified", item.LastModified.ToString() }
        });
        try
        {
            await Task.WhenAll(MultiFileAnalyzer.ProcessItemAsync(item, cancellationToken), 
                IndexRebuilder.ProcessItemAsync(item, cancellationToken));
        }
        catch (OperationCanceledException)
        {
            LogAnalysisCancelledForItem(Logger, item.Name);
            return;
        }
        catch (Exception ex)
        {
            LogUriFailedDuringAnalysis(Logger, ex, item.Uri);
            return;
        }
        Activity.Current?.AddTag("result", "Success");
    }
    
    public IndexingState State { get; private set; }

    public ValueTask<bool> WaitForAsync(IndexingState state, CancellationToken cancellationToken)
    {
        return new ValueTask<bool>(true);
    }
    
    #region Logging
    [LoggerMessage(LogLevel.Warning, "Indexing cancelled for {item}")]
    static partial void LogIndexingCancelledForItem(ILogger<IndexingEngine> logger, string item);

    [LoggerMessage(LogLevel.Error, "{Uri} failed during indexing")]
    static partial void LogUriFailedDuringIndexing(ILogger<IndexingEngine> logger, Exception ex, RepoUri uri);

    [LoggerMessage(LogLevel.Warning, "Analysis cancelled for {item}")]
    static partial void LogAnalysisCancelledForItem(ILogger<IndexingEngine> logger, string item);

    [LoggerMessage(LogLevel.Error, "{Uri} failed during analysis")]
    static partial void LogUriFailedDuringAnalysis(ILogger<IndexingEngine> logger, Exception ex, RepoUri uri);
    #endregion
}
