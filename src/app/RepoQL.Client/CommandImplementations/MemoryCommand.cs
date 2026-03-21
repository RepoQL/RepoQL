using System.Globalization;
using System.Text.Json;
using Google.Protobuf.WellKnownTypes;
using RepoQL.Commands;
using RepoQL.ConsoleApp.Helpers;
using RepoQL.Contracts;
using RepoQL.Protocol;

namespace RepoQL.ConsoleApp.CommandImplementations;

/// <summary>
/// Purpose: Expose host memory and graph stats as a ::diagnostics.memory command.
/// Complexity: Client-side command that queries the host via gRPC for DuckDB memory,
/// graph stats, and reads host PID for process working set. Follows the
/// ReindexCommand/HostRestartCommand pattern.
/// </summary>
[CommandClass]
internal sealed class MemoryCommand
{
    private const long OneMb = 1024 * 1024;
    private readonly IMemoryCommandOperations _operations;

    private const string MemorySql = """
        SELECT
            host_working_set() AS working_set,
            host_managed_heap() AS managed_heap,
            host_total_memory() AS total_memory,
            host_gc_counts() AS gc_counts,
            host_process_memory() AS process_memory,
            host_gc_memory_info() AS gc_memory_info,
            (SELECT COALESCE(SUM(memory_usage_bytes), 0)::BIGINT FROM duckdb_memory()) AS duck_total,
            (SELECT value FROM duckdb_settings() WHERE name = 'memory_limit') AS duck_limit,
            (SELECT COALESCE(SUM(CASE WHEN tag = 'BASE_TABLE' THEN memory_usage_bytes END), 0)::BIGINT FROM duckdb_memory()) AS duck_tables,
            (SELECT COALESCE(SUM(CASE WHEN tag = 'ART_INDEX' THEN memory_usage_bytes END), 0)::BIGINT FROM duckdb_memory()) AS duck_indexes,
            (SELECT COUNT(*) FROM node WHERE kind = 'document') AS files,
            (SELECT COUNT(*) FROM node WHERE kind != 'document') AS symbols,
            (SELECT COUNT(*) FROM edge) AS edges,
            (SELECT COUNT(*) FROM annotation) AS annotations,
            (SELECT COALESCE(SUM(lines), 0) FROM Files) AS total_lines,
            (SELECT COUNT(DISTINCT lang) FROM Files) AS languages,
            (SELECT COUNT(DISTINCT uri) FROM document_embedding) AS embedded,
            (SELECT COUNT(*) FROM node WHERE kind = 'document') - (SELECT COUNT(DISTINCT uri) FROM document_embedding) AS unembedded
        """;

    private const string EmbeddingDetailSql = """
        SELECT
            model,
            dim,
            embedding_type,
            COUNT(*) AS cnt,
            COUNT(DISTINCT uri) AS docs
        FROM document_embedding
        GROUP BY model, dim, embedding_type
        ORDER BY model, embedding_type
        """;

    public MemoryCommand(RepoQlClientProvider clientProvider)
        : this(new DefaultMemoryCommandOperations(clientProvider))
    {
    }

    internal MemoryCommand(IMemoryCommandOperations operations)
    {
        _operations = operations;
    }

    [Command("diagnostics.memory", Description = "Show host memory breakdown by pool")]
    public async Task<CommandResult> Execute(CancellationToken cancel)
    {
        var indexSizeBytes = _operations.TryGetIndexSizeBytes();

        // Query all stats from host via gRPC — UDFs run host-side
        long workingSet = 0, managedHeap = 0, totalRam = 0;
        string gcCounts = "";
        HostProcessMemorySnapshot processMemory = HostProcessMemorySnapshot.Empty;
        HostGcMemorySnapshot gcMemory = HostGcMemorySnapshot.Empty;
        long duckTotal = 0, duckTables = 0, duckIndexes = 0;
        string duckLimit = "unknown";
        long files = 0, symbols = 0, edges = 0, annotations = 0, totalLines = 0, languages = 0;
        long embedded = 0, unembedded = 0;
        bool semanticEnabled = false;
        List<EmbeddingGroup> embeddingGroups = [];

        try
        {
            var client = await _operations.GetClientAsync(cancel).ConfigureAwait(false);
            var result = await client.ExecuteRawQueryAsync(MemorySql, cancellationToken: cancel).ConfigureAwait(false);

            semanticEnabled = result.SemanticEnabled;

            if (result.Rows.Count > 0)
            {
                var row = result.Rows[0];
                var i = 0;
                workingSet = GetLong(row, i++);
                managedHeap = GetLong(row, i++);
                totalRam = GetLong(row, i++);
                gcCounts = GetString(row, i++) ?? "";
                processMemory = ParseProcessMemory(GetString(row, i++));
                gcMemory = ParseGcMemory(GetString(row, i++));
                duckTotal = GetLong(row, i++);
                duckLimit = GetString(row, i++) ?? "unknown";
                duckTables = GetLong(row, i++);
                duckIndexes = GetLong(row, i++);
                files = GetLong(row, i++);
                symbols = GetLong(row, i++);
                edges = GetLong(row, i++);
                annotations = GetLong(row, i++);
                totalLines = GetLong(row, i++);
                languages = GetLong(row, i++);
                embedded = GetLong(row, i++);
                unembedded = GetLong(row, i);
            }

            // Embedding model details — separate query to keep MemorySql a single-row result
            var detail = await client.ExecuteRawQueryAsync(EmbeddingDetailSql, cancellationToken: cancel).ConfigureAwait(false);
            foreach (var row in detail.Rows)
            {
                embeddingGroups.Add(new EmbeddingGroup(
                    Model: GetString(row, 0) ?? "unknown",
                    Dim: GetLong(row, 1),
                    EmbeddingType: GetString(row, 2) ?? "unknown",
                    Count: GetLong(row, 3),
                    Docs: GetLong(row, 4)));
            }
        }
        catch (Exception ex)
        {
            return CommandResult.Error($"Failed to query host: {ex.Message}");
        }

        if (workingSet == 0 && processMemory.WorkingSetBytes > 0)
            workingSet = processMemory.WorkingSetBytes;

        if (totalRam == 0 && gcMemory.TotalAvailableMemoryBytes > 0)
            totalRam = gcMemory.TotalAvailableMemoryBytes;

        var duckOther = Math.Max(0, duckTotal - duckTables - duckIndexes);
        var nativeOther = Math.Max(0, workingSet - managedHeap - duckTotal);
        var embeddingPct = (embedded + unembedded) > 0
            ? (int)(100.0 * embedded / (embedded + unembedded))
            : 0;
        var hostRamPct = Percent(workingSet, totalRam);
        var gcLoadPct = Percent(gcMemory.MemoryLoadBytes, gcMemory.HighMemoryLoadThresholdBytes);

        var lines = new List<string>
        {
            "Memory",
            "\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500",
            $"Host working set:      {Mb(workingSet),7} MB   (system: {Mb(totalRam)} MB, {hostRamPct}% used)",
            $"  Peak working set:    {Mb(processMemory.PeakWorkingSetBytes),7} MB",
            $"  Private bytes:       {Mb(processMemory.PrivateMemoryBytes),7} MB",
            $"  Virtual bytes:       {Mb(processMemory.VirtualMemoryBytes),7} MB",
            $"  Paged bytes:         {Mb(processMemory.PagedMemoryBytes),7} MB",
            $"  .NET live heap:      {Mb(managedHeap),7} MB   ({gcCounts})",
            $"    GC heap size:      {Mb(gcMemory.HeapSizeBytes),7} MB",
            $"    GC committed:      {Mb(gcMemory.CommittedBytes),7} MB",
            $"    GC fragmented:     {Mb(gcMemory.FragmentedBytes),7} MB",
            $"    GC memory load:    {Mb(gcMemory.MemoryLoadBytes),7} MB / {Mb(gcMemory.HighMemoryLoadThresholdBytes)} MB ({gcLoadPct}%)",
            $"    Finalizers queued: {gcMemory.FinalizationPendingCount,7:N0}",
            $"  DuckDB buffer:       {Mb(duckTotal),7} MB   (limit: {duckLimit})",
            $"    Tables:            {Mb(duckTables),7} MB",
            $"    Indexes:           {Mb(duckIndexes),7} MB",
            $"    Other:             {Mb(duckOther),7} MB",
            $"  Native other:        {Mb(nativeOther),7} MB"
        };

        if (semanticEnabled)
            lines.Add("    (includes ONNX runtime + model)");

        lines.AddRange([
            string.Empty,
            "Graph",
            "\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500",
            $"  Files:         {files,10:N0}   ({languages:N0} languages, {totalLines:N0} lines)",
            $"  Symbols:       {symbols,10:N0}",
            $"  Edges:         {edges,10:N0}",
            $"  Annotations:   {annotations,10:N0}",
            string.Empty,
            "Embeddings",
            "\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500",
            $"  Status:        {(semanticEnabled ? "active" : "disabled")}",
            $"  Vectors:       {embedded,10:N0}   ({embeddingPct}% of {embedded + unembedded:N0} files)",
            $"  Pending:       {unembedded,10:N0}"
        ]);

        if (embeddingGroups.Count > 0)
        {
            var models = embeddingGroups
                .GroupBy(g => new { g.Model, g.Dim })
                .ToList();

            foreach (var model in models)
            {
                var totalVectors = model.Sum(g => g.Count);
                var totalDocs = model.Max(g => g.Docs);
                lines.Add($"  Model:         {model.Key.Model}");
                lines.Add($"    Dimensions:  {model.Key.Dim,10:N0}     ({totalVectors:N0} vectors, {totalDocs:N0} docs)");

                foreach (var group in model)
                    lines.Add($"    {group.EmbeddingType,-12} {group.Count,10:N0} vectors");
            }
        }

        lines.AddRange([
            string.Empty,
            "Disk",
            "\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500",
            $"  Index (db+wal):      {Mb(indexSizeBytes),7} MB"
        ]);

        return CommandResult.Success(string.Join(Environment.NewLine, lines));
    }

    private static long GetLong(RowData row, int index)
    {
        if (index >= row.Values.Count) return 0;
        var v = row.Values[index];
        return v.KindCase switch
        {
            Value.KindOneofCase.NumberValue => (long)v.NumberValue,
            Value.KindOneofCase.StringValue => long.TryParse(v.StringValue, CultureInfo.InvariantCulture, out var n) ? n : 0,
            _ => 0
        };
    }

    private static string? GetString(RowData row, int index)
    {
        if (index >= row.Values.Count) return null;
        var v = row.Values[index];
        return v.KindCase switch
        {
            Value.KindOneofCase.StringValue => v.StringValue,
            Value.KindOneofCase.NumberValue => v.NumberValue.ToString(CultureInfo.InvariantCulture),
            _ => null
        };
    }

    private static string Mb(long bytes) =>
        (bytes / (double)OneMb).ToString("N0", CultureInfo.InvariantCulture);

    private static int Percent(long value, long total)
        => total > 0
            ? (int)Math.Round(100.0 * value / total, MidpointRounding.AwayFromZero)
            : 0;

    private static HostProcessMemorySnapshot ParseProcessMemory(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return HostProcessMemorySnapshot.Empty;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            return new HostProcessMemorySnapshot(
                WorkingSetBytes: GetJsonLong(root, "working_set_bytes"),
                PeakWorkingSetBytes: GetJsonLong(root, "peak_working_set_bytes"),
                PrivateMemoryBytes: GetJsonLong(root, "private_memory_bytes"),
                PagedMemoryBytes: GetJsonLong(root, "paged_memory_bytes"),
                VirtualMemoryBytes: GetJsonLong(root, "virtual_memory_bytes"));
        }
        catch (JsonException)
        {
            return HostProcessMemorySnapshot.Empty;
        }
    }

    private static HostGcMemorySnapshot ParseGcMemory(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return HostGcMemorySnapshot.Empty;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            return new HostGcMemorySnapshot(
                HeapSizeBytes: GetJsonLong(root, "heap_size_bytes"),
                FragmentedBytes: GetJsonLong(root, "fragmented_bytes"),
                CommittedBytes: GetJsonLong(root, "committed_bytes"),
                MemoryLoadBytes: GetJsonLong(root, "memory_load_bytes"),
                HighMemoryLoadThresholdBytes: GetJsonLong(root, "high_memory_load_threshold_bytes"),
                TotalAvailableMemoryBytes: GetJsonLong(root, "total_available_memory_bytes"),
                FinalizationPendingCount: GetJsonInt(root, "finalization_pending_count"));
        }
        catch (JsonException)
        {
            return HostGcMemorySnapshot.Empty;
        }
    }

    private static long GetJsonLong(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
            return 0;

        return property.ValueKind switch
        {
            JsonValueKind.Number => property.TryGetInt64(out var value) ? value : 0,
            JsonValueKind.String => long.TryParse(property.GetString(), CultureInfo.InvariantCulture, out var value) ? value : 0,
            _ => 0
        };
    }

    private static int GetJsonInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
            return 0;

        return property.ValueKind switch
        {
            JsonValueKind.Number => property.TryGetInt32(out var value) ? value : 0,
            JsonValueKind.String => int.TryParse(property.GetString(), CultureInfo.InvariantCulture, out var value) ? value : 0,
            _ => 0
        };
    }

    private sealed record EmbeddingGroup(string Model, long Dim, string EmbeddingType, long Count, long Docs);
    internal sealed record HostProcessMemorySnapshot(long WorkingSetBytes, long PeakWorkingSetBytes, long PrivateMemoryBytes, long PagedMemoryBytes, long VirtualMemoryBytes)
    {
        public static HostProcessMemorySnapshot Empty { get; } = new(0, 0, 0, 0, 0);
    }

    internal sealed record HostGcMemorySnapshot(long HeapSizeBytes, long FragmentedBytes, long CommittedBytes, long MemoryLoadBytes, long HighMemoryLoadThresholdBytes, long TotalAvailableMemoryBytes, int FinalizationPendingCount)
    {
        public static HostGcMemorySnapshot Empty { get; } = new(0, 0, 0, 0, 0, 0, 0);
    }

    internal interface IMemoryCommandOperations
    {
        ValueTask<IRepoQlClient> GetClientAsync(CancellationToken cancellationToken);
        long TryGetIndexSizeBytes();
    }

    private sealed class DefaultMemoryCommandOperations(RepoQlClientProvider clientProvider) : IMemoryCommandOperations
    {
        public ValueTask<IRepoQlClient> GetClientAsync(CancellationToken cancellationToken)
            => clientProvider.GetClientAsync(cancellationToken);

        public long TryGetIndexSizeBytes()
        {
            try
            {
                var repoRoot = RepoLocator.FindRepoRoot();
                if (repoRoot is null)
                    return 0;

                var dbPath = Path.Combine(repoRoot, ".repoql", "index.duckdb");
                long total = 0;

                if (File.Exists(dbPath))
                    total += new FileInfo(dbPath).Length;

                var walPath = dbPath + ".wal";
                if (File.Exists(walPath))
                    total += new FileInfo(walPath).Length;

                return total;
            }
            catch
            {
                return 0;
            }
        }
    }
}
