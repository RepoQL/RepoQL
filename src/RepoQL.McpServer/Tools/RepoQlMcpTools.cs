using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Server;
using RepoQL.Contracts;
using RepoQL.McpServer.Formatting;

namespace RepoQL.McpServer.Tools;

internal static class RepoQlMcpTools
{
    public static McpServerTool CreateQueryTool(IRepoQlClient client)
    {
        var options = new McpServerToolCreateOptions
        {
            Name = "repoql.query",
            Title = "RepoQL SQL Query",
            Description = "Run a SQL query against the local RepoQL DuckDB index and return a text table.",
            Idempotent = true,
            ReadOnly = true
        };

        // Delegate signature is reflected to build JSON schema automatically
        return McpServerTool.Create(
            async (string sql, int? limit, CancellationToken ct) =>
            {
                if (string.IsNullOrWhiteSpace(sql))
                    return "No SQL provided. Example: SELECT * FROM xray_documents() LIMIT 10";

                var max = limit.GetValueOrDefault(1000);
                var trimmed = sql.Trim().TrimEnd(';');
                var hasLimit = trimmed.IndexOf(" LIMIT ", StringComparison.OrdinalIgnoreCase) >= 0
                               || trimmed.EndsWith(" LIMIT", StringComparison.OrdinalIgnoreCase)
                               || trimmed.Contains("\nLIMIT ", StringComparison.OrdinalIgnoreCase);
                var limitedSql = hasLimit ? trimmed : $"{trimmed} LIMIT {max}";

                try
                {
                    var result = await client.ExecuteRawQueryAsync(limitedSql, rowLimit: max, cancellationToken: ct).ConfigureAwait(false);
                    return Formatting.TextFormatter.FormatTable(result);
                }
                catch (Exception ex)
                {
                    return $"Error executing query: {ex.Message}\n\n" +
                           "Quick start macros:\n" +
                           "  SELECT * FROM xray_documents()  -- File inventory\n" +
                           "  SELECT * FROM xray_items('md_heading,md_code_block', 20)  -- Structure\n" +
                           "  SELECT * FROM snippet('file:///path#line=42', 5)  -- Code window\n" +
                           "  SELECT * FROM entities_by_uri('file:///path#line=10')  -- Resolve URI\n\n" +
                           "Core tables: node (items), edge (relationships), span (locations), annotation (insights)\n" +
                           "Indexed: text/markdown (full AST), text/* (basic)\n\n" +
                           "Discovery: SELECT kind, COUNT(*) FROM node GROUP BY kind";
                }
            },
            options);
    }

    public static McpServerTool CreateSqlTool(IRepoQlClient client)
    {
        var options = new McpServerToolCreateOptions
        {
            Name = "repoql.sql",
            Title = "RepoQL SQL (alias)",
            Description = "Alias for repoql.query.",
            Idempotent = true,
            ReadOnly = true
        };

        return McpServerTool.Create(
            async (string sql, int? limit, CancellationToken ct) =>
            {
                if (string.IsNullOrWhiteSpace(sql))
                    return "No SQL provided. Example: SELECT * FROM xray_documents() LIMIT 10";

                var max = limit.GetValueOrDefault(1000);
                var trimmed = sql.Trim().TrimEnd(';');
                var hasLimit = trimmed.IndexOf(" LIMIT ", StringComparison.OrdinalIgnoreCase) >= 0
                               || trimmed.EndsWith(" LIMIT", StringComparison.OrdinalIgnoreCase)
                               || trimmed.Contains("\nLIMIT ", StringComparison.OrdinalIgnoreCase);
                var limitedSql = hasLimit ? trimmed : $"{trimmed} LIMIT {max}";

                try
                {
                    var result = await client.ExecuteRawQueryAsync(limitedSql, rowLimit: max, cancellationToken: ct).ConfigureAwait(false);
                    return Formatting.TextFormatter.FormatTable(result);
                }
                catch (Exception ex)
                {
                    return $"Error executing query: {ex.Message}";
                }
            },
            options);
    }

    public static McpServerTool CreateXRayTool(IRepoQlClient client)
    {
        var options = new McpServerToolCreateOptions
        {
            Name = "repoql.xray",
            Title = "Repo X-ray",
            Description = "Show headline/summary/structure via glob or search. Params: glob?, search?, level? (auto|headline|summary|structure), limit? (default 50).",
            Idempotent = true,
            ReadOnly = true
        };

        return McpServerTool.Create(
            async (string? glob, string? search, string? level, int? limit, CancellationToken ct) =>
            {
                var k = Math.Max(1, limit.GetValueOrDefault(50));
                try
                {
                    if (!string.IsNullOrWhiteSpace(search))
                    {
                        // Search-backed X-ray: get many to compute omitted, then render top k
                        var all = await client.ExecuteRawQueryAsync(
                            "SELECT uri FROM file_search(?, k := ?, max_cand := 5000);",
                            new object?[] { search!, 5000 }, cancellationToken: ct).ConfigureAwait(false);

                        var total = all.Rows.Count;
                        var topUris = all.Rows.Take(k)
                            .Select(r => r.Values.FirstOrDefault()?.StringValue ?? string.Empty)
                            .Where(s => !string.IsNullOrWhiteSpace(s))
                            .ToList();
                        if (topUris.Count == 0) return "No results.";

                        var placeholders = string.Join(",", Enumerable.Repeat("?", topUris.Count));
                        var sql = $@"SELECT n.uri, a.headline, a.summary, a.structure
                                     FROM node n
                                     JOIN artifact a ON a.id = n.artifact_id
                                     WHERE n.kind = 'document' AND n.uri IN ({placeholders})";
                        var xr = await client.ExecuteRawQueryAsync(sql, topUris.Cast<object?>().ToArray(), cancellationToken: ct).ConfigureAwait(false);

                        var map = xr.Rows.ToDictionary(
                            row => row.Values.Count > 0 ? (row.Values[0].StringValue ?? string.Empty) : string.Empty,
                            row => row,
                            StringComparer.OrdinalIgnoreCase);
                        var ordered = topUris.Where(map.ContainsKey).Select(u => map[u]).ToList();

                        var chosen = ChooseLevel(level, ordered.Count);
                        var text = TextFormatter.FormatXray(ordered, chosen);
                        var omitted = Math.Max(0, total - topUris.Count);
                        return omitted > 0 ? text + $"(" + omitted + " more results omitted)\n" : text;
                    }
                    else
                    {
                        var repoRoot = RepoLocator.FindRepoRoot();
                        var patternText = string.IsNullOrWhiteSpace(glob) ? "**/*" : glob!;
                        var allUris = PatternResolver.ResolvePatterns(patternText, repoRoot);
                        if (allUris.Count == 0) return "No files matched the pattern(s). Try: '**/*.md', 'src/', or 'README.md'";

                        var topUris = allUris.Take(k).ToList();
                        var placeholders = string.Join(",", Enumerable.Repeat("?", topUris.Count));
                        var sql = $@"SELECT n.uri, a.headline, a.summary, a.structure
                                     FROM node n
                                     JOIN artifact a ON a.id = n.artifact_id
                                     WHERE n.kind = 'document' AND n.uri IN ({placeholders})";
                        var xr = await client.ExecuteRawQueryAsync(sql, topUris.Cast<object?>().ToArray(), cancellationToken: ct).ConfigureAwait(false);

                        var map = xr.Rows.ToDictionary(
                            row => row.Values.Count > 0 ? (row.Values[0].StringValue ?? string.Empty) : string.Empty,
                            row => row,
                            StringComparer.OrdinalIgnoreCase);
                        var ordered = topUris.Where(map.ContainsKey).Select(u => map[u]).ToList();

                        var chosen = ChooseLevel(level, ordered.Count);
                        var text = TextFormatter.FormatXray(ordered, chosen);
                        var omitted = Math.Max(0, allUris.Count - topUris.Count);
                        return omitted > 0 ? text + $"(" + omitted + " more results omitted)\n" : text;
                    }
                }
                catch (Exception ex)
                {
                    return $"Error generating X-ray: {ex.Message}";
                }
            },
            options);
    }

    private static int ChooseLevel(string? level, int count)
    {
        // level: auto|headline|summary|structure
        var l = level?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(l) || l == "auto")
        {
            if (count <= 10) return 2;       // structure
            if (count <= 50) return 1;       // summary
            return 0;                        // headline
        }
        return l switch
        {
            "headline" => 0,
            "summary" => 1,
            "structure" => 2,
            _ => 0
        };
    }
}
