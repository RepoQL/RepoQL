using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RepoQL.Indexing.Indexing.Pipelines.Discovery;

namespace RepoQL.Indexing.Indexing.Pipelines;

public abstract class PipelinePhase<TInput, TResult> where TInput : IDiscoveredArtifact
{
    public string Name { get; }

    protected readonly UpDownCounter<long> ItemsInFlight;
    protected readonly Counter<long> ItemsProcessed;
    protected readonly Histogram<double> PhaseDuration;
    
    protected ILogger<PipelinePhase<TInput, TResult>> Logger { get; }
    protected IReadOnlyList<IAsyncPipeline<TInput, TResult>> Processors { get; }

    [SuppressMessage("Globalization", "CA1308:Normalize strings to uppercase")]
    protected PipelinePhase(string name, IEnumerable<IAsyncPipeline<TInput, TResult>> processors, 
        ILogger<PipelinePhase<TInput, TResult>>? logger = null)
    {
        Name = name;
        Logger = logger ?? NullLogger<PipelinePhase<TInput, TResult>>.Instance;
        Processors = CreateProcessorList(processors);
        ItemsInFlight = IndexingEngine.Meter.CreateUpDownCounter<long>($"repoql.indexing.{Name.ToLowerInvariant()}.processing");
        ItemsProcessed = IndexingEngine.Meter.CreateCounter<long>($"repoql.indexing.{Name.ToLowerInvariant()}.processed");
        PhaseDuration = IndexingEngine.Meter.CreateHistogram<double>($"repoql.indexing.{Name.ToLowerInvariant()}.duration");
    }

    protected abstract Task ApplyResultAsync(IndexItem item, TResult result,
        CancellationToken cancellationToken = default);
    
    public virtual async Task<PipelineResult> ProcessItemAsync(TInput item, CancellationToken cancellationToken)
    {
        try
        {
            ItemsInFlight.Add(1, new TagList()
            {
                { "item.media_type", item.RawArtifact.ProvisionalMediaType.Value?.ToString() }
            });
            cancellationToken.ThrowIfCancellationRequested();
            if (Logger.IsEnabled(LogLevel.Trace))
            {
                Logger.LogTrace("[{Phase}] Starting processors for {Uri}", Name, item.Uri);
            }

            if (Processors.Count == 0)
                return PipelineResult.Success;

            var (result, status) = await InvokeProcessorAsync(0, item, cancellationToken).ConfigureAwait(false);
            if (status == PipelineResult.Success && result != null)
                await ApplyResultAsync((item as IndexItem)!, result, cancellationToken);
            return status;
        }
        catch (OperationCanceledException)
        {
            return PipelineResult.Cancelled;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "{Uri} failed during the {}", item.Uri);
            return PipelineResult.Error;
        }
        finally
        {
            ItemsInFlight.Add(-1);
            if (Logger.IsEnabled(LogLevel.Trace))
            {
                Logger.LogTrace("[{Phase}] Completed processors for {Uri}", Name, item.Uri);
            }
        }
    }

    private Task<(TResult? Result, PipelineResult PipelineStatus)> InvokeProcessorAsync(int index, TInput item, CancellationToken cancellationToken)
    {
        if (index >= Processors.Count)
            return Task.FromResult((default(TResult), PipelineResult.Success));

        cancellationToken.ThrowIfCancellationRequested();

        var processor = Processors[index];
        var processorName = processor.GetType().Name;
        if (Logger.IsEnabled(LogLevel.Trace))
        {
            Logger.LogTrace("[{Phase}] Executing {Processor} for {Uri}", Name, processorName, item.Uri);
        }

        async Task<(TResult? Result, PipelineResult PipelineStatus)> RunAsync()
        {
            var processorSw = Stopwatch.StartNew();
            var output = await processor.ProcessAsync(
                item,
                nextItem => InvokeProcessorAsync(index + 1, nextItem, cancellationToken),
                cancellationToken).ConfigureAwait(false);
            processorSw.Stop();

            if (processorSw.ElapsedMilliseconds > 50 || Logger.IsEnabled(LogLevel.Trace))
            {
                Logger.LogDebug(
                    "[{Phase}] {Processor} for {Uri}: {ElapsedMs:F1}ms (Status={Status})",
                    Name, processorName, item.Uri,
                    processorSw.Elapsed.TotalMilliseconds, output.PipelineStatus);
            }

            return output;
        }

        return RunAsync();
    }

    private static IReadOnlyList<IAsyncPipeline<TInput, TResult>> CreateProcessorList(IEnumerable<IAsyncPipeline<TInput, TResult>> processors)
    {
        ArgumentNullException.ThrowIfNull(processors);

        if (processors is IReadOnlyList<IAsyncPipeline<TInput, TResult>> readOnlyList)
            return readOnlyList;

        if (processors is List<IAsyncPipeline<TInput, TResult>> list)
            return list;

        return [..processors];
    }
}
