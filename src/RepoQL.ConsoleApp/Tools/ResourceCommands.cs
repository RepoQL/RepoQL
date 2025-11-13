using ModelContextProtocol.Protocol;
using RepoQL.ConsoleApp.Helpers;
using RepoQL.ConsoleApp.Resources;
using Spectre.Console;
using ConsoleAppFramework;
using RepoQL.ConsoleApp.Commands;

namespace RepoQL.ConsoleApp.Tools;

[RegisterCommands]
internal class ResourceCommands
{
    private readonly QueryExecutor _queryExecutor;
    private readonly RepoQlClientProvider _clientProvider;
    private readonly IAnsiConsole _console;
    private readonly RepoResourceService _resourceService;

    public ResourceCommands(QueryExecutor queryExecutor, RepoQlClientProvider clientProvider, IAnsiConsole console, RepoResourceService resourceService)
    {
        _queryExecutor = queryExecutor;
        _clientProvider = clientProvider;
        _console = console;
        _resourceService = resourceService;
        _ = _clientProvider.EnsureStarted();
    }
    
    /// <summary>
    /// Show x-ray summaries for repository documents filtered by a glob pattern plus optional search keywords/question.
    /// </summary>
    /// <param name="pattern">Glob pattern for document URIs (e.g., "src/**/*.md").</param>
    /// <param name="keywords">Literal filename or symbol filters passed to file_search keywords.</param>
    /// <param name="question">Optional natural-language question passed to file_search.</param>
    /// <param name="detail">Level of detail: Headline, Summary, Structure, or Full.</param>
    /// <param name="limit">Display limit (default 100). Query returns all; output notes how many were omitted.</param>
    public async Task Xray(
        [Argument] string pattern,
        string? keywords = null,
        string? question = null,
        LevelOfDetail? detail = null,
        int limit = 100,
        CancellationToken cancel = default)
    {
        if (limit <= 0) limit = 100;

        var globPattern = NormalizeGlobPattern(pattern);

        var client = await _clientProvider.GetClientAsync(cancel).ConfigureAwait(false);

        var keywordsText = keywords?.Trim();
        var questionText = question?.Trim();
        var hasSearch = !string.IsNullOrEmpty(keywordsText) || !string.IsNullOrEmpty(questionText);

        var whereClauses = new List<string> { "n.kind='document'" };
        var whereParameters = new List<object?>();
        if (!string.IsNullOrEmpty(globPattern))
        {
            whereClauses.Add("(glob_match(n.uri, ?, default_scheme := 'file:///') OR glob_match(n.uri, ?, default_scheme := 'docs:///') OR glob_match(n.uri, ?, default_scheme := 'embed:///'))");
            whereParameters.Add(globPattern);
            whereParameters.Add(globPattern);
            whereParameters.Add(globPattern);
        }

        // Base query: fetch uri + x-ray fields (not text_content). Do not limit at server; we handle display limit.
        var sql = !hasSearch ?
            """
            SELECT n.uri, a.headline, a.summary, a.structure
                          FROM node n
                          JOIN artifact a ON a.id = n.artifact_id
                          WHERE {WHERE_CLAUSE}
                          ORDER BY lower(n.uri)
            """ :
            """
            WITH s AS (
                             SELECT doc_id, uri, score FROM file_search(?, k := 100000, max_cand := 5000, question := ?)
                           )
                           SELECT n.uri, a.headline, a.summary, a.structure
                           FROM s
                           JOIN node n ON n.id = s.doc_id
                           JOIN artifact a ON a.id = n.artifact_id
                           WHERE {WHERE_CLAUSE}
                           ORDER BY s.score DESC, length(n.uri)
            """;
        sql = sql.Replace("{WHERE_CLAUSE}", string.Join(" AND ", whereClauses));

        object?[] parameters;
        if (!hasSearch)
        {
            parameters = whereParameters.ToArray();
        }
        else
        {
            var keywordParam = keywordsText ?? string.Empty;
            object? questionParam = string.IsNullOrEmpty(questionText) ? null : questionText;
            parameters = [keywordParam, questionParam, .. whereParameters];
        }

        var result = await client.ExecuteRawQueryAsync(sql, parameters, null, cancel);

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
                var (sqlFull, fullParameters) = BuildFullContentQuery(topUris);
                var fullResult = await client.ExecuteRawQueryAsync(sqlFull, fullParameters, null, cancel);
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

        _console.WriteLine(omitted > 0
            ? $"[{displayCount} / {total} items] — {omitted} omitted"
            : $"[{displayCount} / {total} items]");

        // Local helpers
        void WriteBlock(string text)
        {
            text = text.Replace("\r\n", "\n").Replace('\r', '\n');
            foreach (var ln in text.Split('\n')) _console.WriteLine(ln);
        }
    }

    /// <summary>
    /// Fetches repository content or summaries using the same logic as MCP resources.
    /// </summary>
    /// <param name="uri">RepoURI or prefixed template (e.g., summarize:file:///...)</param>
    /// <param name="cancel">Cancellation token.</param>
    public async Task Resource(
        [Argument] string uri,
        CancellationToken cancel = default)
    {
        if (string.IsNullOrWhiteSpace(uri))
        {
            _console.MarkupLine("[red]Resource URI is required.[/]");
            return;
        }

        TextResourceContents? content = null;
        try
        {
            await _console.Status().StartAsync("Fetching resource...", async context =>
            {
                context.Status = "Calling RepoQL...";
                content = await _resourceService.FetchResourceAsync(uri, cancel).ConfigureAwait(false);
            });
        }
        catch (Exception ex)
        {
            _console.MarkupLine($"[red]{ex.Message}[/]");
            return;
        }

        if (content is null)
        {
            _console.MarkupLine("[red]No resource content returned.[/]");
            return;
        }

        var text = content.Text ?? string.Empty;
        if (string.IsNullOrEmpty(text))
        {
            _console.WriteLine("(empty content)");
            return;
        }

        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            _console.WriteLine(line);
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

    private static string? NormalizeGlobPattern(string? glob) =>
        string.IsNullOrWhiteSpace(glob) ? null : glob.Trim();
}
