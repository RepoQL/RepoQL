using System.Globalization;
using System.Text;
using RepoQL.Contracts;
using RepoQL.Data.DuckDB;
using RepoQL.Explore;
using RepoQL.Read;

namespace RepoQL.ConsoleApp.Host;

/// <summary>
/// Purpose: Renders git blame for matched files as a read modifier output.
/// Complexity: Aggregates per-line attribution from git_blame() UDF, groups consecutive
/// lines by commit for readability, and enforces token budgets.
/// </summary>
internal sealed class BlameHandler(DuckDbDataStore db, RepositoryConfiguration repoConfig) : IModifierHandler
{
    private readonly DuckDbDataStore _db = db ?? throw new ArgumentNullException(nameof(db));
    private readonly RepositoryConfiguration _repoConfig = repoConfig ?? throw new ArgumentNullException(nameof(repoConfig));

    public string ModifierName => "blame";

    public bool CanHandle(string? modifier)
        => string.Equals(modifier, ModifierName, StringComparison.OrdinalIgnoreCase);

    public Task<ModifierResult> ExecuteAsync(
        IReadOnlyList<ReadDocument> documents,
        string? parameter,
        int tokenBudget,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (documents.Count == 0)
        {
            return Task.FromResult(BuildSimpleResult(
                "No files matched.",
                filesConsulted: [],
                tokenBudget: tokenBudget));
        }

        var fileInfos = ExtractFileInfos(documents);
        if (fileInfos.Count == 0)
        {
            return Task.FromResult(BuildSimpleResult(
                "No valid URIs found in matched documents.",
                filesConsulted: documents.Select(d => d.Uri).ToArray(),
                tokenBudget: tokenBudget));
        }

        if (AllUrisAreFileScheme(fileInfos) && !IsGitRepository(_repoConfig.Path))
        {
            return Task.FromResult(BuildSimpleResult(
                "Not in a git repository.",
                filesConsulted: fileInfos.Select(f => f.Uri).ToArray(),
                tokenBudget: tokenBudget));
        }

        var allBlameLines = new List<BlameLine>();
        var filesConsulted = new List<string>();
        var filesWithNoBlame = new List<string>();
        var fileContents = new Dictionary<string, IReadOnlyDictionary<int, string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var fileInfo in fileInfos)
        {
            ct.ThrowIfCancellationRequested();
            filesConsulted.Add(fileInfo.Uri);

            var blameLines = LoadBlame(fileInfo, ct);
            if (blameLines.Count == 0)
            {
                filesWithNoBlame.Add(fileInfo.Uri);
            }
            else
            {
                allBlameLines.AddRange(blameLines);

                // Fetch file content for this file if not already loaded
                if (!fileContents.ContainsKey(fileInfo.Uri))
                {
                    var lineContent = LoadFileContent(fileInfo.Uri, ct);
                    if (lineContent.Count > 0)
                        fileContents[fileInfo.Uri] = lineContent;
                }
            }
        }

        if (allBlameLines.Count == 0)
        {
            var message = filesWithNoBlame.Count == fileInfos.Count
                ? "No blame available (file not tracked by git or uncommitted)."
                : "No blame data found for matched files.";

            return Task.FromResult(BuildSimpleResult(
                message,
                filesConsulted: filesConsulted,
                tokenBudget: tokenBudget));
        }

        // Group consecutive lines by commit within each file
        var groups = GroupByCommit(allBlameLines);
        var totalLines = allBlameLines.Count;
        var totalCommits = groups.Select(g => g.CommitHash).Distinct(StringComparer.OrdinalIgnoreCase).Count();

        // Build output with budget fitting
        var (content, shownLines, shownCommits) = BuildOutput(groups, fileContents, totalLines, totalCommits, tokenBudget);
        var tokenCount = TokenEstimator.EstimateTokens(content);

        var extra = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["line_count"] = totalLines,
            ["lines_shown"] = shownLines,
            ["commit_count"] = totalCommits,
            ["commits_shown"] = shownCommits
        };

        string? warning = null;
        if (filesWithNoBlame.Count > 0 && filesWithNoBlame.Count < fileInfos.Count)
        {
            warning = $"No blame available for: {string.Join(", ", filesWithNoBlame.Select(ExtractFileName))}";
        }

        return Task.FromResult(new ModifierResult(
            Content: content,
            TokenCount: tokenCount,
            TotalAvailable: totalLines,
            Shown: shownLines,
            ExceedsBudget: tokenCount > tokenBudget,
            Metadata: new ResultMetadata(filesConsulted, warning, extra)));
    }

    private static ModifierResult BuildSimpleResult(
        string message,
        IReadOnlyList<string> filesConsulted,
        int tokenBudget,
        int totalAvailable = 0,
        int shown = 0,
        string? warning = null)
    {
        var tokenCount = TokenEstimator.EstimateTokens(message);
        return new ModifierResult(
            Content: message,
            TokenCount: tokenCount,
            TotalAvailable: totalAvailable,
            Shown: shown,
            ExceedsBudget: tokenCount > tokenBudget,
            Metadata: new ResultMetadata(filesConsulted, warning, new Dictionary<string, object>()));
    }

    private static IReadOnlyList<FileBlameInfo> ExtractFileInfos(IReadOnlyList<ReadDocument> documents)
    {
        var results = new List<FileBlameInfo>();

        foreach (var doc in documents)
        {
            if (string.IsNullOrWhiteSpace(doc.Uri))
                continue;

            if (!RepoUri.TryParse(doc.Uri, out var repoUri))
                continue;

            var containerUri = repoUri.Container.AbsoluteUri;

            // Extract line range from parsed Location
            int? startLine = null;
            int? endLine = null;

            if (repoUri.Loc.Line is { } lineRange)
            {
                startLine = lineRange.Start;
                endLine = lineRange.End;
            }

            results.Add(new FileBlameInfo(containerUri, startLine, endLine));
        }

        return results;
    }

    private static bool AllUrisAreFileScheme(IReadOnlyList<FileBlameInfo> fileInfos)
        => fileInfos.Count > 0 && fileInfos.All(f => f.Uri.StartsWith("file:///", StringComparison.OrdinalIgnoreCase));

    private static bool IsGitRepository(string repoRoot)
        => Directory.Exists(Path.Combine(repoRoot, ".git"));

    private IReadOnlyList<BlameLine> LoadBlame(FileBlameInfo fileInfo, CancellationToken ct)
    {
        var results = new List<BlameLine>();

        // Build SQL for git_blame call
        var escapedUri = EscapeSqlLiteral(fileInfo.Uri);
        string sql;

        if (fileInfo.StartLine.HasValue && fileInfo.EndLine.HasValue)
        {
            sql = $"SELECT * FROM git_blame('{escapedUri}', {fileInfo.StartLine.Value}, {fileInfo.EndLine.Value})";
        }
        else if (fileInfo.StartLine.HasValue)
        {
            sql = $"SELECT * FROM git_blame('{escapedUri}', {fileInfo.StartLine.Value}, NULL)";
        }
        else
        {
            sql = $"SELECT * FROM git_blame('{escapedUri}')";
        }

        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows;
        try
        {
            rows = _db.Query(sql, ct);
        }
        catch (Exception ex) when (ex.Message.Contains("Not a valid git repository") ||
                                   ex.Message.Contains("does not exist"))
        {
            return results;
        }

        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();

            var lineNumber = row.TryGetValue("line_number", out var lineVal)
                ? Convert.ToInt32(lineVal, CultureInfo.InvariantCulture)
                : 0;

            var commitHash = row.TryGetValue("commit_hash", out var hashVal) ? hashVal?.ToString() : null;
            var authorName = row.TryGetValue("author_name", out var authorVal) ? authorVal?.ToString() : null;
            var authorDate = row.TryGetValue("author_date", out var dateVal) ? ParseDate(dateVal) : DateTimeOffset.MinValue;
            var message = row.TryGetValue("message", out var msgVal) ? msgVal?.ToString() : null;

            if (string.IsNullOrWhiteSpace(commitHash))
                continue;

            results.Add(new BlameLine(
                fileInfo.Uri,
                lineNumber,
                commitHash!,
                string.IsNullOrWhiteSpace(authorName) ? "Unknown" : authorName!,
                authorDate,
                message ?? string.Empty));
        }

        return results;
    }

    /// <summary>
    /// Load file content as a dictionary of line number to content.
    /// </summary>
    private IReadOnlyDictionary<int, string> LoadFileContent(string uri, CancellationToken ct)
    {
        var result = new Dictionary<int, string>();

        try
        {
            var escapedUri = EscapeSqlLiteral(uri.ToLowerInvariant());
            var sql = $"""
                SELECT a.text_content
                FROM node n
                JOIN artifact a ON n.artifact_id = a.id
                WHERE (n.container_uri_lowercase = '{escapedUri}' OR lower(n.uri) = '{escapedUri}')
                  AND n.kind = 'document'
                LIMIT 1
                """;
            var rows = _db.Query(sql, ct);

            if (rows.Count == 0)
                return result;

            var textContent = rows[0].TryGetValue("text_content", out var val) ? val?.ToString() : null;
            if (string.IsNullOrEmpty(textContent))
                return result;

            var lines = textContent.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                // Line numbers are 1-based
                result[i + 1] = lines[i].TrimEnd('\r');
            }
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // Ignore errors loading content - blame will just show line numbers
        }

        return result;
    }

    private static IReadOnlyList<BlameGroup> GroupByCommit(IReadOnlyList<BlameLine> lines)
    {
        if (lines.Count == 0)
            return [];

        var groups = new List<BlameGroup>();
        BlameGroup? currentGroup = null;

        // Sort by file, then line number
        var sortedLines = lines
            .OrderBy(l => l.FileUri, StringComparer.OrdinalIgnoreCase)
            .ThenBy(l => l.LineNumber)
            .ToList();

        foreach (var line in sortedLines)
        {
            // Start new group if:
            // - First line
            // - Different file
            // - Different commit
            // - Non-consecutive line number
            var needsNewGroup = currentGroup is null ||
                                !string.Equals(currentGroup.FileUri, line.FileUri, StringComparison.OrdinalIgnoreCase) ||
                                !string.Equals(currentGroup.CommitHash, line.CommitHash, StringComparison.OrdinalIgnoreCase) ||
                                line.LineNumber != currentGroup.EndLine + 1;

            if (needsNewGroup)
            {
                currentGroup = new BlameGroup(
                    line.FileUri,
                    line.CommitHash,
                    line.AuthorName,
                    line.AuthorDate,
                    line.Message,
                    line.LineNumber);
                groups.Add(currentGroup);
            }
            else
            {
                currentGroup!.ExtendTo(line.LineNumber);
            }
        }

        return groups;
    }

    private static (string Content, int ShownLines, int ShownCommits) BuildOutput(
        IReadOnlyList<BlameGroup> groups,
        IReadOnlyDictionary<string, IReadOnlyDictionary<int, string>> fileContents,
        int totalLines,
        int totalCommits,
        int tokenBudget)
    {
        if (groups.Count == 0)
            return (BuildFooter(0, 0, totalLines, totalCommits), 0, 0);

        var builder = new StringBuilder();
        var shownLines = 0;
        var shownCommitHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var includedGroups = new List<BlameGroup>();

        // Try to fit as many groups as possible within budget
        foreach (var group in groups)
        {
            var tentativeContent = BuildTentativeContent(includedGroups, group, fileContents, totalLines, totalCommits);
            var tentativeTokens = TokenEstimator.EstimateTokens(tentativeContent);

            if (tentativeTokens > tokenBudget && includedGroups.Count > 0)
            {
                // Can't fit this group, stop here
                break;
            }

            includedGroups.Add(group);
            shownLines += group.LineCount;
            shownCommitHashes.Add(group.CommitHash);
        }

        // Build final content
        for (var i = 0; i < includedGroups.Count; i++)
        {
            if (i > 0)
                builder.Append('\n');
            builder.Append(FormatGroup(includedGroups[i], fileContents));
        }

        builder.Append("\n\n");
        builder.Append(BuildFooter(shownLines, shownCommitHashes.Count, totalLines, totalCommits));

        return (builder.ToString(), shownLines, shownCommitHashes.Count);
    }

    private static string BuildTentativeContent(
        IReadOnlyList<BlameGroup> existingGroups,
        BlameGroup newGroup,
        IReadOnlyDictionary<string, IReadOnlyDictionary<int, string>> fileContents,
        int totalLines,
        int totalCommits)
    {
        var builder = new StringBuilder();

        for (var i = 0; i < existingGroups.Count; i++)
        {
            if (i > 0)
                builder.Append('\n');
            builder.Append(FormatGroup(existingGroups[i], fileContents));
        }

        if (existingGroups.Count > 0)
            builder.Append('\n');
        builder.Append(FormatGroup(newGroup, fileContents));

        var shownLines = existingGroups.Sum(g => g.LineCount) + newGroup.LineCount;
        var shownCommits = existingGroups
            .Select(g => g.CommitHash)
            .Append(newGroup.CommitHash)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        builder.Append("\n\n");
        builder.Append(BuildFooter(shownLines, shownCommits, totalLines, totalCommits));

        return builder.ToString();
    }

    private static string FormatGroup(
        BlameGroup group,
        IReadOnlyDictionary<string, IReadOnlyDictionary<int, string>> fileContents)
    {
        var builder = new StringBuilder();

        // Commit header: abc123f Alice Developer (2024-01-15) "Fix token expiration"
        var shortHash = group.CommitHash.Length > 7 ? group.CommitHash[..7] : group.CommitHash;
        var date = group.AuthorDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var messageSummary = GetFirstLine(group.Message);

        builder.Append(shortHash);
        builder.Append(' ');
        builder.Append(group.AuthorName);
        builder.Append(" (");
        builder.Append(date);
        builder.Append(") \"");
        builder.Append(messageSummary);
        builder.Append('"');

        // Get file content if available
        fileContents.TryGetValue(group.FileUri, out var lineContents);

        // Line range with code content
        for (var lineNum = group.StartLine; lineNum <= group.EndLine; lineNum++)
        {
            builder.Append('\n');
            builder.Append(' ');
            builder.Append(lineNum.ToString(CultureInfo.InvariantCulture).PadLeft(4));
            builder.Append(": ");

            // Append line content if available
            if (lineContents is not null && lineContents.TryGetValue(lineNum, out var lineText))
            {
                builder.Append(lineText);
            }
        }

        return builder.ToString();
    }

    private static string BuildFooter(int shownLines, int shownCommits, int totalLines, int totalCommits)
    {
        var lineLabel = totalLines == 1 ? "line" : "lines";
        var commitLabel = totalCommits == 1 ? "commit" : "commits";

        if (shownLines == totalLines && shownCommits == totalCommits)
        {
            return $"[{totalLines} {lineLabel}, {totalCommits} {commitLabel}]";
        }

        return $"[{shownLines}/{totalLines} {lineLabel}, {shownCommits}/{totalCommits} {commitLabel}]";
    }

    private static string GetFirstLine(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var newlineIndex = normalized.IndexOf('\n', StringComparison.Ordinal);

        return newlineIndex < 0 ? normalized.Trim() : normalized[..newlineIndex].Trim();
    }

    private static string ExtractFileName(string uri)
    {
        if (string.IsNullOrWhiteSpace(uri))
            return string.Empty;

        var trimmed = uri;
        var hashIndex = trimmed.IndexOf('#', StringComparison.Ordinal);
        if (hashIndex >= 0)
            trimmed = trimmed[..hashIndex];

        var lastSlash = trimmed.LastIndexOf('/');
        if (lastSlash < 0 || lastSlash >= trimmed.Length - 1)
            return trimmed;

        return trimmed[(lastSlash + 1)..];
    }

    private static DateTimeOffset ParseDate(object? value)
    {
        return value switch
        {
            DateTimeOffset dto => dto,
            DateTime dt => new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)),
            string s when DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) => parsed,
            _ => DateTimeOffset.MinValue
        };
    }

    private static string EscapeSqlLiteral(string value)
        => value.Replace("'", "''", StringComparison.Ordinal);

    private sealed record FileBlameInfo(string Uri, int? StartLine, int? EndLine);

    private sealed record BlameLine(
        string FileUri,
        int LineNumber,
        string CommitHash,
        string AuthorName,
        DateTimeOffset AuthorDate,
        string Message);

    /// <summary>
    /// Purpose: Represents a group of consecutive lines from the same commit.
    /// Complexity: Tracks line range for grouping; mutable EndLine for efficient building.
    /// </summary>
    private sealed class BlameGroup
    {
        public BlameGroup(
            string fileUri,
            string commitHash,
            string authorName,
            DateTimeOffset authorDate,
            string message,
            int startLine)
        {
            FileUri = fileUri;
            CommitHash = commitHash;
            AuthorName = authorName;
            AuthorDate = authorDate;
            Message = message;
            StartLine = startLine;
            EndLine = startLine;
        }

        public string FileUri { get; }
        public string CommitHash { get; }
        public string AuthorName { get; }
        public DateTimeOffset AuthorDate { get; }
        public string Message { get; }
        public int StartLine { get; }
        public int EndLine { get; private set; }
        public int LineCount => EndLine - StartLine + 1;

        public void ExtendTo(int lineNumber) => EndLine = lineNumber;
    }
}
