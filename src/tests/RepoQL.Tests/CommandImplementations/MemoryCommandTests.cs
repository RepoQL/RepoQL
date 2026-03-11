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
