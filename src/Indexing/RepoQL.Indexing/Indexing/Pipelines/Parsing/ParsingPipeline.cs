using Microsoft.Extensions.Logging;
using RepoQL.Contracts.Models;
using RepoQL.Indexing.Indexing.Pipelines.Classification;

namespace RepoQL.Indexing.Indexing.Pipelines.Parsing;

/// <summary>
/// Pipeline stage that reads file content and materializes into graph structure (<see cref="Records"/>).
/// First processor to return non-null wins.
/// </summary>
/// <remarks>
/// <para><strong>Processor Chain</strong></para>
/// <para>
/// Processors registered as <c>IAsyncPipeline&lt;IClassifiedArtifact, Records?&gt;</c>
/// run in sequence. First to return non-null <see cref="Records"/> sets
/// <see cref="IndexItem.Records"/> and stops the chain.
/// </para>
///
/// <para><strong>Records Structure</strong></para>
/// <para>
/// <see cref="Records"/> contains graph data:
/// </para>
/// <list type="bullet">
/// <item><description>Artifacts: Content blobs with xray summaries (headline, summary, structure)</description></item>
/// <item><description>Nodes: Document node + child nodes (headings, functions, etc.)</description></item>
/// <item><description>Spans: Line/character ranges for nodes</description></item>
/// <item><description>Edges: Relationships between nodes (HAS_PART, REFERS_TO, etc.)</description></item>
/// </list>
///
/// <para><strong>Extension Pattern</strong></para>
/// <code>
/// // Add custom parser
/// services.AddSingleton&lt;IAsyncPipeline&lt;IClassifiedArtifact, Records?&gt;, MyParser&gt;();
///
/// class MyParser : IAsyncPipeline&lt;IClassifiedArtifact, Records?&gt; {
///     public async Task&lt;Records?&gt; ProcessAsync(IClassifiedArtifact a, CancellationToken ct) {
///         if (a.MediaType?.BaseType != "application/x-myformat")
///             return null;
///
///         // Load, parse, materialize
///         var records = new Records { Artifacts = [...], Nodes = [...] };
///         return records;
///     }
/// }
/// </code>
/// </remarks>
public class ParsingPipeline(IEnumerable<IAsyncPipeline<IClassifiedArtifact, Records?>> processors, ILogger<ParsingPipeline>? logger = null)
    : PipelinePhase<IClassifiedArtifact, Records?>("Parsing", processors, logger)
{
    protected override Task ApplyResultAsync(IndexItem item, Records? result, CancellationToken cancellationToken = default)
    {
        item.Records = result;
        return Task.CompletedTask;
    }
}