using Google.Protobuf.WellKnownTypes;
using RepoQL.Contracts;
using RepoQL.App.Formatting;
using Spectre.Console;
using Spectre.Console.Cli;

internal sealed class XrayCommand : Command<XraySettings>
{
    public override int Execute(CommandContext context, XraySettings settings)
    {
        var repo = ProgramHelpers.ResolveRepo(settings.Repo);

        // Resolve inputs → URIs (repo-relative file:///) or run search via server
        List<string> uris;
        if (!string.IsNullOrWhiteSpace(settings.Search))
        {
            uris = RunRemoteSearch(repo, settings.Search!, Math.Max(1, settings.Top));
        }
        else
        {
            var resolved = ResolveInputsToUris(repo, settings.Patterns ?? []);
            uris = resolved.Take(Math.Max(1, settings.Top)).ToList();
        }

        if (uris.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey]No results.[/]");
            return 0;
        }

        // Fetch x-ray rows over gRPC for the resolved repository
        var resp = FetchXray(repo, uris);
        var chosen = McpToolFactory.ChooseLevel(settings.Level, Math.Max(uris.Count, resp.Rows.Count));
        var text = TextFormatter.FormatXray(resp, chosen);
        AnsiConsole.WriteLine(text);
        return 0;
    }

    private static List<string> ResolveInputsToUris(string repoRoot, IEnumerable<string> inputs)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in inputs)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var s = raw.Trim();
            if (Uri.TryCreate(s, UriKind.Absolute, out var abs) && !string.IsNullOrEmpty(abs.Scheme))
            {
                if (abs.Scheme.Equals("file", StringComparison.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrEmpty(abs.Host))
                    {
                        var relOnly = abs.LocalPath.TrimStart('/', '\\').Replace('\\', '/');
                        if (!string.IsNullOrEmpty(relOnly)) result.Add($"file:///{relOnly}");
                        else result.Add("file:///");
                    }
                    else
                    {
                        var osPath = abs.LocalPath;
                        var rel = GetRelativeTo(repoRoot, osPath).Replace('\\', '/');
                        if (!string.IsNullOrEmpty(rel)) result.Add($"file:///{rel}");
                        else result.Add(abs.AbsoluteUri);
                    }
                }
                else result.Add(abs.AbsoluteUri);
                continue;
            }

            if (File.Exists(s))
            {
                var full = Path.GetFullPath(s);
                var rel = GetRelativeTo(repoRoot, full).Replace('\\', '/');
                if (!string.IsNullOrEmpty(rel)) result.Add($"file:///{rel}");
                continue;
            }
        }

        // Expand simple globs/directories relative to repo root
        var patterns = inputs.ToArray();
        if (patterns.Length > 0)
        {
            var matcher = new Microsoft.Extensions.FileSystemGlobbing.Matcher(StringComparison.OrdinalIgnoreCase);
            var any = false;
            foreach (var raw in patterns)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                var s = raw.Trim();
                if (IsGlob(s)) { matcher.AddInclude(NormalizePattern(s)); any = true; continue; }
                if (Directory.Exists(s))
                {
                    var rel = GetRelativeTo(repoRoot, Path.GetFullPath(s));
                    var pattern = string.IsNullOrEmpty(rel) ? "**/*" : rel.Replace('\\', '/') + "/**/*";
                    matcher.AddInclude(pattern); any = true; continue;
                }
                var relFile = GetRelativeTo(repoRoot, Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), s)));
                if (!string.IsNullOrEmpty(relFile)) { matcher.AddInclude(relFile.Replace('\\', '/')); any = true; }
            }
            if (any)
            {
                var fs = new Microsoft.Extensions.FileSystemGlobbing.Abstractions.DirectoryInfoWrapper(new DirectoryInfo(repoRoot));
                var matches = matcher.Execute(fs);
                foreach (var m in matches.Files)
                {
                    var full = Path.GetFullPath(Path.Combine(repoRoot, m.Path));
                    if (!File.Exists(full)) continue;
                    var rel = m.Path.Replace('\\', '/');
                    result.Add($"file:///{rel}");
                }
            }
        }

        return result.ToList();
    }

    private static bool IsGlob(string s) => s.IndexOfAny(['*', '?', '[', ']']) >= 0;
    private static string NormalizePattern(string p) => p.Replace('\\', '/');
    private static string GetRelativeTo(string baseDir, string path)
    {
        var b = Path.GetFullPath(baseDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var p = Path.GetFullPath(path);
        return p.StartsWith(b, StringComparison.OrdinalIgnoreCase) ? p[b.Length..] : p;
    }

    private static List<string> RunRemoteSearch(string repo, string query, int top)
    {
        var client = ProgramHelpers.CreateClient(repo);
        try
        {
            var response = client.ExecuteRawQueryAsync(
                "SELECT uri FROM file_search(?, k := ?, max_cand := 5000);",
                new object?[] { query, top }).GetAwaiter().GetResult();
            var list = new List<string>((int)Math.Min(int.MaxValue, response.RowCount));
            foreach (var r in response.Rows)
            {
                var v = r.Values.Count > 0 && r.Values[0].KindCase == Value.KindOneofCase.StringValue ? r.Values[0].StringValue : null;
                if (!string.IsNullOrWhiteSpace(v)) list.Add(v);
            }
            return list;
        }
        finally
        {
            client.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static RawQueryResponse FetchXray(string repo, IReadOnlyList<string> uris)
    {
        var client = ProgramHelpers.CreateClient(repo);
        try
        {
            var placeholders = string.Join(",", Enumerable.Repeat("?", uris.Count));
            var sql = $@"SELECT n.uri, a.headline, a.summary, a.structure
                     FROM node n
                     JOIN artifact a ON a.id = n.artifact_id
                     WHERE n.kind = 'document' AND n.uri IN ({placeholders})";
            var resp = client.ExecuteRawQueryAsync(sql, uris.Cast<object?>().ToArray()).GetAwaiter().GetResult();

            // Order rows to match input URIs using the first response
            var idxUri = Array.FindIndex(resp.Columns.ToArray(), c => string.Equals(c.Name, "uri", StringComparison.OrdinalIgnoreCase));
            var map = new Dictionary<string, RowData>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in resp.Rows)
            {
                var key = (idxUri >= 0 && row.Values.Count > idxUri && row.Values[idxUri].KindCase == Value.KindOneofCase.StringValue)
                    ? (row.Values[idxUri].StringValue ?? string.Empty)
                    : string.Empty;
                if (!string.IsNullOrWhiteSpace(key)) map[key] = row;
            }
            var ordered = new List<RowData>(Math.Min(uris.Count, map.Count));
            foreach (var u in uris)
            {
                if (map.TryGetValue(u, out var r)) ordered.Add(r);
            }
            resp.Rows.Clear();
            resp.Rows.AddRange(ordered);
            return resp;
        }
        finally
        {
            client.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }
}
