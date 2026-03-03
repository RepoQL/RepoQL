using AwesomeAssertions;
using RepoQL.Explore;

namespace RepoQL.Rendering.Tests;

public class ResultClustererTests
{
    [Test]
    [DisplayName("Clustering is skipped when fewer than 6 results are provided")]
    public void Given_FewerThanSixResults_Then_NoClustering()
    {
        var decisions = new List<RenderingDecision>
        {
            CreateDecision("file:///src/Auth/A.cs", 95),
            CreateDecision("file:///src/Auth/B.cs", 90),
            CreateDecision("file:///src/Auth/C.cs", 85),
            CreateDecision("file:///src/Auth/D.cs", 80),
            CreateDecision("file:///src/Auth/E.cs", 75),
        };

        var clustered = ResultClusterer.Cluster(decisions);

        clustered.HeaderCount.Should().Be(0);
        clustered.Items.Should().HaveCount(5);
        clustered.Items.Select(i => ((RenderingDecision)i).Result.Uri)
            .Should().Equal(decisions.Select(d => d.Result.Uri));
    }

    [Test]
    [DisplayName("Three-member clusters get headers and interleave with singles by confidence")]
    public void Given_MixedResults_Then_ClustersAndSinglesInterleaveByConfidence()
    {
        var decisions = new List<RenderingDecision>
        {
            CreateDecision("file:///src/Top.cs", 98),
            CreateDecision("file:///src/Auth/AuthService.cs", 95),
            CreateDecision("file:///src/Auth/JwtHandler.cs", 90),
            CreateDecision("file:///src/Auth/TokenValidator.cs", 86),
            CreateDecision("file:///src/Billing/InvoiceService.cs", 89),
            CreateDecision("file:///src/Billing/PaymentService.cs", 84),
            CreateDecision("file:///docs/Guide.md", 80),
        };

        var clustered = ResultClusterer.Cluster(decisions);

        var firstResult = clustered.Items[0].Should().BeOfType<RenderingDecision>().Subject;
        firstResult.Result.Uri.Should().Be("file:///src/Top.cs");

        var header = clustered.Items[1].Should().BeOfType<ClusterHeader>().Subject;
        header.SharedPath.Should().Be("src/Auth/");
        header.MemberCount.Should().Be(3);

        clustered.Items[2].Should().BeOfType<RenderingDecision>()
            .Subject.Result.Uri.Should().Be("file:///src/Auth/AuthService.cs");
        clustered.Items[3].Should().BeOfType<RenderingDecision>()
            .Subject.Result.Uri.Should().Be("file:///src/Auth/JwtHandler.cs");
        clustered.Items[4].Should().BeOfType<RenderingDecision>()
            .Subject.Result.Uri.Should().Be("file:///src/Auth/TokenValidator.cs");

        clustered.Items[5].Should().BeOfType<RenderingDecision>()
            .Subject.Result.Uri.Should().Be("file:///src/Billing/InvoiceService.cs");
        clustered.Items[6].Should().BeOfType<RenderingDecision>()
            .Subject.Result.Uri.Should().Be("file:///src/Billing/PaymentService.cs");
        clustered.Items[7].Should().BeOfType<RenderingDecision>()
            .Subject.Result.Uri.Should().Be("file:///docs/Guide.md");
    }

    [Test]
    [DisplayName("Two-member clusters are adjacent but do not get headers")]
    public void Given_TwoMemberCluster_Then_NoHeaderAndMembersAdjacent()
    {
        var decisions = new List<RenderingDecision>
        {
            CreateDecision("file:///src/Top.cs", 99),
            CreateDecision("file:///src/Auth/AuthService.cs", 95),
            CreateDecision("file:///src/Auth/JwtHandler.cs", 90),
            CreateDecision("file:///docs/Guide.md", 80),
            CreateDecision("file:///tests/AuthTests.cs", 70),
            CreateDecision("file:///config/appsettings.json", 60),
        };

        var clustered = ResultClusterer.Cluster(decisions);

        clustered.Items.Should().NotContain(i => i is ClusterHeader);

        var authIndices = clustered.Items
            .Select((item, index) => (item, index))
            .Where(tuple => tuple.item is RenderingDecision decision &&
                            (decision.Result.Uri == "file:///src/Auth/AuthService.cs" ||
                             decision.Result.Uri == "file:///src/Auth/JwtHandler.cs"))
            .Select(tuple => tuple.index)
            .ToArray();

        authIndices.Should().HaveCount(2);
        authIndices[1].Should().Be(authIndices[0] + 1);
    }

    private static RenderingDecision CreateDecision(string uri, int confidence)
    {
        var result = new ExploreResult(
            Uri: uri,
            Confidence: confidence,
            Kind: null,
            Headline: uri,
            Structure: null,
            Snippet: null,
            Lang: null);

        return new RenderingDecision(result, Representation.Compact, EstimatedTokens: 10);
    }
}
