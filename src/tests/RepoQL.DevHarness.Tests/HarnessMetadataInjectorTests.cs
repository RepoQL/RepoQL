using System.Text.Json;
using AwesomeAssertions;
using RepoQL.DevHarness.Proxy;

namespace RepoQL.DevHarness.Tests;

public class HarnessMetadataInjectorTests
{
    [Test]
    public async Task TryInjectToolResponse_AddsHarnessMetadata()
    {
        var json = """{"jsonrpc":"2.0","id":1,"result":{"content":[{"type":"text","text":"ok"}]}}""";

        var injected = HarnessMetadataInjector.TryInjectToolResponse(
            json,
            "req_20260205143022_abcd",
            45,
            out var updated);

        injected.Should().BeTrue();

        using var doc = JsonDocument.Parse(updated);
        var harness = doc.RootElement.GetProperty("result").GetProperty("_harness");
        harness.GetProperty("request_id").GetString().Should().Be("req_20260205143022_abcd");
        harness.GetProperty("duration_ms").GetInt64().Should().Be(45);
    }

    [Test]
    public async Task TryInjectToolResponse_SkipsErrorResponses()
    {
        var json = """{"jsonrpc":"2.0","id":1,"error":{"code":-32000,"message":"boom"}}""";

        var injected = HarnessMetadataInjector.TryInjectToolResponse(
            json,
            "req_20260205143022_abcd",
            12,
            out var updated);

        injected.Should().BeFalse();
        updated.Should().Be(json);
    }
}
