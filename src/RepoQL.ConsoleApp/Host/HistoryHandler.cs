using System.Globalization;
using System.Text;
using RepoQL.Contracts;
using RepoQL.Data.DuckDB;
using RepoQL.Explore;

namespace RepoQL.ConsoleApp.Host;

/// <summary>
/// Purpose: Renders git history for matched files as a read modifier output.
/// Complexity: Aggregates indexed history into commit-centric summaries while enforcing token budgets.
/// </summary>
internal sealed class HistoryHandler(DuckDbDataStore db, RepositoryConfiguration repoConfig) : IModifierHandler
{
    private readonly DuckDbDataStore _db = db ?? throw new ArgumentNullException(nameof(db));
    private readonly RepositoryConfiguration _repoConfig = repoConfig ?? throw new ArgumentNullException(nameof(repoConfig));

    public string ModifierName => "history";

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
                filesConsulted: Array.Empty<string>(),
                tokenBudget: tokenBudget));
        }

        var fileUris = ExtractFileUris(documents);
        if (fileUris.Count == 0)
        {
            return Task.FromResult(BuildSimpleResult(
                "History is only available for document URIs.",
                filesConsulted: documents.Select(d => d.Uri).ToArray(),
                tokenBudget: tokenBudget));
        }

        var commits = LoadHistory(fileUris, ct);
        if (commits.Count == 0)
        {
            if (AllUrisAreFileScheme(fileUris) && !IsGitRepository(_repoConfig.Path))
            {
                return Task.FromResult(BuildSimpleResult(
                    "Not in a git repository.",
                    filesConsulted: fileUris,
                    tokenBudget: tokenBudget));
            }

            return Task.FromResult(BuildSimpleResult(
                "No commits found for matched files.",
                filesConsulted: fileUris,
                tokenBudget: tokenBudget));
        }

        var totalAvailable = commits.Count;
        var keyword = parameter?.Trim();
        var warning = (string?)null;
        var usedRelevance = false;

        IReadOnlyList<HistoryCommit> ordered;
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var terms = NormalizeKeywords(keyword);
            if (terms.Count == 0)
            {
                warning = $"No matches for keywords: {keyword}. Showing recent history.";
                ordered = commits.OrderByDescending(c => c.AuthorDate).ToList();
            }
            else
            {
                var scored = commits
                    .Select(commit => new CommitScore(commit, ScoreCommit(commit, keyword, terms)))
                    .ToList();

                var maxScore = scored.Max(s => s.Score);
                if (maxScore <= 0)
                {
                    warning = $"No matches for keywords: {keyword}. Showing recent history.";
                    ordered = commits.OrderByDescending(c => c.AuthorDate).ToList();
                }
                else
                {
                    usedRelevance = true;
                    ordered = scored
                        .OrderByDescending(s => s.Score)
                        .ThenByDescending(s => s.Commit.AuthorDate)
                        .Select(s => s.Commit)
                        .ToList();
                }
            }
        }
        else
        {
            ordered = commits.OrderByDescending(c => c.AuthorDate).ToList();
        }

        var blocks = new List<string>(ordered.Count);
        foreach (var commit in ordered)
        {
            ct.ThrowIfCancellationRequested();
            var block = FitCommitBlock(commit, tokenBudget);
            if (block is null)
                continue;

            blocks.Add(block);
        }

        string content;
        while (blocks.Count > 0)
        {
            content = BuildContent(blocks, totalAvailable, usedRelevance);
            if (TokenEstimator.EstimateTokens(content) <= tokenBudget)
                break;

            blocks.RemoveAt(blocks.Count - 1);
        }

        if (blocks.Count == 0)
        {
            return Task.FromResult(BuildSimpleResult(
                "History output exceeds token budget. Increase tokenBudget to see commits.",
                filesConsulted: fileUris,
                tokenBudget: tokenBudget,
                totalAvailable: totalAvailable,
                warning: warning));
        }

        content = BuildContent(blocks, totalAvailable, usedRelevance);
        var tokenCount = TokenEstimator.EstimateTokens(content);

        var extra = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["commit_count"] = totalAvailable,
            ["commits_shown"] = blocks.Count
        };

        return Task.FromResult(new ModifierResult(
            Content: content,
            TokenCount: tokenCount,
            TotalAvailable: totalAvailable,
            Shown: blocks.Count,
            ExceedsBudget: tokenCount > tokenBudget,
            Metadata: new ResultMetadata(fileUris, warning, extra)));
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

    private static IReadOnlyList<string> ExtractFileUris(IReadOnlyList<ReadDocument> documents)
    {
        var uris = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var doc in documents)
        {
            if (string.IsNullOrWhiteSpace(doc.Uri))
                continue;

            if (RepoUri.TryParse(doc.Uri, out var repoUri))
            {
                uris.Add(repoUri.Container.AbsoluteUri);
                continue;
            }

            var hashIndex = doc.Uri.IndexOf('#', StringComparison.Ordinal);
            uris.Add(hashIndex >= 0 ? doc.Uri[..hashIndex] : doc.Uri);
        }

        return uris.ToList();
    }

    private static bool AllUrisAreFileScheme(IReadOnlyList<string> uris)
        => uris.Count > 0 && uris.All(uri => uri.StartsWith("file:///", StringComparison.OrdinalIgnoreCase));

    private static bool IsGitRepository(string repoRoot)
    {
        var gitMetadataPath = Path.Combine(repoRoot, ".git");
        return Directory.Exists(gitMetadataPath) || File.Exists(gitMetadataPath);
    }

    private IReadOnlyList<HistoryCommit> LoadHistory(IReadOnlyList<string> fileUris, CancellationToken ct)
    {
        if (fileUris.Count == 0)
            return Array.Empty<HistoryCommit>();

        var escapedUris = fileUris
            .Select(uri => $"'{EscapeSqlLiteral(uri)}'")
            .ToArray();

        var inClause = string.Join(", ", escapedUris);

        var sql = $"""
            SELECT c.hash,
                   c.author_name,
                   c.author_email,
                   c.author_date,
                   c.message,
                   fc.uri,
                   fc.old_uri,
                   fc.change_type,
                   fc.insertions,
                   fc.deletions
            FROM git_file_change fc
            JOIN git_commit c ON fc.commit_hash = c.hash
            WHERE fc.uri IN ({inClause})
               OR fc.old_uri IN ({inClause})
            ORDER BY c.author_date DESC, c.hash
            """;

        var rows = _db.Query(sql, ct);
        var commits = new Dictionary<string, HistoryCommit>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();

            var hash = row.TryGetValue("hash", out var hashValue) ? hashValue?.ToString() : null;
            if (string.IsNullOrWhiteSpace(hash))
                continue;

            if (!commits.TryGetValue(hash, out var commit))
            {
                var author = row.TryGetValue("author_name", out var authorValue) ? authorValue?.ToString() : null;
                var dateValue = row.TryGetValue("author_date", out var dateRaw) ? dateRaw : null;
                var message = row.TryGetValue("message", out var messageValue) ? messageValue?.ToString() : null;
                commit = new HistoryCommit(
                    hash,
                    string.IsNullOrWhiteSpace(author) ? "Unknown" : author!,
                    ParseDate(dateValue),
                    message ?? string.Empty);
                commits[hash] = commit;
            }

            var uri = row.TryGetValue("uri", out var uriValue) ? uriValue?.ToString() : null;
            if (string.IsNullOrWhiteSpace(uri))
                continue;

            var oldUri = row.TryGetValue("old_uri", out var oldValue) ? oldValue?.ToString() : null;
            var changeType = row.TryGetValue("change_type", out var changeValue) ? changeValue?.ToString() : null;
            var insertions = row.TryGetValue("insertions", out var insertValue) ? Convert.ToInt32(insertValue, CultureInfo.InvariantCulture) : 0;
            var deletions = row.TryGetValue("deletions", out var deleteValue) ? Convert.ToInt32(deleteValue, CultureInfo.InvariantCulture) : 0;

            commit.AddChange(new HistoryChange(
                uri!,
                oldUri,
                string.IsNullOrWhiteSpace(changeType) ? "M" : changeType!,
                insertions,
                deletions));
        }

        return commits.Values.ToList();
    }

    private static string? FitCommitBlock(HistoryCommit commit, int tokenBudget)
    {
        // Try with detailed file changes
        var block = BuildCommitBlock(commit, includeMessage: true, includeFileDetails: true, includeFileSummary: false);
        if (TokenEstimator.EstimateTokens(block) <= tokenBudget)
            return block;

        // Fall back to summary line only
        block = BuildCommitBlock(commit, includeMessage: true, includeFileDetails: false, includeFileSummary: true);
        if (TokenEstimator.EstimateTokens(block) <= tokenBudget)
            return block;

        // No file info
        block = BuildCommitBlock(commit, includeMessage: true, includeFileDetails: false, includeFileSummary: false);
        if (TokenEstimator.EstimateTokens(block) <= tokenBudget)
            return block;

        // Minimal: just hash, date, author
        block = BuildCommitBlock(commit, includeMessage: false, includeFileDetails: false, includeFileSummary: false);
        return TokenEstimator.EstimateTokens(block) <= tokenBudget ? block : null;
    }

    private static string BuildContent(IReadOnlyList<string> blocks, int totalAvailable, bool usedRelevance)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < blocks.Count; i++)
        {
            if (i > 0)
                builder.Append('\n');
            builder.Append(blocks[i]);
        }

        builder.Append("\n\n");
        builder.Append(BuildSummaryLine(blocks.Count, totalAvailable, usedRelevance));
        return builder.ToString();
    }

    private static string BuildSummaryLine(int shown, int totalAvailable, bool usedRelevance)
    {
        var remainder = Math.Max(0, totalAvailable - shown);
        var shownLabel = shown == 1 ? "commit" : "commits";
        var qualifier = usedRelevance ? " (by relevance)" : string.Empty;
        return $"[{shown} {shownLabel} shown{qualifier}, {remainder} more in history]";
    }

    private static string BuildCommitBlock(
        HistoryCommit commit,
        bool includeMessage,
        bool includeFileDetails,
        bool includeFileSummary)
    {
        var shortHash = commit.Hash.Length > 7 ? commit.Hash[..7] : commit.Hash;
        var date = commit.AuthorDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        var builder = new StringBuilder();
        builder.Append(shortHash);
        builder.Append(' ');
        builder.Append(date);
        builder.Append(' ');
        builder.Append(commit.Author);

        if (includeMessage)
        {
            var message = NormalizeLineEndings(commit.Message).TrimEnd();
            if (!string.IsNullOrWhiteSpace(message))
            {
                var firstLine = message.Split('\n', StringSplitOptions.None)[0].TrimEnd();
                if (!string.IsNullOrWhiteSpace(firstLine))
                {
                    builder.Append(" | ");
                    builder.Append(firstLine);
                }
            }
        }

        if (includeFileDetails && commit.Changes.Count > 0)
        {
            var (folderRenames, otherChanges) = DetectFolderRenames(commit.Changes);

            // Group by operation type
            var adds = otherChanges.Where(c => c.ChangeType.Equals("A", StringComparison.OrdinalIgnoreCase)).ToList();
            var deletes = otherChanges.Where(c => c.ChangeType.Equals("D", StringComparison.OrdinalIgnoreCase)).ToList();
            var renames = otherChanges.Where(c => c.ChangeType.Equals("R", StringComparison.OrdinalIgnoreCase)).ToList();
            var modified = otherChanges.Where(c => c.ChangeType.Equals("M", StringComparison.OrdinalIgnoreCase) ||
                                                    (!c.ChangeType.Equals("A", StringComparison.OrdinalIgnoreCase) &&
                                                     !c.ChangeType.Equals("D", StringComparison.OrdinalIgnoreCase) &&
                                                     !c.ChangeType.Equals("R", StringComparison.OrdinalIgnoreCase))).ToList();

            // Folder renames first
            foreach (var rename in folderRenames)
            {
                builder.Append('\n');
                builder.Append("    ");
                builder.Append(rename.OldFolder);
                builder.Append("/ → ");
                builder.Append(rename.NewFolder);
                builder.Append("/ (");
                builder.Append(rename.FileCount);
                builder.Append(rename.FileCount == 1 ? " file)" : " files)");
            }

            // Adds grouped
            if (adds.Count > 0)
            {
                builder.Append('\n');
                builder.Append("    ");
                builder.Append(string.Join(", ", adds.Select(c => ExtractFileName(c.Uri))));
                builder.Append(" added");
            }

            // Deletes grouped
            if (deletes.Count > 0)
            {
                builder.Append('\n');
                builder.Append("    ");
                builder.Append(string.Join(", ", deletes.Select(c => ExtractFileName(c.Uri))));
                builder.Append(" deleted");
            }

            // Renames individually
            foreach (var change in renames)
            {
                builder.Append('\n');
                builder.Append("    ");
                builder.Append(ExtractFileName(change.Uri));
                builder.Append(' ');
                builder.Append(FormatChangeType(change));
            }

            // Modified individually
            foreach (var change in modified)
            {
                builder.Append('\n');
                builder.Append("    ");
                builder.Append(ExtractFileName(change.Uri));
                builder.Append(' ');
                builder.Append(FormatChangeType(change));
            }
        }
        else if (includeFileSummary)
        {
            builder.Append(" | ");
            builder.Append(BuildDiffSummary(commit));
        }

        return builder.ToString();
    }

    private static (IReadOnlyList<FolderRename> FolderRenames, IReadOnlyList<HistoryChange> OtherChanges) DetectFolderRenames(
        IReadOnlyList<HistoryChange> changes)
    {
        // Group renames by (oldFolder, newFolder) where filename stayed the same
        var moveGroups = new Dictionary<(string OldFolder, string NewFolder), List<HistoryChange>>(
            new FolderPairComparer());

        var otherChanges = new List<HistoryChange>();

        foreach (var change in changes)
        {
            if (!change.ChangeType.Equals("R", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(change.OldUri))
            {
                otherChanges.Add(change);
                continue;
            }

            var newName = ExtractFileName(change.Uri);
            var oldName = ExtractFileName(change.OldUri);

            // Only consider moves where the filename stayed the same
            if (!string.Equals(newName, oldName, StringComparison.OrdinalIgnoreCase))
            {
                otherChanges.Add(change);
                continue;
            }

            var newFolder = ExtractFolderPath(change.Uri);
            var oldFolder = ExtractFolderPath(change.OldUri);

            if (string.Equals(newFolder, oldFolder, StringComparison.OrdinalIgnoreCase))
            {
                otherChanges.Add(change);
                continue;
            }

            var key = (oldFolder, newFolder);
            if (!moveGroups.TryGetValue(key, out var group))
            {
                group = new List<HistoryChange>();
                moveGroups[key] = group;
            }

            group.Add(change);
        }

        // Only treat as folder rename if there are multiple files moved together
        var folderRenames = new List<FolderRename>();
        foreach (var (key, group) in moveGroups)
        {
            if (group.Count >= 2)
            {
                folderRenames.Add(new FolderRename(key.OldFolder, key.NewFolder, group.Count));
            }
            else
            {
                otherChanges.AddRange(group);
            }
        }

        // Collapse child renames into parent renames
        var collapsed = CollapseChildRenames(folderRenames);

        return (collapsed, otherChanges);
    }

    private static List<FolderRename> CollapseChildRenames(List<FolderRename> renames)
    {
        if (renames.Count <= 1)
            return renames;

        // Sort by path length (shortest first = most likely parent)
        var sorted = renames.OrderBy(r => r.OldFolder.Length).ToList();
        var result = new List<FolderRename>();
        var consumed = new HashSet<int>();

        for (var i = 0; i < sorted.Count; i++)
        {
            if (consumed.Contains(i))
                continue;

            var parent = sorted[i];
            var totalFiles = parent.FileCount;

            // Check if any other renames are children of this one
            for (var j = i + 1; j < sorted.Count; j++)
            {
                if (consumed.Contains(j))
                    continue;

                var child = sorted[j];

                // Check if child is a subfolder of parent (same rename pattern)
                if (child.OldFolder.StartsWith(parent.OldFolder + "/", StringComparison.OrdinalIgnoreCase) &&
                    child.NewFolder.StartsWith(parent.NewFolder + "/", StringComparison.OrdinalIgnoreCase))
                {
                    // Verify the subfolder names match
                    var oldSuffix = child.OldFolder[(parent.OldFolder.Length + 1)..];
                    var newSuffix = child.NewFolder[(parent.NewFolder.Length + 1)..];
                    if (string.Equals(oldSuffix, newSuffix, StringComparison.OrdinalIgnoreCase))
                    {
                        totalFiles += child.FileCount;
                        consumed.Add(j);
                    }
                }
            }

            result.Add(new FolderRename(parent.OldFolder, parent.NewFolder, totalFiles));
        }

        return result;
    }

    private sealed record FolderRename(string OldFolder, string NewFolder, int FileCount);

    private sealed class FolderPairComparer : IEqualityComparer<(string OldFolder, string NewFolder)>
    {
        public bool Equals((string OldFolder, string NewFolder) x, (string OldFolder, string NewFolder) y)
            => string.Equals(x.OldFolder, y.OldFolder, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(x.NewFolder, y.NewFolder, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string OldFolder, string NewFolder) obj)
            => StringComparer.OrdinalIgnoreCase.GetHashCode(obj.OldFolder) ^
               StringComparer.OrdinalIgnoreCase.GetHashCode(obj.NewFolder);
    }

    private static string ExtractFolderPath(string uri)
    {
        if (string.IsNullOrWhiteSpace(uri))
            return string.Empty;

        var trimmed = uri;
        var hashIndex = trimmed.IndexOf('#', StringComparison.Ordinal);
        if (hashIndex >= 0)
            trimmed = trimmed[..hashIndex];

        // Remove scheme (file:///)
        var schemeEnd = trimmed.IndexOf("///", StringComparison.Ordinal);
        if (schemeEnd >= 0)
            trimmed = trimmed[(schemeEnd + 3)..];

        var lastSlash = trimmed.LastIndexOf('/');
        return lastSlash < 0 ? string.Empty : trimmed[..lastSlash];
    }

    private static string FormatChangeType(HistoryChange change)
    {
        if (change.ChangeType.Equals("A", StringComparison.OrdinalIgnoreCase))
            return "added";

        if (change.ChangeType.Equals("D", StringComparison.OrdinalIgnoreCase))
            return "deleted";

        if (change.ChangeType.Equals("R", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(change.OldUri))
        {
            var newName = ExtractFileName(change.Uri);
            var oldName = ExtractFileName(change.OldUri);
            var newFolder = ExtractFolder(change.Uri);
            var oldFolder = ExtractFolder(change.OldUri);

            var nameChanged = !string.Equals(newName, oldName, StringComparison.OrdinalIgnoreCase);
            var folderChanged = !string.Equals(newFolder, oldFolder, StringComparison.OrdinalIgnoreCase);

            if (nameChanged && folderChanged)
                return $"renamed from {oldName} (moved from {oldFolder})";
            if (nameChanged)
                return $"renamed from {oldName}";
            if (folderChanged)
                return $"moved from {oldFolder}";

            return "renamed";
        }

        return $"+{change.Insertions} -{change.Deletions}";
    }

    private static string ExtractFolder(string uri)
    {
        if (string.IsNullOrWhiteSpace(uri))
            return string.Empty;

        var trimmed = uri;
        var hashIndex = trimmed.IndexOf('#', StringComparison.Ordinal);
        if (hashIndex >= 0)
            trimmed = trimmed[..hashIndex];

        var lastSlash = trimmed.LastIndexOf('/');
        if (lastSlash < 0)
            return string.Empty;

        // Find the folder name (second-to-last segment)
        var folderEnd = lastSlash;
        var folderStart = trimmed.LastIndexOf('/', folderEnd - 1);
        if (folderStart < 0)
            return trimmed[..folderEnd];

        return trimmed[(folderStart + 1)..folderEnd];
    }

    private static string BuildDiffSummary(HistoryCommit commit)
    {
        var fileCount = commit.Changes.Count;
        var fileLabel = fileCount == 1 ? "file" : "files";
        return $"+{commit.Insertions} -{commit.Deletions}, {fileCount} {fileLabel}";
    }

    private static string NormalizeLineEndings(string text)
        => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    private static List<string> NormalizeKeywords(string keywords)
    {
        var terms = new List<string>();
        foreach (var raw in keywords.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var term = raw.Trim().Trim('"', '\'', ',', '.', ':', ';', '!', '?', '(', ')', '[', ']', '{', '}', '<', '>', '`');
            if (string.IsNullOrWhiteSpace(term))
                continue;
            terms.Add(term.ToLowerInvariant());
        }

        return terms.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static int ScoreCommit(HistoryCommit commit, string phrase, IReadOnlyList<string> terms)
    {
        var haystack = BuildSearchText(commit);
        var score = 0;

        if (!string.IsNullOrWhiteSpace(phrase))
        {
            var phraseLower = phrase.Trim().ToLowerInvariant();
            if (haystack.Contains(phraseLower, StringComparison.Ordinal))
                score += 2;
        }

        foreach (var term in terms)
        {
            if (haystack.Contains(term, StringComparison.Ordinal))
                score += 1;
        }

        return score;
    }

    private static string BuildSearchText(HistoryCommit commit)
    {
        var builder = new StringBuilder(commit.Message.Length + commit.Author.Length + commit.Changes.Count * 16);
        builder.Append(commit.Message);
        builder.Append(' ');
        builder.Append(commit.Author);

        foreach (var change in commit.Changes)
        {
            var fileName = ExtractFileName(change.Uri);
            if (!string.IsNullOrWhiteSpace(fileName))
            {
                builder.Append(' ');
                builder.Append(fileName);
            }

            if (!string.IsNullOrWhiteSpace(change.OldUri))
            {
                var oldFileName = ExtractFileName(change.OldUri);
                if (!string.IsNullOrWhiteSpace(oldFileName))
                {
                    builder.Append(' ');
                    builder.Append(oldFileName);
                }
            }
        }

        return builder.ToString().ToLowerInvariant();
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

    /// <summary>
    /// Purpose: Captures commit-level data for history rendering.
    /// Complexity: Centralizes change aggregation to keep formatting logic simple.
    /// </summary>
    private sealed class HistoryCommit
    {
        public HistoryCommit(string hash, string author, DateTimeOffset authorDate, string message)
        {
            Hash = hash;
            Author = author;
            AuthorDate = authorDate;
            Message = message;
            Changes = new List<HistoryChange>();
        }

        public string Hash { get; }
        public string Author { get; }
        public DateTimeOffset AuthorDate { get; }
        public string Message { get; }
        public List<HistoryChange> Changes { get; }
        public int Insertions { get; private set; }
        public int Deletions { get; private set; }

        public void AddChange(HistoryChange change)
        {
            Changes.Add(change);
            Insertions += change.Insertions;
            Deletions += change.Deletions;
        }
    }

    /// <summary>
    /// Purpose: Records per-file change details for history output.
    /// Complexity: Keeps per-file change data structured for aggregation and scoring.
    /// </summary>
    private sealed record HistoryChange(
        string Uri,
        string? OldUri,
        string ChangeType,
        int Insertions,
        int Deletions);

    /// <summary>
    /// Purpose: Associates relevance scores with commits for keyword ordering.
    /// Complexity: Keeps scoring data together to simplify ordering logic.
    /// </summary>
    private sealed record CommitScore(HistoryCommit Commit, int Score);
}
