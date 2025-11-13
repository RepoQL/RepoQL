using Microsoft.Extensions.Logging;
using RepoQL.Contracts;
using RepoQL.Indexing.Indexing.Pipelines.Discovery;

namespace RepoQL.Indexing.Indexing.Pipelines.Classification;

/// <summary>
/// Pipeline stage that determines file media type by running classification processors.
/// First processor to return non-null wins.
/// </summary>
/// <remarks>
/// <para><strong>Processor Chain</strong></para>
/// <para>
/// Processors registered as <c>IAsyncPipeline&lt;IDiscoveredArtifact, SemanticMediaType?&gt;</c>
/// run in sequence. First to return non-null <see cref="SemanticMediaType"/> sets
/// <see cref="IndexItem.MediaType"/> and stops the chain.
/// </para>
///
/// <para><strong>Extension Pattern</strong></para>
/// <code>
/// // Add custom classifier
/// services.AddSingleton&lt;IAsyncPipeline&lt;IDiscoveredArtifact, SemanticMediaType?&gt;, MyClassifier&gt;();
///
/// class MyClassifier : IAsyncPipeline&lt;IDiscoveredArtifact, SemanticMediaType?&gt; {
///     public Task&lt;SemanticMediaType?&gt; ProcessAsync(IDiscoveredArtifact a, CancellationToken ct) {
///         if (a.Name.EndsWith(".xyz"))
///             return Task.FromResult(SemanticMediaType.Parse("application/x-xyz"));
///         return Task.FromResult&lt;SemanticMediaType?&gt;(null);
///     }
/// }
/// </code>
///
/// <para><strong>Fallback</strong></para>
/// <para>
/// If all processors return null, uses <see cref="RawArtifact.ProvisionalMediaType"/>
/// (guessed from file extension) as fallback.
/// </para>
/// </remarks>
public class ClassificationPipeline(IEnumerable<IAsyncPipeline<IDiscoveredArtifact, SemanticMediaType?>> processors, ILogger<ClassificationPipeline>? logger = null)
    : PipelinePhase<IDiscoveredArtifact, SemanticMediaType?>("Classification", processors, logger)
{
    protected override Task ApplyResultAsync(IndexItem item, SemanticMediaType? result, CancellationToken cancellationToken = default)
    {
        item.MediaType = result;
        return Task.CompletedTask;
    }
}