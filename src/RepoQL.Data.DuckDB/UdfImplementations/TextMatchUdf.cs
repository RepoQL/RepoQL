using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using RepoQL.Contracts;
using RepoQL.Data.DuckDB.UdfFramework;
using RepoQL.FileSystem.Abstractions;
using RepoQL.FileSystem.Physical;

namespace RepoQL.Data.DuckDB.UdfImplementations;

/// <summary>
/// Purpose: Line-level text search across indexed files, safe at any scale.
/// Complexity: Uses UriRegistry for scope matching and reads one file at a time through
/// the mounted file systems (falling back to local file:// reads when needed).
/// Never materializes all lines from all files — processes each file independently
/// and stops after max_results, preventing OOM on wide scopes.
/// Regex stays streaming for line-local patterns and falls back to full-document
/// evaluation only when the pattern may span newlines.
/// </summary>
[UdfClass]
public sealed class TextMatchUdf(
    RepositoryConfiguration repoConfig,
    UriRegistry uriRegistry,
    IMultiFileSystem? fileSystem = null)
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

        foreach (var repoUri in uriRegistry.MatchPattern(scope))
        {
            if (!TryOpenTextReader(repoUri, out var reader))
                continue;

            using (reader)
            {
                var uri = repoUri.Container.AbsoluteUri;
                string? line;
                var lineNumber = 0;

                while ((line = reader.ReadLine()) is not null)
                {
                    lineNumber++;

                    if (!line.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                        continue;

                    emitted++;
                    if (emitted > limit)
                    {
                        yield return new TextMatchRow(
                            uri, lineNumber, line,
                            $"Truncated at {limit} results. Narrow scope or increase max_results.");
                        yield break;
                    }

                    yield return new TextMatchRow(uri, lineNumber, line, null);
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
        var maySpanLines = RegexMaySpanLines(pattern);

        foreach (var repoUri in uriRegistry.MatchPattern(scope))
        {
            if (!TryOpenTextReader(repoUri, out var reader))
                continue;

            using (reader)
            {
                var uri = repoUri.Container.AbsoluteUri;

                if (maySpanLines)
                {
                    var content = reader.ReadToEnd();
                    var newlinePositions = BuildNewlinePositions(content);
                    var matchedLines = new HashSet<int>();

                    MatchCollection? matches = null;
                    string? timeoutWarning = null;
                    try
                    {
                        matches = regex.Matches(content);
                    }
                    catch (RegexMatchTimeoutException)
                    {
                        timeoutWarning = $"Regex timed out while scanning {uri}. Simplify the pattern.";
                    }

                    if (timeoutWarning is not null)
                    {
                        yield return new TextMatchRow(uri, 0, "", timeoutWarning);
                        yield break;
                    }

                    foreach (Match match in matches!)
                    {
                        if (!match.Success)
                            continue;

                        var lineIndex = GetLineIndexAtCharPosition(newlinePositions, match.Index);
                        if (!matchedLines.Add(lineIndex))
                            continue;

                        var line = GetLineText(content, newlinePositions, lineIndex);
                        var lineNumber = lineIndex + 1;

                        emitted++;
                        if (emitted > limit)
                        {
                            yield return new TextMatchRow(
                                uri, lineNumber, line,
                                $"Truncated at {limit} results. Narrow scope or increase max_results.");
                            yield break;
                        }

                        yield return new TextMatchRow(uri, lineNumber, line, null);
                    }
                    continue;
                }

                string? lineText;
                var lineNumberStreaming = 0;
                while ((lineText = reader.ReadLine()) is not null)
                {
                    lineNumberStreaming++;

                    var isMatch = false;
                    string? lineTimeoutWarning = null;
                    try
                    {
                        isMatch = regex.IsMatch(lineText);
                    }
                    catch (RegexMatchTimeoutException)
                    {
                        lineTimeoutWarning =
                            $"Regex timed out on {uri} line {lineNumberStreaming}. Simplify the pattern.";
                    }

                    if (lineTimeoutWarning is not null)
                    {
                        yield return new TextMatchRow(uri, lineNumberStreaming, lineText, lineTimeoutWarning);
                        yield break;
                    }

                    if (!isMatch)
                        continue;

                    emitted++;
                    if (emitted > limit)
                    {
                        yield return new TextMatchRow(
                            uri, lineNumberStreaming, lineText,
                            $"Truncated at {limit} results. Narrow scope or increase max_results.");
                        yield break;
                    }

                    yield return new TextMatchRow(uri, lineNumberStreaming, lineText, null);
                }
            }
        }
    }

    private static bool RegexMaySpanLines(string pattern)
    {
        return pattern.Contains('\n') ||
               pattern.Contains(@"\n", StringComparison.Ordinal) ||
               pattern.Contains(@"\r", StringComparison.Ordinal) ||
               pattern.Contains(@"\R", StringComparison.Ordinal) ||
               pattern.Contains(@"(?s", StringComparison.Ordinal) ||
               pattern.Contains(@"\s", StringComparison.Ordinal) ||
               pattern.Contains(@"[\s\S]", StringComparison.Ordinal) ||
               pattern.Contains(@"[\S\s]", StringComparison.Ordinal);
    }

    private static int[] BuildNewlinePositions(string content)
    {
        if (string.IsNullOrEmpty(content))
            return [];

        var positions = new List<int>(capacity: Math.Min(1024, content.Length / 20));
        for (var i = 0; i < content.Length; i++)
        {
            if (content[i] == '\n')
                positions.Add(i);
        }
        return [.. positions];
    }

    private static int GetLineIndexAtCharPosition(IReadOnlyList<int> newlinePositions, int charPosition)
    {
        var low = 0;
        var high = newlinePositions.Count;

        while (low < high)
        {
            var mid = low + ((high - low) / 2);
            if (newlinePositions[mid] < charPosition)
                low = mid + 1;
            else
                high = mid;
        }

        return low;
    }

    private static string GetLineText(string content, IReadOnlyList<int> newlinePositions, int lineIndex)
    {
        var start = lineIndex == 0 ? 0 : newlinePositions[lineIndex - 1] + 1;
        var end = lineIndex < newlinePositions.Count ? newlinePositions[lineIndex] : content.Length;
        var length = Math.Max(0, end - start);
        return content.Substring(start, length).TrimEnd('\r');
    }

    private bool TryOpenTextReader(RepoUri uri, [NotNullWhen(true)] out TextReader? reader)
    {
        if (TryOpenReaderViaMountedFileSystems(uri, out reader))
            return true;

        if (TryOpenReaderFromLocalFile(uri, out reader))
            return true;

        reader = null;
        return false;
    }

    private bool TryOpenReaderViaMountedFileSystems(RepoUri uri, [NotNullWhen(true)] out TextReader? reader)
    {
        reader = null;
        if (fileSystem is null)
            return false;

        Stream? stream = null;
        try
        {
            var fileInfo = fileSystem.GetFile(uri);
            if (!fileInfo.Exists || fileInfo.IsDirectory)
                return false;

            stream = fileInfo.CreateReadStream();
            reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
            stream = null;
            return true;
        }
        catch
        {
            stream?.Dispose();
            reader?.Dispose();
            reader = null;
            return false; // Skip resources that can't be opened from mounted stores
        }
    }

    private bool TryOpenReaderFromLocalFile(RepoUri uri, [NotNullWhen(true)] out TextReader? reader)
    {
        reader = null;

        if (!string.Equals(uri.Scheme, "file", StringComparison.OrdinalIgnoreCase))
            return false;

        string absolutePath;
        try
        {
            absolutePath = FileUriPathResolver.ToAbsolutePath(repoConfig.Path, uri);
            if (!File.Exists(absolutePath))
                return false;
        }
        catch (InvalidOperationException)
        {
            return false; // URI escapes repo root or wrong scheme
        }
        catch
        {
            return false; // Skip files that can't be stat'ed
        }

        FileStream? stream = null;
        try
        {
            stream = new FileStream(
                absolutePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
            stream = null;
            return true;
        }
        catch
        {
            stream?.Dispose();
            reader?.Dispose();
            reader = null;
            return false; // Skip files that can't be opened
        }
    }

    public record TextMatchRow(
        string Uri,
        int LineNumber,
        string LineContent,
        string? TruncatedWarning);
}
