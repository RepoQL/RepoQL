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
    private readonly IReadOnlyList<IAsyncPipeline<TInput, TResult>> _processors;

    [SuppressMessage("Globalization", "CA1308:Normalize strings to uppercase")]
    protected PipelinePhase(string name, IEnumerable<IAsyncPipeline<TInput, TResult>> processors, 
        ILogger<PipelinePhase<TInput, TResult>>? logger = null)
    {
        Name = name;
        Logger = logger ?? NullLogger<PipelinePhase<TInput, TResult>>.Instance;
        _processors = CreateProcessorList(processors);
        ItemsInFlight = IndexingEngine.Meter.CreateUpDownCounter<long>($"repoql.indexing.{Name.ToLowerInvariant()}.processing");
        ItemsProcessed = IndexingEngine.Meter.CreateCounter<long>($"repoql.indexing.{Name.ToLowerInvariant()}.processed");
        PhaseDuration = IndexingEngine.Meter.CreateHistogram<double>($"repoql.indexing.{Name.ToLowerInvariant()}.duration");
    }

    protected abstract Task ApplyResultAsync(IndexItem item, TResult result,
        CancellationToken cancellationToken = default);
    
    public virtual async Task<PipelineResult> ProcessItemAsync(TInput item, CancellationToken cancellationToken)
    {
        using var activity = IndexingEngine.ActivitySource.StartActivity(ActivityKind.Internal, name: $"Indexer.{Name}", tags: new TagList
        {
            { "phase", Name },
            { "item.name", item.Name },
            { "item.uri", item.Uri.ToString() },
            { "item.last_modified", item.LastModified.ToString() },
            { "item.provisional_media_type", item.RawArtifact.ProvisionalMediaType }
        });
        try
        {
            ItemsInFlight.Add(1, new TagList()
            {
                { "item.media_type", item.RawArtifact.ProvisionalMediaType.Value?.ToString() }
            });
            cancellationToken.ThrowIfCancellationRequested();

            if (_processors.Count == 0)
                return PipelineResult.Success;

            var (result, status) = await InvokeProcessorAsync(0, item, cancellationToken).ConfigureAwait(false);
            if (status == PipelineResult.Success && result != null)
                await ApplyResultAsync((item as IndexItem)!, result, cancellationToken);
            return PipelineResult.Success;
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
        }
    }

    private Task<(TResult? Result, PipelineResult PipelineStatus)> InvokeProcessorAsync(int index, TInput item, CancellationToken cancellationToken)
    {
        if (index >= _processors.Count)
            return Task.FromResult((default(TResult), PipelineResult.Success));

        cancellationToken.ThrowIfCancellationRequested();

        var processor = _processors[index];
        return processor.ProcessAsync(
            item,
            nextItem => InvokeProcessorAsync(index + 1, nextItem, cancellationToken),
            cancellationToken);
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
