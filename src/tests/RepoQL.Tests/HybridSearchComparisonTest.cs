using System.Text;
using RepoQL.Data.DuckDB;

namespace RepoQL.Tests;

/// <summary>
/// Manual test to compare hybrid_search vs file_search on the real RepoQL codebase.
/// Run this with: dotnet run -- --treenode-filter "/*/*/*/HybridSearchComparison*"
/// </summary>
internal class HybridSearchComparisonTest
{
    [Test]
    public void CompareHybridSearchVsFileSearch_OnRealCodebase()
    {
        var dbPath = Path.Combine(Environment.CurrentDirectory, ".repoql", "index.duckdb");
        if (!File.Exists(dbPath))
        {
            // Try parent directories
            var current = new DirectoryInfo(Environment.CurrentDirectory);
            while (current != null)
            {
                var candidate = Path.Combine(current.FullName, ".repoql", "index.duckdb");
                if (File.Exists(candidate))
                {
                    dbPath = candidate;
                    break;
                }
                current = current.Parent;
            }
        }

        if (!File.Exists(dbPath))
        {
            Console.WriteLine($"⚠️  RepoQL database not found. Please run 'repoql reindex' first.");
            return;
        }

        using var store = new DuckDbGraphStore(dbPath);
        store.EnsureSchema();

        Console.WriteLine("=".PadRight(80, '='));
        Console.WriteLine("HYBRID_SEARCH vs FILE_SEARCH COMPARISON");
        Console.WriteLine("=".PadRight(80, '='));
        Console.WriteLine();

        // Test 1: Recall comparison for "markdown"
        Console.WriteLine("TEST 1: Recall Comparison - 'markdown'");
        Console.WriteLine("-".PadRight(80, '-'));
        CompareRecall(store, "markdown");
        Console.WriteLine();

        // Test 2: Known item search - "SingleThreadedDatabaseWriter"
        Console.WriteLine("TEST 2: Known Item Search - 'SingleThreadedDatabaseWriter'");
        Console.WriteLine("-".PadRight(80, '-'));
        CompareKnownItem(store, "SingleThreadedDatabaseWriter");
        Console.WriteLine();

        // Test 3: Rescue feature demonstration - "DuckDB"
        Console.WriteLine("TEST 3: Rescue Feature Demo - 'DuckDB'");
        Console.WriteLine("-".PadRight(80, '-'));
        DemonstrateRescue(store, "DuckDB");
        Console.WriteLine();

        Console.WriteLine("=".PadRight(80, '='));
        Console.WriteLine("SUMMARY");
        Console.WriteLine("=".PadRight(80, '='));
        Console.WriteLine("✓ Tests completed successfully");
        Console.WriteLine("✓ Check console output above for detailed comparison results");
    }

    private static void CompareRecall(DuckDbGraphStore store, string query)
    {
        var fileSearchSql = $"SELECT uri FROM file_search('{query}') LIMIT 20";
        var hybridSearchSql = $@"
            WITH fs AS (SELECT uri FROM file_search('{query}') LIMIT 20),
                 hs AS (SELECT uri, source FROM hybrid_search('{query}') LIMIT 20)
            SELECT hs.uri, hs.source,
                   CASE WHEN fs.uri IS NOT NULL THEN 'both' ELSE 'hybrid_only' END AS found_by
            FROM hs
            LEFT JOIN fs ON fs.uri = hs.uri
            ORDER BY found_by DESC, hs.uri";

        var fileSearchResults = store.RawQuery(fileSearchSql).ToList();
        var comparisonResults = store.RawQuery(hybridSearchSql).ToList();

        Console.WriteLine($"file_search found: {fileSearchResults.Count} documents");
        Console.WriteLine($"hybrid_search found: {comparisonResults.Count} documents");

        var hybridOnly = comparisonResults.Where(r => r["found_by"]?.ToString() == "hybrid_only").ToList();
        if (hybridOnly.Any())
        {
            Console.WriteLine($"\n✓ hybrid_search found {hybridOnly.Count} additional documents:");
            foreach (var row in hybridOnly.Take(5))
            {
                var uri = row["uri"]?.ToString() ?? "";
                var source = row["source"]?.ToString() ?? "";
                var filename = Path.GetFileName(uri.Split('#')[0]);
                Console.WriteLine($"  • {filename} (source: {source})");
            }
            if (hybridOnly.Count > 5)
            {
                Console.WriteLine($"  ... and {hybridOnly.Count - 5} more");
            }
        }
        else
        {
            Console.WriteLine("\nℹ️  Both methods found the same documents");
        }
    }

    private static void CompareKnownItem(DuckDbGraphStore store, string query)
    {
        var fileSearchSql = $"SELECT uri, ROUND(score, 3) AS score FROM file_search('{query}') LIMIT 3";
        var hybridSearchSql = $"SELECT uri, ROUND(score, 3) AS score, source FROM hybrid_search('{query}') LIMIT 3";

        var fileSearchResults = store.RawQuery(fileSearchSql).ToList();
        var hybridSearchResults = store.RawQuery(hybridSearchSql).ToList();

        Console.WriteLine("file_search results:");
        foreach (var row in fileSearchResults)
        {
            var uri = row["uri"]?.ToString() ?? "";
            var score = row["score"];
            var filename = Path.GetFileName(uri.Split('#')[0]);
            Console.WriteLine($"  {score,6} - {filename}");
        }

        Console.WriteLine("\nhybrid_search results:");
        foreach (var row in hybridSearchResults)
        {
            var uri = row["uri"]?.ToString() ?? "";
            var score = row["score"];
            var source = row["source"];
            var filename = Path.GetFileName(uri.Split('#')[0]);
            Console.WriteLine($"  {score,6} - {filename} (source: {source})");
        }

        if (fileSearchResults.Any() && hybridSearchResults.Any())
        {
            var fsTop = Path.GetFileName(fileSearchResults[0]["uri"]?.ToString()?.Split('#')[0] ?? "");
            var hsTop = Path.GetFileName(hybridSearchResults[0]["uri"]?.ToString()?.Split('#')[0] ?? "");
            if (fsTop == hsTop)
            {
                Console.WriteLine($"\n✓ Both methods ranked '{fsTop}' first");
            }
            else
            {
                Console.WriteLine($"\n⚠️  Different top results: file_search='{fsTop}', hybrid_search='{hsTop}'");
            }
        }
    }

    private static void DemonstrateRescue(DuckDbGraphStore store, string query)
    {
        var sql = $@"
            SELECT uri, source, struct_mentions, body_mentions, ROUND(score, 3) AS score
            FROM hybrid_search('{query}', enable_body_rescue := TRUE)
            LIMIT 10";

        var results = store.RawQuery(sql).ToList();

        Console.WriteLine($"Top 10 results with rescue attribution:");
        Console.WriteLine($"{"Score",-8} {"Source",-10} {"Struct",-7} {"Body",-6} {"File",-40}");
        Console.WriteLine("-".PadRight(80, '-'));

        foreach (var row in results)
        {
            var uri = row["uri"]?.ToString() ?? "";
            var score = row["score"];
            var source = row["source"]?.ToString() ?? "";
            var structMentions = row["struct_mentions"];
            var bodyMentions = row["body_mentions"];
            var filename = Path.GetFileName(uri.Split('#')[0]);

            Console.WriteLine($"{score,-8} {source,-10} {structMentions,-7} {bodyMentions,-6} {filename,-40}");
        }

        var rescuedCount = results.Count(r =>
            r["source"]?.ToString() == "outline" || r["source"]?.ToString() == "body");

        if (rescuedCount > 0)
        {
            Console.WriteLine($"\n✓ {rescuedCount} documents were rescued by outline/body matching");
        }
        else
        {
            Console.WriteLine($"\nℹ️  All results came from semantic/BM25/search tiers");
        }
    }
}
