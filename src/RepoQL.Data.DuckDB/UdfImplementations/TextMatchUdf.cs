using System.Text.RegularExpressions;
using RepoQL.Contracts;
using RepoQL.Data.DuckDB.UdfFramework;
using RepoQL.FileSystem.Physical;

namespace RepoQL.Data.DuckDB.UdfImplementations;

/// <summary>
/// Purpose: Line-level text search across indexed files, safe at any scale.
/// Complexity: Uses UriRegistry for scope matching and reads files from disk.
/// Never materializes all lines from all files — processes each file independently
/// and stops after max_results, preventing OOM on wide scopes.
/// </summary>
[UdfClass]
public sealed class TextMatchUdf(RepositoryConfiguration repoConfig, UriRegistry uriRegistry)
{
    /// <summary>
    /// Case-insensitive literal text search across indexed files.
    /// </summary>
    [StructuredUdf("_grep_matches_internal",
        MacroName = "grep_matches",
        Description = "Line-level case-insensitive literal text search across indexed files")]
    public IEnumerable<TextMatchRow> GrepMatches(
        string pattern,
        [UdfDefault("NULL")] string? scope,
        [UdfDefault("1000")] int max_results)
    {
        if (string.IsNullOrEmpty(pattern))
            yield break;

        var limit = max_results <= 0 ? int.MaxValue : max_results;
        var emitted = 0;

        foreach (var (uri, content) in EnumerateFileContent(scope))
        {
            var lines = content.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i].TrimEnd('\r');
                if (line.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                {
                    emitted++;
                    if (emitted > limit)
                    {
                        yield return new TextMatchRow(
                            uri, i + 1, line,
                            $"Truncated at {limit} results. Narrow scope or increase max_results.");
                        yield break;
                    }

                    yield return new TextMatchRow(uri, i + 1, line, null);
                }
            }
        }
    }

    /// <summary>
    /// Regex pattern search across indexed files. Case-sensitive by default.
    /// </summary>
    [StructuredUdf("_regex_matches_internal",
        MacroName = "regex_matches",
        Description = "Line-level regex pattern search across indexed files")]
    public IEnumerable<TextMatchRow> RegexMatches(
        string pattern,
        [UdfDefault("NULL")] string? scope,
        [UdfDefault("1000")] int max_results)
    {
        if (string.IsNullOrEmpty(pattern))
            yield break;

        Regex? regex = null;
        string? regexError = null;
        try
        {
            regex = new Regex(pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant,
                TimeSpan.FromSeconds(5));
        }
        catch (ArgumentException ex)
        {
            regexError = ex.Message;
        }

        if (regex is null)
        {
            yield return new TextMatchRow("", 0, "", $"Invalid regex: {regexError}");
            yield break;
        }

        var limit = max_results <= 0 ? int.MaxValue : max_results;
        var emitted = 0;

        foreach (var (uri, content) in EnumerateFileContent(scope))
        {
            var lines = content.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i].TrimEnd('\r');

                bool isMatch;
                TextMatchRow? timeoutRow = null;
                try
                {
                    isMatch = regex.IsMatch(line);
                }
                catch (RegexMatchTimeoutException)
                {
                    isMatch = false;
                    timeoutRow = new TextMatchRow(
                        uri, i + 1, line,
                        $"Regex timed out on {uri} line {i + 1}. Simplify the pattern.");
                }

                if (timeoutRow is not null)
                {
                    yield return timeoutRow;
                    yield break;
                }

                if (isMatch)
                {
                    emitted++;
                    if (emitted > limit)
                    {
                        yield return new TextMatchRow(
                            uri, i + 1, line,
                            $"Truncated at {limit} results. Narrow scope or increase max_results.");
                        yield break;
                    }

                    yield return new TextMatchRow(uri, i + 1, line, null);
                }
            }
        }
    }

    /// <summary>
    /// Yields (uri, content) for each file matching the scope.
    /// Uses UriRegistry for glob matching, reads content from disk.
    /// Memory: one file at a time.
    /// Uses FileUriPathResolver for safe URI-to-path conversion with
    /// percent-decoding and repo-root boundary enforcement.
    /// </summary>
    private IEnumerable<(string Uri, string Content)> EnumerateFileContent(string? scope)
    {
        var matchingUris = uriRegistry.MatchPattern(scope);

        foreach (var repoUri in matchingUris)
        {
            // Only process file:// scheme documents
            if (!string.Equals(repoUri.Scheme, "file", StringComparison.OrdinalIgnoreCase))
                continue;

            string absolutePath;
            try
            {
                absolutePath = FileUriPathResolver.ToAbsolutePath(repoConfig.Path, repoUri);
            }
            catch (InvalidOperationException)
            {
                continue; // URI escapes repo root or wrong scheme — skip
            }

            string content;
            try
            {
                if (!File.Exists(absolutePath))
                    continue;
                content = File.ReadAllText(absolutePath);
            }
            catch
            {
                continue; // Skip files that can't be read
            }

            yield return (repoUri.Container.AbsoluteUri, content);
        }
    }

    public record TextMatchRow(
        string Uri,
        int LineNumber,
        string LineContent,
        string? TruncatedWarning);
}
