using System.Text.Json;
using AwesomeAssertions;
using RepoQL.Data.DuckDB.UdfImplementations;

namespace RepoQL.Data.DuckDB.Tests;

public sealed class HostMemoryUdfTests
{
    [Test]
    public async Task ProcessMemory_ReturnsExpectedFields()
    {
        var udf = new HostMemoryUdf();

        using var doc = JsonDocument.Parse(udf.ProcessMemory(string.Empty));
        var root = doc.RootElement;

        root.GetProperty("working_set_bytes").GetInt64().Should().BeGreaterThan(0);
        root.GetProperty("peak_working_set_bytes").GetInt64().Should().BeGreaterThanOrEqualTo(0);
        root.GetProperty("private_memory_bytes").GetInt64().Should().BeGreaterThanOrEqualTo(0);
        root.GetProperty("paged_memory_bytes").GetInt64().Should().BeGreaterThanOrEqualTo(0);
        root.GetProperty("virtual_memory_bytes").GetInt64().Should().BeGreaterThanOrEqualTo(0);
    }

    [Test]
    public async Task GcMemoryInfo_ReturnsExpectedFields()
    {
        var udf = new HostMemoryUdf();

        using var doc = JsonDocument.Parse(udf.GcMemoryInfo(string.Empty));
        var root = doc.RootElement;

        root.GetProperty("heap_size_bytes").GetInt64().Should().BeGreaterThanOrEqualTo(0);
        root.GetProperty("fragmented_bytes").GetInt64().Should().BeGreaterThanOrEqualTo(0);
        root.GetProperty("committed_bytes").GetInt64().Should().BeGreaterThanOrEqualTo(0);
        root.GetProperty("memory_load_bytes").GetInt64().Should().BeGreaterThanOrEqualTo(0);
        root.GetProperty("high_memory_load_threshold_bytes").GetInt64().Should().BeGreaterThanOrEqualTo(0);
        root.GetProperty("total_available_memory_bytes").GetInt64().Should().BeGreaterThan(0);
        root.GetProperty("finalization_pending_count").GetInt32().Should().BeGreaterThanOrEqualTo(0);
        root.GetProperty("pause_time_percentage").GetDouble().Should().BeGreaterThanOrEqualTo(0);
    }
}
