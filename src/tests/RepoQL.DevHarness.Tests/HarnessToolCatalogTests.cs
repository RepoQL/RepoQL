using System.Linq;
using System.Text.Json;
using AwesomeAssertions;
using RepoQL.DevHarness.Proxy;

namespace RepoQL.DevHarness.Tests;

public class HarnessToolCatalogTests
{
    [Test]
    public async Task TryMergeInitializeResponse_AddsHarnessTools()
    {
        var response = """{"jsonrpc":"2.0","id":1,"result":{"protocolVersion":"2025-11-25"}}""";

        var merged = HarnessToolCatalog.TryMergeInitializeResponse(response, out var updated);

        merged.Should().BeTrue();
        using var doc = JsonDocument.Parse(updated);
        var tools = doc.RootElement.GetProperty("result").GetProperty("tools");
        var toolNames = tools.EnumerateArray()
            .Select(tool => tool.GetProperty("name").GetString())
            .ToArray();

        toolNames.Should().Contain(HarnessToolCatalog.StatusToolName);
        toolNames.Should().Contain(HarnessToolCatalog.BuildToolName);
        toolNames.Should().Contain(HarnessToolCatalog.RestartToolName);
        toolNames.Should().Contain(HarnessToolCatalog.DeployToolName);
        toolNames.Should().Contain(HarnessToolCatalog.WaitForOperationToolName);
        toolNames.Should().Contain(HarnessToolCatalog.LogsToolName);
        toolNames.Should().Contain(HarnessToolCatalog.TracesToolName);
        toolNames.Should().Contain(HarnessToolCatalog.ConsoleLogsToolName);
        toolNames.Should().Contain(HarnessToolCatalog.TraceLogsToolName);
    }

    [Test]
    public async Task TryMergeToolsListResponse_AddsHarnessTools()
    {
        var response = """{"jsonrpc":"2.0","id":2,"result":{"tools":[{"name":"query","description":"Run SQL.","inputSchema":{"type":"object"}}]}}""";

        var merged = HarnessToolCatalog.TryMergeToolsListResponse(response, out var updated);

        merged.Should().BeTrue();
        using var doc = JsonDocument.Parse(updated);
        var tools = doc.RootElement.GetProperty("result").GetProperty("tools");
        var toolNames = tools.EnumerateArray()
            .Select(tool => tool.GetProperty("name").GetString())
            .ToArray();

        toolNames.Should().Contain("query");
        toolNames.Should().Contain(HarnessToolCatalog.StatusToolName);
        toolNames.Should().Contain(HarnessToolCatalog.BuildToolName);
        toolNames.Should().Contain(HarnessToolCatalog.LogsToolName);
        toolNames.Should().Contain(HarnessToolCatalog.ConsoleLogsToolName);
        toolNames.Should().Contain(HarnessToolCatalog.TraceLogsToolName);
    }
}
