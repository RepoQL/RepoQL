using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.FileSystemGlobbing.Abstractions;
using RepoQL.Contracts;

namespace RepoQL.Cli.Commands;

internal static class XrayCommand
{
    public static Command Build()
    {
        var cmd = new Command("xray", "X-ray document summaries for URIs, file paths, or glob patterns");
        var inputsArg = new Argument<string[]>("inputs", description: "URIs, file paths, or glob patterns")
        {
            Arity = ArgumentArity.ZeroOrMore
        };
        cmd.AddArgument(inputsArg);

        // Level of detail: auto|0|1|2|headline|summary|structure
        var levelOpt = new Option<string?>(
            name: "--level",
            description: "Level of detail: auto|0|1|2|headline|summary|structure (default: auto)",
            getDefaultValue: () => "auto");
        var jsonOpt = new Option<bool>(
            name: "--json",
            description: "Output JSON instead of Spectre text",
            getDefaultValue: () => false);
        var searchOpt = new Option<string?>(
            name: "--search",
            description: "Full-text search query for documents (uses file_search macro)");
        var topOpt = new Option<int?>(
            name: "--top",
            description: "Maximum number of search results (default: 50)");

        cmd.AddOption(levelOpt);
        cmd.AddOption(jsonOpt);
        cmd.AddOption(searchOpt);
        cmd.AddOption(topOpt);

        cmd.SetHandler(async (string[] inputs, string? level, bool json, string? searchQuery, int? top) =>
        {
            if (!string.IsNullOrWhiteSpace(searchQuery))
            {
                var topCount = top.HasValue && top.Value > 0 ? top.Value : 50;
                await RunSearchAsync(searchQuery!, topCount, json, level).ConfigureAwait(false);
                return;
            }

            var uris = ResolveInputsToUris(inputs);
            if (uris.Count == 0)
            {
                Console.Error.WriteLine("No inputs matched files or URIs.");
                return;
            }

            var rows = await FetchXrayAsync(uris).ConfigureAwait(false);
            var chosen = ChooseLevel(level, rows.Count);
            if (json)
                WriteXrayJson(rows, chosen);
            else
                WriteXrayTable(rows, chosen);
        }, inputsArg, levelOpt, jsonOpt, searchOpt, topOpt);

        return cmd;
    }

    private static List<string> ResolveInputsToUris(IEnumerable<string> inputs)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var repoRoot = RepoLocator.FindRepoRoot();
        // 1) Collect explicit URIs and direct file paths
        foreach (var raw in inputs)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var s = raw.Trim();
            if (Uri.TryCreate(s, UriKind.Absolute, out var abs) && !string.IsNullOrEmpty(abs.Scheme))
            {
                // Normalize file:/// URIs
                if (abs.Scheme.Equals("file", StringComparison.OrdinalIgnoreCase))
                {
                    // file:///relative -> repo-aware
                    if (string.IsNullOrEmpty(abs.Host))
                    {
                        var relOnly = abs.LocalPath.TrimStart('/', '\\').Replace('\\', '/');
                        if (!string.IsNullOrEmpty(relOnly))
                        {
                            result.Add($"file:///{relOnly}");
                        }
                        else
                        {
                            result.Add("file:///");
                        }
                    }
                    else
                    {
                        // file://host/abs or file:///C:/... -> map under repo root when possible
                        var osPath = abs.LocalPath; // already unescaped
                        var rel = GetRelativeTo(repoRoot, osPath).Replace('\\', '/');
                        if (!string.IsNullOrEmpty(rel))
                        {
                            result.Add($"file:///{rel}");
                        }
                        else
                        {
                            result.Add(abs.AbsoluteUri);
                        }
                    }
                }
                else
                {
                    result.Add(abs.AbsoluteUri);
                }
                continue;
            }

            // Direct file path? Convert to repo-aware file:///rel URI
            if (File.Exists(s))
            {
                var full = Path.GetFullPath(s);
                var rel = GetRelativeTo(repoRoot, full).Replace('\\', '/');
                if (!string.IsNullOrEmpty(rel))
                {
                    var repoUri = $"file:///{rel}";
                    result.Add(repoUri);
                }
                continue;
            }
        }

        // 2) Expand directories and glob patterns relative to repo root
        var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
        var anyPatterns = false;
        foreach (var raw in inputs)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var s = raw.Trim();

            if (IsGlob(s))
            {
                matcher.AddInclude(NormalizePattern(s));
                anyPatterns = true;
                continue;
            }

            if (Directory.Exists(s))
            {
                var rel = GetRelativeTo(repoRoot, Path.GetFullPath(s));
                var pattern = string.IsNullOrEmpty(rel) ? "**/*" : rel.Replace('\\', '/') + "/**/*";
                matcher.AddInclude(pattern);
                anyPatterns = true;
                continue;
            }

            // Non-glob, non-directory: treat as repo-relative file path pattern
            var relFile = GetRelativeTo(repoRoot, Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), s)));
            if (!string.IsNullOrEmpty(relFile))
            {
                matcher.AddInclude(relFile.Replace('\\', '/'));
                anyPatterns = true;
            }
        }

        if (anyPatterns)
        {
            var dir = new DirectoryInfo(repoRoot);
            var fs = new DirectoryInfoWrapper(dir);
            var matches = matcher.Execute(fs);
            foreach (var m in matches.Files)
            {
                var full = Path.GetFullPath(Path.Combine(repoRoot, m.Path));
                if (!File.Exists(full)) continue;
                var rel = m.Path.Replace('\\', '/');
                var repoUri = $"file:///{rel}";
                result.Add(repoUri);
            }
        }

        return result.ToList();
    }

    private readonly record struct SearchHit(string Uri, double Score, double Bm25Normalized, double FuzzNormalized);

    private static async Task RunSearchAsync(string query, int top, bool json, string? level)
    {
        await using var client = RepoQlClient.Create(new RepoQlClientOptions());
        var response = await client.ExecuteRawQueryAsync(
            "SELECT uri, score, bm25n, fuzzn FROM file_search(?, k := ?, max_cand := 5000);",
            new object?[] { query, top }).ConfigureAwait(false);

        var hits = ParseSearchHits(response);
        // Fetch x-ray for the URIs in the same order as hits
        var uris = hits.Select(h => h.Uri).ToList();
        var rows = await FetchXrayAsync(uris).ConfigureAwait(false);
        var ordered = OrderByUris(rows, uris);
        var chosen = ChooseLevel(level, ordered.Count);
        if (json) WriteXrayJson(ordered, chosen); else WriteXrayTable(ordered, chosen);
    }

    private static IReadOnlyList<SearchHit> ParseSearchHits(RawQueryResponse response)
    {
        var hits = new List<SearchHit>(response.Rows.Count);
        foreach (var row in response.Rows)
        {
            var values = row.Values;
            var uri = GetString(values, 0);
            var score = GetNumber(values, 1);
            var bm25 = GetNumber(values, 2);
            var fuzz = GetNumber(values, 3);
            hits.Add(new SearchHit(uri, score, bm25, fuzz));
        }
        return hits;
    }

    private static void WriteSearchTable(IReadOnlyList<SearchHit> hits)
    {
        if (hits.Count == 0)
        {
            Console.WriteLine("No results.");
            return;
        }

        Console.WriteLine("Score    BM25    Fuzz    URI");
        foreach (var hit in hits)
        {
            Console.WriteLine($"{hit.Score,6:0.000}  {hit.Bm25Normalized,6:0.000}  {hit.FuzzNormalized,6:0.000}  {hit.Uri}");
        }
    }

    private static void WriteSearchJson(IReadOnlyList<SearchHit> hits)
    {
        using var output = Console.OpenStandardOutput();
        using var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true });
        writer.WriteStartArray();
        foreach (var hit in hits)
        {
            writer.WriteStartObject();
            writer.WriteString("uri", hit.Uri);
            writer.WriteNumber("score", hit.Score);
            writer.WriteNumber("bm25n", hit.Bm25Normalized);
            writer.WriteNumber("fuzzn", hit.FuzzNormalized);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.Flush();
    }

    private static string GetString(IReadOnlyList<Value> values, int index)
    {
        if (index >= values.Count) return string.Empty;
        var value = values[index];
        return value.KindCase switch
        {
            Value.KindOneofCase.StringValue => value.StringValue ?? string.Empty,
            Value.KindOneofCase.NumberValue => value.NumberValue.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Value.KindOneofCase.BoolValue => value.BoolValue ? "true" : "false",
            _ => string.Empty
        };
    }

    private static double GetNumber(IReadOnlyList<Value> values, int index)
    {
        if (index >= values.Count) return 0;
        var value = values[index];
        return value.KindCase == Value.KindOneofCase.NumberValue ? value.NumberValue : 0;
    }

    private static bool IsGlob(string s)
    {
        return s.IndexOfAny(['*', '?', '[', ']']) >= 0;
    }

    private static string NormalizePattern(string p)
    {
        return p.Replace('\\', '/');
    }

    private static string GetRelativeTo(string baseDir, string path)
    {
        var b = Path.GetFullPath(baseDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var p = Path.GetFullPath(path);
        return p.StartsWith(b, StringComparison.OrdinalIgnoreCase) ? p[b.Length..] : p;
    }

    private readonly record struct XrayRow(string Uri, string? Headline, string? Summary, string? Structure);

    private static async Task<List<XrayRow>> FetchXrayAsync(IReadOnlyList<string> uris)
    {
        if (uris.Count == 0) return new List<XrayRow>();
        await using var client = RepoQlClient.Create(new RepoQlClientOptions());
        // Build IN clause with parameters
        var placeholders = string.Join(",", Enumerable.Repeat("?", uris.Count));
        var sql = $@"SELECT n.uri, a.headline, a.summary, a.structure
                     FROM node n
                     JOIN artifact a ON a.id = n.artifact_id
                     WHERE n.kind = 'document' AND n.uri IN ({placeholders})";
        var param = uris.Cast<object?>().ToArray();
        var resp = await client.ExecuteRawQueryAsync(sql, param).ConfigureAwait(false);
        var list = new List<XrayRow>(resp.Rows.Count);
        foreach (var row in resp.Rows)
        {
            var vals = row.Values;
            var uri = GetString(vals, 0);
            var headline = GetString(vals, 1);
            var summary = GetString(vals, 2);
            var structure = GetString(vals, 3);
            list.Add(new XrayRow(uri, string.IsNullOrWhiteSpace(headline) ? null : headline,
                                      string.IsNullOrWhiteSpace(summary) ? null : summary,
                                      string.IsNullOrWhiteSpace(structure) ? null : structure));
        }
        return list;
    }

    private static List<XrayRow> OrderByUris(List<XrayRow> rows, IReadOnlyList<string> uris)
    {
        var order = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < uris.Count; i++) order[uris[i]] = i;
        return rows.OrderBy(r => order.GetValueOrDefault(r.Uri, int.MaxValue)).ToList();
    }

    private static int ChooseLevel(string? level, int count)
    {
        if (string.IsNullOrWhiteSpace(level) || level!.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            if (count <= 10) return 2;      // Few results → structure
            if (count <= 50) return 1;      // Some results → summary
            return 0;                        // Many results → headline
        }
        return level.ToLowerInvariant() switch
        {
            "0" or "headline" => 0,
            "1" or "summary" => 1,
            "2" or "structure" => 2,
            _ => 0
        };
    }

    private static void WriteXrayTable(IReadOnlyList<XrayRow> rows, int level)
    {
        if (rows.Count == 0)
        {
            Console.WriteLine("No results.");
            return;
        }
        foreach (var r in rows)
        {
            Console.WriteLine(r.Uri);
            var text = level switch
            {
                2 => r.Structure ?? r.Summary ?? r.Headline ?? string.Empty,
                1 => r.Summary ?? r.Structure ?? r.Headline ?? string.Empty,
                _ => r.Headline ?? r.Summary ?? r.Structure ?? string.Empty
            };
            if (string.IsNullOrWhiteSpace(text))
            {
                Console.WriteLine("  (no x-ray content)\n");
                continue;
            }
            foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                Console.WriteLine(line);
            }
            Console.WriteLine();
        }
    }

    private static void WriteXrayJson(IReadOnlyList<XrayRow> rows, int level)
    {
        using var output = Console.OpenStandardOutput();
        using var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true });
        writer.WriteStartArray();
        foreach (var r in rows)
        {
            writer.WriteStartObject();
            writer.WriteString("uri", r.Uri);
            writer.WriteNumber("level", level);
            if (r.Headline is not null) writer.WriteString("headline", r.Headline);
            if (r.Summary is not null) writer.WriteString("summary", r.Summary);
            if (r.Structure is not null) writer.WriteString("structure", r.Structure);
            writer.WriteString("text", level switch
            {
                2 => r.Structure ?? r.Summary ?? r.Headline ?? string.Empty,
                1 => r.Summary ?? r.Structure ?? r.Headline ?? string.Empty,
                _ => r.Headline ?? r.Summary ?? r.Structure ?? string.Empty
            });
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.Flush();
    }
}
