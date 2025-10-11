using System;
using System.Linq;
using ConsoleAppFramework;
using RepoQL.ConsoleApp.Helpers;
using RepoQL.Protocol;
using Spectre.Console;

namespace RepoQL.ConsoleApp.Commands;

[RegisterCommands]
internal class TerminalCommands(QueryExecutor queryExecutor, IAnsiConsole console)
{
    /// <summary>
    ///    Queries the structure  repository 
    /// </summary>
    /// <param name="query"></param>
    /// <param name="maxRows"></param>
    /// <param name="format"></param>
    /// <param name="cancel"></param>
    public async Task Query([Argument] string query, int maxRows = 100, ResultFormat format = ResultFormat.Unstructured, CancellationToken cancel = default)
    {
        string[] lines = Array.Empty<string>();
        await console.Status().StartAsync("Launching RepoQL host...", async context =>
        {
            context.Status = "Running query...";
            var result = await queryExecutor.ExecuteAsync(query, maxRows, format, cancel);
            lines = result.Lines;
        });
        foreach (var line in lines) 
            console.WriteLine(line);
    }
    
    /// <summary>
    /// Show x-ray summaries for repository documents filtered by a glob pattern and optional search terms.
    /// </summary>
    /// <param name="pattern">Glob pattern for document URIs (e.g., "src/**/*.md").</param>
    /// <param name="search">Optional search terms; uses the file_search(q) macro for ranking.</param>
    /// <param name="detail">Level of detail: Headline, Summary, Structure, or Full.</param>
    /// <param name="limit">Display limit (default 100). Query returns all; output notes how many were omitted.</param>
    public async Task Xray(
        [Argument] string pattern,
        string? search = null,
        LevelOfDetail? detail = null,
        int limit = 100,
        CancellationToken cancel = default)
    {
        if (limit <= 0) limit = 100;

        var likeFile = BuildLikePattern("file:///", pattern);
        var likeEmbed = BuildLikePattern("embed:///", pattern);

        await using var client = await RepoQlClient.CreateAsync(cancellationToken: cancel);

        // Base query: fetch uri + x-ray fields (not text_content). Do not limit at server; we handle display limit.
        var sql = string.IsNullOrWhiteSpace(search) ?
            """
            SELECT n.uri, a.headline, a.summary, a.structure
                          FROM node n
                          JOIN artifact a ON a.id = n.artifact_id
                          WHERE n.kind='document'
                            AND (lower(n.uri) LIKE ? ESCAPE '\' OR lower(n.uri) LIKE ? ESCAPE '\')
                          ORDER BY lower(n.uri)
            """ :
            """
            WITH s AS (
                             SELECT doc_id, uri, score FROM file_search(?, k := 100000, max_cand := 5000)
                           )
                           SELECT n.uri, a.headline, a.summary, a.structure
                           FROM s
                           JOIN node n ON n.id = s.doc_id
                           JOIN artifact a ON a.id = n.artifact_id
                           WHERE n.kind='document'
                             AND (lower(n.uri) LIKE ? ESCAPE '\' OR lower(n.uri) LIKE ? ESCAPE '\')
                           ORDER BY s.score DESC, length(n.uri)
            """;

        var result = string.IsNullOrWhiteSpace(search)
            ? await client.ExecuteRawQueryAsync(sql, [likeFile, likeEmbed], null, cancel)
            : await client.ExecuteRawQueryAsync(sql, [search!, likeFile, likeEmbed], null, cancel);

        var total = (int)result.RowCount;
        var displayCount = Math.Min(total, limit);

        // Determine detail if not specified
        var lod = detail ?? AutoDetail(displayCount);

        // When Full is selected, we need text_content for top N; fetch it separately for those URIs only.
        Dictionary<string, string?>? fullMap = null;
        if (lod == LevelOfDetail.Full && displayCount > 0)
        {
            var topUris = result.Rows.Take(displayCount)
                .Select(r => r.Values.Count > 0 ? r.Values[0].StringValue ?? string.Empty : string.Empty)
                .Where(u => !string.IsNullOrWhiteSpace(u))
                .ToArray();

            if (topUris.Length > 0)
            {
                var (sqlFull, parameters) = BuildFullContentQuery(topUris);
                var fullResult = await client.ExecuteRawQueryAsync(sqlFull, parameters, null, cancel);
                fullMap = fullResult.Rows.ToDictionary(
                    r => r.Values[0].StringValue ?? string.Empty,
                    r => r.Values.Count > 1 ? r.Values[1].StringValue : null,
                    StringComparer.OrdinalIgnoreCase);
            }
        }

        // Render only the requested x-ray content per item
        var omitted = Math.Max(0, total - displayCount);
        for (int i = 0; i < displayCount && i < result.Rows.Count; i++)
        {
            var row = result.Rows[i].Values;
            var uri = row[0].StringValue ?? string.Empty;
            var headline = row.Count > 1 ? row[1].StringValue : null;
            var summary = row.Count > 2 ? row[2].StringValue : null;
            var structure = row.Count > 3 ? row[3].StringValue : null;

            string? outText = null;
            switch (lod)
            {
                case LevelOfDetail.Headline:
                    outText = headline;
                    break;
                case LevelOfDetail.Summary:
                    outText = summary;
                    break;
                case LevelOfDetail.Structure:
                    outText = structure;
                    break;
                case LevelOfDetail.Full:
                    if (fullMap != null && fullMap.TryGetValue(uri, out var txt)) outText = txt;
                    break;
            }

            if (!string.IsNullOrEmpty(outText))
                WriteBlock(outText!.TrimEnd());
            else
                WriteBlock($"{uri} <Blank {lod}>");

            //if (i < displayCount - 1) console.WriteLine("");
        }

        console.WriteLine(omitted > 0
            ? $"[{displayCount} / {total} items] — {omitted} omitted"
            : $"[{displayCount} / {total} items]");

        // Local helpers
        void WriteBlock(string text)
        {
            text = text.Replace("\r\n", "\n").Replace('\r', '\n');
            foreach (var ln in text.Split('\n')) console.WriteLine(ln);
        }
    }

    private static (string Sql, object?[] Parameters) BuildFullContentQuery(string[] uris)
    {
        // Build a parameterized IN (...) list for the given URIs (case-insensitive compare via lower(uri)).
        var placeholders = string.Join(",", Enumerable.Repeat("?", uris.Length));
        var sql = $@"SELECT n.uri, a.text_content
                    FROM node n
                    JOIN artifact a ON a.id = n.artifact_id
                    WHERE n.kind='document' AND lower(n.uri) IN ({placeholders})";
        var parms = uris.Select(u => (object)u.ToLowerInvariant()).ToArray();
        return (sql, parms);
    }

    private static LevelOfDetail AutoDetail(int displayCount)
        => displayCount <= 5 ? LevelOfDetail.Structure
         : displayCount <= 15 ? LevelOfDetail.Summary
         : LevelOfDetail.Headline;

    private static string BuildLikePattern(string schemePrefix, string glob)
    {
        if (string.IsNullOrWhiteSpace(glob)) glob = "**";
        var p = glob.Replace('\\', '/');
        if (p.StartsWith("file:///", StringComparison.OrdinalIgnoreCase) || p.StartsWith("embed:///", StringComparison.OrdinalIgnoreCase))
        {
            // Keep as-typed
        }
        else
        {
            p = schemePrefix + p.TrimStart('/');
        }

        // Escape LIKE wildcards first, using \\ as ESCAPE
        p = p.Replace("\\", "\\\\");
        p = p.Replace("%", "\\%").Replace("_", "\\_");
        // Translate glob to LIKE
        p = p.Replace("**", "%");
        p = p.Replace("*", "%");
        p = p.Replace("?", "_");
        return p.ToLowerInvariant();
    }
}
