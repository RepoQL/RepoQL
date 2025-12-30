using AwesomeAssertions;
using RepoQL.Indexing.Indexing.Pipelines;
using RepoQL.Indexing.Indexing.Pipelines.Discovery;
using RepoQL.Testing.Indexing;

namespace RepoQL.Indexing.Tests.Indexing;

internal class PipelinePhaseTests
{
    [Test]
    [Arguments(PipelineResult.Filtered)]
    [Arguments(PipelineResult.Cancelled)]
    [Arguments(PipelineResult.Error)]
    [DisplayName("Propagates non-success results and skips ApplyResult")]
    public async Task Given_ProcessorReturnsNonSuccess_When_ProcessItemAsync_Then_PropagatesAndSkipsApply(PipelineResult result)
    {
        var item = IndexingTestItemBuilder.ForMarkdown().WithContent("text").Build();
        var processor = new FixedResultProcessor("value", result);
        var phase = new TestPhase(new[] { processor });

        var status = await phase.ProcessItemAsync(item, CancellationToken.None);

        status.Should().Be(result);
        phase.Applied.Should().BeFalse("non-success results must not apply output");
    }

    [Test]
    [DisplayName("Delegates to next processor and propagates its result")]
    public async Task Given_ProcessorCallsNext_When_NextReturnsFiltered_Then_PropagatesFiltered()
    {
        var item = IndexingTestItemBuilder.ForMarkdown().WithContent("text").Build();
        var phase = new TestPhase(new IAsyncPipeline<IDiscoveredArtifact, string>[]
        {
            new DelegatingProcessor(),
            new FixedResultProcessor("value", PipelineResult.Filtered)
        });

        var status = await phase.ProcessItemAsync(item, CancellationToken.None);

        status.Should().Be(PipelineResult.Filtered);
        phase.Applied.Should().BeFalse("filtered items should not apply stage output");
    }

    [Test]
    [DisplayName("Applies result when processor succeeds")]
    public async Task Given_ProcessorReturnsSuccess_When_ProcessItemAsync_Then_AppliesResult()
    {
        var item = IndexingTestItemBuilder.ForMarkdown().WithContent("text").Build();
        var processor = new FixedResultProcessor("value", PipelineResult.Success);
        var phase = new TestPhase(new[] { processor });

        var status = await phase.ProcessItemAsync(item, CancellationToken.None);

        status.Should().Be(PipelineResult.Success);
        phase.Applied.Should().BeTrue();
        phase.AppliedValue.Should().Be("value");
    }

    private sealed class TestPhase : PipelinePhase<IDiscoveredArtifact, string>
    {
        public TestPhase(IEnumerable<IAsyncPipeline<IDiscoveredArtifact, string>> processors)
            : base("TestPhase", processors)
        {
        }

        public bool Applied { get; private set; }
        public string? AppliedValue { get; private set; }

        protected override Task ApplyResultAsync(IndexItem item, string result, CancellationToken cancellationToken = default)
        {
            Applied = true;
            AppliedValue = result;
            return Task.CompletedTask;
        }
    }

    private sealed class FixedResultProcessor : IAsyncPipeline<IDiscoveredArtifact, string>
    {
        private readonly string? _result;
        private readonly PipelineResult _status;

        public FixedResultProcessor(string? result, PipelineResult status)
        {
            _result = result;
            _status = status;
        }

        public Task<(string? Result, PipelineResult PipelineStatus)> ProcessAsync(
            IDiscoveredArtifact item,
            CallNextPipeline<IDiscoveredArtifact, string> next,
            CancellationToken token)
        {
            return Task.FromResult((_result, _status));
        }
    }

    private sealed class DelegatingProcessor : IAsyncPipeline<IDiscoveredArtifact, string>
    {
        public Task<(string? Result, PipelineResult PipelineStatus)> ProcessAsync(
            IDiscoveredArtifact item,
            CallNextPipeline<IDiscoveredArtifact, string> next,
            CancellationToken token)
        {
            return next(item);
        }
    }
}
