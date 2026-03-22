using AwesomeAssertions;
using FakeItEasy;
using RepoQL.Contracts;
using RepoQL.Explore;

namespace RepoQL.Tests;

internal sealed class TrustSignalTests
{
    [Test]
    public void FromSummary_ComputesExpectedFields()
    {
        var byStatus = A.Fake<IReadOnlyDictionary<UriStatus, int>>();
        var byEmbedding = A.Fake<IReadOnlyDictionary<EmbeddingStatus, int>>();

        var summary = new RegistrySummary(
            TotalFiles: 200,
            TotalSymbols: 500,
            IndexPending: 47,
            IndexFailed: 3,
            IndexStale: 2,
            IndexIndexed: 148,
            EmbeddedFiles: 120,
            EmbeddingApplicableFiles: 150,
            ByStatus: byStatus,
            ByEmbeddingStatus: byEmbedding);

        var signal = TrustSignal.FromSummary(summary, executionTimeMs: 42, semanticEnabled: true);

        signal.IndexTotal.Should().Be(200);
        signal.IndexPending.Should().Be(47);
        signal.IndexFailed.Should().Be(3);
        signal.IndexStale.Should().Be(2);
        signal.SemanticEnabled.Should().BeTrue();
        signal.SemanticPercent.Should().Be(80);
        signal.SemanticReady.Should().BeFalse();
        signal.ExecutionTimeMs.Should().Be(42);
    }
}
