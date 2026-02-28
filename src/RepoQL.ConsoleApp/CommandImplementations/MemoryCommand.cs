using System.Globalization;
using Google.Protobuf.WellKnownTypes;
using RepoQL.Commands;
using RepoQL.ConsoleApp.Helpers;
using RepoQL.Contracts;

namespace RepoQL.ConsoleApp.CommandImplementations;

/// <summary>
/// Purpose: Expose host memory and graph stats as a ::memory command.
/// Complexity: Client-side command that queries the host via gRPC for DuckDB memory,
/// graph stats, and reads host PID for process working set. Follows the
/// ReindexCommand/HostRestartCommand pattern.
/// </summary>
[CommandClass]
internal sealed class MemoryCommand(RepoQlClientProvider clientProvider)
{
    private const long OneMb = 1024 * 1024;

    private const string MemorySql = """
        SELECT
            host_working_set() AS working_set,
            host_managed_heap() AS managed_heap,
            host_total_memory() AS total_memory,
            host_gc_counts() AS gc_counts,
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
            (SELECT COUNT(*) FROM document_embedding) AS embedded,
            (SELECT COUNT(*) FROM node WHERE kind = 'document') - (SELECT COUNT(*) FROM document_embedding) AS unembedded
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

    [Command("memory", Description = "Show host memory breakdown by pool")]
    public async Task<CommandResult> Execute(CancellationToken cancel)
    {
        long indexSizeBytes = 0;

        // Read index file size from the file system (client-side, no gRPC needed)
        try
        {
            var repoRoot = RepoLocator.FindRepoRoot();
            if (repoRoot != null)
            {
                var dbPath = Path.Combine(repoRoot, ".repoql", "index.duckdb");
                if (File.Exists(dbPath))
                    indexSizeBytes = new FileInfo(dbPath).Length;

                var walPath = dbPath + ".wal";
                if (File.Exists(walPath))
                    indexSizeBytes += new FileInfo(walPath).Length;
            }
        }
        catch { /* index files may not exist yet */ }

        // Query all stats from host via gRPC — UDFs run host-side
        long workingSet = 0, managedHeap = 0, totalRam = 0;
        string gcCounts = "";
        long duckTotal = 0, duckTables = 0, duckIndexes = 0;
        string duckLimit = "unknown";
        long files = 0, symbols = 0, edges = 0, annotations = 0, totalLines = 0, languages = 0;
        long embedded = 0, unembedded = 0;
        bool semanticEnabled = false;
        List<EmbeddingGroup> embeddingGroups = [];

        try
        {
            var client = await clientProvider.GetClientAsync(cancel).ConfigureAwait(false);
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

        var duckOther = Math.Max(0, duckTotal - duckTables - duckIndexes);
        var nativeOther = Math.Max(0, workingSet - managedHeap - duckTotal);
        var embeddingPct = (embedded + unembedded) > 0
            ? (int)(100.0 * embedded / (embedded + unembedded))
            : 0;

        var lines = new List<string>
        {
            "Memory",
            "\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500",
            $"Host working set:      {Mb(workingSet),7} MB   (system: {Mb(totalRam)} MB)",
            $"  .NET managed heap:   {Mb(managedHeap),7} MB   ({gcCounts})",
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

    private sealed record EmbeddingGroup(string Model, long Dim, string EmbeddingType, long Count, long Docs);
}
