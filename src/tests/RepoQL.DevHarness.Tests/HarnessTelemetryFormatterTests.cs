using System.Text.Json;
using AwesomeAssertions;
using RepoQL.DevHarness.Proxy;

namespace RepoQL.DevHarness.Tests;

public class HarnessTelemetryFormatterTests
{
    [Test]
    public async Task TryFormatLogs_ExtractsSignalFields()
    {
        var raw = """
[
  {
    "log_id": 123,
    "span_id": "abc",
    "trace_id": "def",
    "timestamp": "2026-02-05T14:30:01.234Z",
    "severity": "Error",
    "message": "Query failed",
    "resource_name": "host",
    "source": "RepoQL.Data.DuckDB",
    "exception": "System.Exception: boom",
    "attributes": { "sql": "SELECT * FROM nodes" },
    "dashboard_link": { "url": "http://localhost:15011/log/123" }
  }
]
""";

        var formatted = HarnessTelemetryFormatter.TryFormatLogs(raw, 100, out var payload, out var error);

        formatted.Should().BeTrue();
        error.Should().BeNull();

        using var doc = JsonDocument.Parse(payload);
        var logs = doc.RootElement.GetProperty("logs");
        logs.GetArrayLength().Should().Be(1);

        var log = logs[0];
        // Signal fields kept.
        log.GetProperty("timestamp").GetString().Should().Be("2026-02-05T14:30:01.234Z");
        log.GetProperty("severity").GetString().Should().Be("Error");
        log.GetProperty("message").GetString().Should().Be("Query failed");
        log.GetProperty("source").GetString().Should().Be("RepoQL.Data.DuckDB");
        log.GetProperty("exception").GetString().Should().Contain("boom");
        log.GetProperty("attributes").GetProperty("sql").GetString().Should().Be("SELECT * FROM nodes");

        // Noise fields dropped.
        log.TryGetProperty("log_id", out _).Should().BeFalse();
        log.TryGetProperty("span_id", out _).Should().BeFalse();
        log.TryGetProperty("trace_id", out _).Should().BeFalse();
        log.TryGetProperty("resource_name", out _).Should().BeFalse();
        log.TryGetProperty("dashboard_link", out _).Should().BeFalse();
    }

    [Test]
    public async Task TryFormatLogs_DropsEmptyAttributes()
    {
        var raw = """[{"message":"hello","attributes":{}}]""";

        HarnessTelemetryFormatter.TryFormatLogs(raw, 100, out var payload, out _);

        using var doc = JsonDocument.Parse(payload);
        var log = doc.RootElement.GetProperty("logs")[0];
        log.TryGetProperty("attributes", out _).Should().BeFalse();
    }

    [Test]
    public async Task TryFormatTraces_SummarizesSpans()
    {
        var raw = """
[
  {
    "trace_id": "trace-1",
    "title": "idle_processing",
    "duration_ms": 2469,
    "has_error": false,
    "timestamp": "2026-02-05T14:30:01Z",
    "spans": [
      { "span_id": "root-1", "parent_span_id": "", "name": "idle_processing", "duration_ms": 2469, "kind": "Internal", "attributes": {"epochs": "1"}, "links": [], "back_links": [] },
      { "span_id": "slow-1", "parent_span_id": "root-1", "name": "embedding", "duration_ms": 1200, "kind": "Client", "attributes": {}, "links": [], "back_links": [] },
      { "span_id": "fast-1", "parent_span_id": "root-1", "name": "udf.repo_container", "duration_ms": 0, "kind": "Internal", "attributes": {"row_count": "100"}, "links": [], "back_links": [] },
      { "span_id": "fast-2", "parent_span_id": "root-1", "name": "udf.repo_container", "duration_ms": 0, "kind": "Internal", "attributes": {"row_count": "200"}, "links": [], "back_links": [] },
      { "span_id": "fast-3", "parent_span_id": "root-1", "name": "udf.repo_container", "duration_ms": 0, "kind": "Internal", "attributes": {"row_count": "300"}, "links": [], "back_links": [] }
    ]
  }
]
""";

        var formatted = HarnessTelemetryFormatter.TryFormatTraces(raw, 10, out var payload, out var error);

        formatted.Should().BeTrue();
        error.Should().BeNull();

        using var doc = JsonDocument.Parse(payload);
        var trace = doc.RootElement.GetProperty("traces")[0];

        // Header fields preserved.
        trace.GetProperty("trace_id").GetString().Should().Be("trace-1");
        trace.GetProperty("title").GetString().Should().Be("idle_processing");
        trace.GetProperty("duration_ms").GetInt64().Should().Be(2469);

        // Only root span (2469ms) + slow span (1200ms) shown. Three fast spans (0ms) omitted.
        var spans = trace.GetProperty("spans");
        spans.GetArrayLength().Should().Be(2);
        trace.GetProperty("total_spans").GetInt32().Should().Be(5);
        trace.GetProperty("omitted_spans").GetInt32().Should().Be(3);

        // Span summaries drop links/back_links but keep attributes.
        var rootSpan = spans[0];
        rootSpan.GetProperty("name").GetString().Should().Be("idle_processing");
        rootSpan.TryGetProperty("links", out _).Should().BeFalse();
        rootSpan.TryGetProperty("back_links", out _).Should().BeFalse();
        rootSpan.GetProperty("attributes").GetProperty("epochs").GetString().Should().Be("1");
    }

    [Test]
    public async Task TryFormatTraces_IncludesErrorSpans()
    {
        var raw = """
[
  {
    "trace_id": "trace-err",
    "title": "query",
    "duration_ms": 100,
    "has_error": true,
    "spans": [
      { "span_id": "root", "parent_span_id": "", "name": "query", "duration_ms": 100, "status": null, "attributes": {} },
      { "span_id": "err", "parent_span_id": "root", "name": "db.execute", "duration_ms": 5, "status": "Error", "status_message": "timeout", "attributes": {} },
      { "span_id": "ok", "parent_span_id": "root", "name": "db.prepare", "duration_ms": 2, "status": null, "attributes": {} }
    ]
  }
]
""";

        HarnessTelemetryFormatter.TryFormatTraces(raw, 10, out var payload, out _);

        using var doc = JsonDocument.Parse(payload);
        var spans = doc.RootElement.GetProperty("traces")[0].GetProperty("spans");
        // Root (always) + error span shown. OK span (2ms < 50ms threshold, no error) omitted.
        spans.GetArrayLength().Should().Be(2);

        var errSpan = spans[1];
        errSpan.GetProperty("name").GetString().Should().Be("db.execute");
        errSpan.GetProperty("status").GetString().Should().Be("Error");
        errSpan.GetProperty("status_message").GetString().Should().Be("timeout");
    }

    [Test]
    public async Task TryFormatLogs_AppliesLimit()
    {
        var raw = """
[
  { "message": "first" },
  { "message": "second" },
  { "message": "third" }
]
""";

        var formatted = HarnessTelemetryFormatter.TryFormatLogs(raw, 2, out var payload, out _);

        formatted.Should().BeTrue();
        using var doc = JsonDocument.Parse(payload);
        doc.RootElement.GetProperty("count").GetInt32().Should().Be(2);
        doc.RootElement.GetProperty("truncated").GetBoolean().Should().BeTrue();
    }

    [Test]
    public async Task TryFormatLogs_EmptyInput_ReturnsEmptyCollection()
    {
        var formatted = HarnessTelemetryFormatter.TryFormatLogs(null, 100, out var payload, out var error);

        formatted.Should().BeTrue();
        error.Should().BeNull();

        using var doc = JsonDocument.Parse(payload);
        doc.RootElement.GetProperty("logs").GetArrayLength().Should().Be(0);
        doc.RootElement.GetProperty("count").GetInt32().Should().Be(0);
    }

    [Test]
    public async Task TryFormatConsoleLogs_TakesLastNLines()
    {
        var raw = "line1\nline2\nline3\nline4\nline5";

        var formatted = HarnessTelemetryFormatter.TryFormatConsoleLogs(raw, 3, out var payload, out var error);

        formatted.Should().BeTrue();
        error.Should().BeNull();

        using var doc = JsonDocument.Parse(payload);
        var lines = doc.RootElement.GetProperty("lines");
        lines.GetArrayLength().Should().Be(3);
        lines[0].GetString().Should().Be("line3");
        lines[1].GetString().Should().Be("line4");
        lines[2].GetString().Should().Be("line5");
        doc.RootElement.GetProperty("truncated").GetBoolean().Should().BeTrue();
    }

    [Test]
    public async Task TryFormatConsoleLogs_EmptyInput_ReturnsEmptyOutput()
    {
        var formatted = HarnessTelemetryFormatter.TryFormatConsoleLogs(null, 200, out var payload, out var error);

        formatted.Should().BeTrue();
        error.Should().BeNull();

        using var doc = JsonDocument.Parse(payload);
        doc.RootElement.GetProperty("lines").GetArrayLength().Should().Be(0);
        doc.RootElement.GetProperty("count").GetInt32().Should().Be(0);
    }

    [Test]
    public async Task TryFormatConsoleLogs_AllLinesFit_NotTruncated()
    {
        var raw = "line1\nline2";

        var formatted = HarnessTelemetryFormatter.TryFormatConsoleLogs(raw, 200, out var payload, out var error);

        formatted.Should().BeTrue();

        using var doc = JsonDocument.Parse(payload);
        doc.RootElement.GetProperty("lines").GetArrayLength().Should().Be(2);
        doc.RootElement.GetProperty("truncated").GetBoolean().Should().BeFalse();
    }

    [Test]
    public async Task TryFormatConsoleLogs_ContainsFilter_MatchesLines()
    {
        var raw = "dbug: Parsed file\nfail: Build failed\ninfo: Starting up\nfail: Unhandled exception\ndbug: Done";

        var formatted = HarnessTelemetryFormatter.TryFormatConsoleLogs(raw, 200, "fail", out var payload, out var error);

        formatted.Should().BeTrue();
        error.Should().BeNull();

        using var doc = JsonDocument.Parse(payload);
        var lines = doc.RootElement.GetProperty("lines");
        lines.GetArrayLength().Should().Be(2);
        lines[0].GetString().Should().Contain("Build failed");
        lines[1].GetString().Should().Contain("Unhandled exception");
        doc.RootElement.GetProperty("total_before_filter").GetInt32().Should().Be(5);
    }

    [Test]
    public async Task TryFormatConsoleLogs_ContainsFilter_CaseInsensitive()
    {
        var raw = "info: OK\nfail: Error occurred\ninfo: Done";

        HarnessTelemetryFormatter.TryFormatConsoleLogs(raw, 200, "ERROR", out var payload, out _);

        using var doc = JsonDocument.Parse(payload);
        var lines = doc.RootElement.GetProperty("lines");
        lines.GetArrayLength().Should().Be(1);
        lines[0].GetString().Should().Contain("Error occurred");
    }
}
