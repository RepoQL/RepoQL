using RepoQL.Indexing.Indexing.Pipelines.Discovery;

namespace RepoQL.Indexing.Indexing.Pipelines;

public delegate Task<(TResult? Result, PipelineResult PipelineStatus)> CallNextPipeline<in TInput, TResult>(TInput item);

public interface IAsyncPipeline<TInput, TResult> where TInput : IDiscoveredArtifact
{
    public Task<(TResult? Result, PipelineResult PipelineStatus)> ProcessAsync(TInput item, CallNextPipeline<TInput, TResult> next, CancellationToken token);
}