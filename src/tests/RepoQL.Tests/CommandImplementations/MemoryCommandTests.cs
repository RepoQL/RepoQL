using AwesomeAssertions;
using FakeItEasy;
using Google.Protobuf.WellKnownTypes;
using RepoQL.ConsoleApp.CommandImplementations;
using RepoQL.Contracts;
using RepoQL.Protocol;

namespace RepoQL.Tests.CommandImplementations;

internal sealed class MemoryCommandTests
{
    [Test]
    public async Task Execute_IncludesDetailedHostMemoryBreakdown()
    {
        var ops = A.Fake<MemoryCommand.IMemoryCommandOperations>();
        var client = A.Fake<IRepoQlClient>();

        A.CallTo(() => ops.TryGetIndexSizeBytes()).Returns(12L * 1024 * 1024);
        A.CallTo(() => ops.GetClientAsync(A<CancellationToken>._))
            .Returns(new ValueTask<IRepoQlClient>(client));

        A.CallTo(() => client.ExecuteRawQueryAsync(
                A<string>._,
                A<IEnumerable<object?>>._,
                A<int?>._,
                A<int>._,
                A<CancellationToken>._))
            .ReturnsNextFromSequence(
                Task.FromResult(CreateMemoryResponse()),
                Task.FromResult(CreateEmbeddingResponse()));

        var command = new MemoryCommand(ops);

        var result = await command.Execute(CancellationToken.None);

        result.IsError.Should().BeFalse();
        result.Text.Should().Contain("Peak working set:");
        result.Text.Should().Contain("Private bytes:");
        result.Text.Should().Contain("Virtual bytes:");
        result.Text.Should().Contain("Paged bytes:");
        result.Text.Should().Contain("GC heap size:");
        result.Text.Should().Contain("GC committed:");
        result.Text.Should().Contain("GC fragmented:");
        result.Text.Should().Contain("GC memory load:");
        result.Text.Should().Contain("Finalizers queued:");
        result.Text.Should().Contain("25% used");
        result.Text.Should().Contain("50%)");
    }

    private static RawQueryResponse CreateMemoryResponse()
    {
        var response = new RawQueryResponse
        {
            SemanticEnabled = true
        };

        var row = new RowData();
        row.Values.Add(Value.ForNumber(100 * 1024 * 1024));
        row.Values.Add(Value.ForNumber(40 * 1024 * 1024));
        row.Values.Add(Value.ForNumber(400 * 1024 * 1024));
        row.Values.Add(Value.ForString("gen0:1 gen1:2 gen2:3"));
        row.Values.Add(Value.ForString("""
            {"working_set_bytes":104857600,"peak_working_set_bytes":125829120,"private_memory_bytes":94371840,"paged_memory_bytes":31457280,"virtual_memory_bytes":268435456}
            """));
        row.Values.Add(Value.ForString("""
            {"heap_size_bytes":41943040,"fragmented_bytes":4194304,"committed_bytes":50331648,"memory_load_bytes":209715200,"high_memory_load_threshold_bytes":419430400,"total_available_memory_bytes":419430400,"finalization_pending_count":3}
            """));
        row.Values.Add(Value.ForNumber(30 * 1024 * 1024));
        row.Values.Add(Value.ForString("256MB"));
        row.Values.Add(Value.ForNumber(10 * 1024 * 1024));
        row.Values.Add(Value.ForNumber(5 * 1024 * 1024));
        row.Values.Add(Value.ForNumber(12));
        row.Values.Add(Value.ForNumber(50));
        row.Values.Add(Value.ForNumber(60));
        row.Values.Add(Value.ForNumber(3));
        row.Values.Add(Value.ForNumber(700));
        row.Values.Add(Value.ForNumber(4));
        row.Values.Add(Value.ForNumber(8));
        row.Values.Add(Value.ForNumber(2));
        response.Rows.Add(row);
        return response;
    }

    private static RawQueryResponse CreateEmbeddingResponse()
    {
        var response = new RawQueryResponse();
        var row = new RowData();
        row.Values.Add(Value.ForString("local-model"));
        row.Values.Add(Value.ForNumber(384));
        row.Values.Add(Value.ForString("document"));
        row.Values.Add(Value.ForNumber(8));
        row.Values.Add(Value.ForNumber(8));
        response.Rows.Add(row);
        return response;
    }
}

internal sealed class DiagnosticsCommandTests
{
    [Test]
    [Arguments(0L, "0ms")]
    [Arguments(999L, "999ms")]
    [Arguments(1_250L, "1.3s")]
    [Arguments(135_000L, "2m 15s")]
    [Arguments(3_661_000L, "1h 1m 1s")]
    public void FormatDuration_FormatsMillisecondsAcrossRanges(long milliseconds, string expected)
    {
        DiagnosticsCommand.FormatDuration(milliseconds).Should().Be(expected);
    }

    [Test]
    public void CalculateDurationDistribution_GroupsByExtensionAndComputesPercentiles()
    {
        var distributions = DiagnosticsCommand.CalculateDurationDistribution(
        [
            new DiagnosticsCommand.IndexerStatusEntry("file:///repo/src/alpha.cs", "Indexed", 1000),
            new DiagnosticsCommand.IndexerStatusEntry("file:///repo/src/beta.cs", "Indexed", 2000),
            new DiagnosticsCommand.IndexerStatusEntry("file:///repo/src/gamma.cs", "Indexed", 5000),
            new DiagnosticsCommand.IndexerStatusEntry("file:///repo/docs/readme.md", "Indexed", 4000)
        ]);

        distributions.Should().HaveCount(2);

        var csharp = distributions.Single(d => d.Extension == ".cs");
        csharp.MinMs.Should().Be(1000);
        csharp.P5Ms.Should().Be(1100);
        csharp.P50Ms.Should().Be(2000);
        csharp.AvgMs.Should().BeApproximately(2667.0f, 1.0);
        csharp.P95Ms.Should().Be(4700);
        csharp.MaxMs.Should().Be(5000);
        csharp.TotalMs.Should().Be(8000);
        csharp.Count.Should().Be(3);

        var markdown = distributions.Single(d => d.Extension == ".md");
        markdown.MinMs.Should().Be(4000);
        markdown.P50Ms.Should().Be(4000);
        markdown.TotalMs.Should().Be(4000);
        markdown.Count.Should().Be(1);
    }

    [Test]
    public void CalculateDurationDistribution_OmitsExtensionsWithOnlyZeroDurations()
    {
        var distributions = DiagnosticsCommand.CalculateDurationDistribution(
        [
            new DiagnosticsCommand.IndexerStatusEntry("file:///repo/src/alpha.cs", "Indexed", 0),
            new DiagnosticsCommand.IndexerStatusEntry("file:///repo/src/beta.cs", "Indexed", 0),
            new DiagnosticsCommand.IndexerStatusEntry("file:///repo/src/gamma.md", "Indexed", 250)
        ]);

        distributions.Should().HaveCount(1);
        distributions[0].Extension.Should().Be(".md");
        distributions[0].MaxMs.Should().Be(250);
    }
}
