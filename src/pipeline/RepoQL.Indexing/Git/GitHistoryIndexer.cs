using System.Text;
using DuckDB.NET.Data;
using LibGit2Sharp;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RepoQL.Data.DuckDB;

namespace RepoQL.Indexing.Git;

/// <summary>
/// Indexes git commit history into DuckDB for SQL-based code archaeology.
///
/// Purpose: Enables querying git history via SQL - hotspot analysis, file history,
/// author contributions, and correlation with code quality metrics.
///
/// Complexity: Uses LibGit2Sharp to read git history without CLI dependency.
/// Batches inserts to minimize database transactions. Supports both full reindex
/// (clears and repopulates 12 months) and incremental mode (only new commits).
/// </summary>
public sealed class GitHistoryIndexer
{
    private const int BatchSize = 100;
    private const int HistoryMonths = 12;
    private const string DefaultSourceUri = "file://";

    private readonly DuckDbDataStore _db;
    private readonly ILogger<GitHistoryIndexer> _logger;

    public GitHistoryIndexer(DuckDbDataStore db, ILogger<GitHistoryIndexer>? logger = null)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _logger = logger ?? NullLogger<GitHistoryIndexer>.Instance;
    }

    /// <summary>
    /// Indexes git history from the specified repository path.
    /// Clears existing git data and repopulates from the last 12 months.
    /// </summary>
    public Task IndexAsync(string repoPath, CancellationToken cancellationToken = default)
        => IndexCoreAsync(repoPath, DefaultSourceUri, fullReindex: true, cancellationToken);

    /// <summary>
    /// Indexes git history for a specific repository source URI (for example:
    /// <c>file://</c>, <c>github://owner/repo</c>, or <c>local:///path</c>).
    /// Clears existing indexed history for this source and repopulates from the last 12 months.
    /// </summary>
    public Task IndexAsync(string repoPath, string sourceUri, CancellationToken cancellationToken = default)
        => IndexCoreAsync(repoPath, sourceUri, fullReindex: true, cancellationToken);

    /// <summary>
    /// Incrementally indexes new commits since the last indexed commit.
    /// If no commits are indexed yet, performs a full index.
    /// </summary>
    public Task IndexIncrementalAsync(string repoPath, CancellationToken cancellationToken = default)
        => IndexCoreAsync(repoPath, DefaultSourceUri, fullReindex: false, cancellationToken);

    /// <summary>
    /// Incrementally indexes new commits for the specified source URI.
    /// If no commits are indexed yet for this source, performs a full index.
    /// </summary>
    public Task IndexIncrementalAsync(string repoPath, string sourceUri, CancellationToken cancellationToken = default)
        => IndexCoreAsync(repoPath, sourceUri, fullReindex: false, cancellationToken);

    /// <summary>
    /// Gets the hash of the most recently indexed commit, or null if none indexed.
    /// </summary>
    public string? GetLatestIndexedCommitHash(string sourceUri = DefaultSourceUri)
    {
        try
        {
            var prefix = BuildSourceUriPrefix(sourceUri);
            return _db.ReadScalar<string?>(
                $"""
                SELECT c.hash
                FROM git_commit c
                JOIN git_file_change fc ON fc.commit_hash = c.hash
                WHERE starts_with(fc.uri, '{EscapeSqlLiteral(prefix)}')
                   OR (fc.old_uri IS NOT NULL AND starts_with(fc.old_uri, '{EscapeSqlLiteral(prefix)}'))
                ORDER BY c.committer_date DESC
                LIMIT 1
                """);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Gets the count of indexed commits.
    /// </summary>
    public int GetIndexedCommitCount(string sourceUri = DefaultSourceUri)
    {
        try
        {
            var prefix = BuildSourceUriPrefix(sourceUri);
            return _db.ReadScalar<int>(
                $"""
                SELECT COUNT(DISTINCT c.hash)
                FROM git_commit c
                JOIN git_file_change fc ON fc.commit_hash = c.hash
                WHERE starts_with(fc.uri, '{EscapeSqlLiteral(prefix)}')
                   OR (fc.old_uri IS NOT NULL AND starts_with(fc.old_uri, '{EscapeSqlLiteral(prefix)}'))
                """);
        }
        catch
        {
            return 0;
        }
    }

    private async Task IndexCoreAsync(string repoPath, string sourceUri, bool fullReindex, CancellationToken cancellationToken)
    {
        if (!Repository.IsValid(repoPath))
        {
            _logger.LogDebug("Path is not a valid git repository: {Path}", repoPath);
            return;
        }

        var normalizedSourceUri = NormalizeSourceUri(sourceUri);

        // For incremental, check if we have any commits indexed
        string? lastIndexedHash = null;
        if (!fullReindex)
        {
            lastIndexedHash = GetLatestIndexedCommitHash(normalizedSourceUri);
            if (lastIndexedHash is null)
            {
                _logger.LogInformation(
                    "No commits indexed yet for {SourceUri}, performing full git history index",
                    normalizedSourceUri);
                fullReindex = true;
            }
        }

        var mode = fullReindex ? "full" : "incremental";
        _logger.LogInformation(
            "Indexing git history ({Mode}) from {Path} [source={SourceUri}]...",
            mode,
            repoPath,
            normalizedSourceUri);
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            if (fullReindex)
            {
                ClearGitTables(normalizedSourceUri);
            }

            var commitCount = 0;
            var fileChangeCount = 0;
            var since = DateTimeOffset.UtcNow.AddMonths(-HistoryMonths);

            using var repo = new Repository(repoPath);

            var commitBatch = new List<CommitRecord>(BatchSize);
            var fileChangeBatch = new List<FileChangeRecord>(BatchSize * 10);

            foreach (var commit in repo.Commits.QueryBy(new CommitFilter
            {
                SortBy = CommitSortStrategies.Time
            }))
            {
                cancellationToken.ThrowIfCancellationRequested();

                // For incremental: stop when we hit a commit we already have
                if (!fullReindex && commit.Sha == lastIndexedHash)
                {
                    _logger.LogDebug("Reached already-indexed commit {Hash}", lastIndexedHash[..8]);
                    break;
                }

                // Stop when we reach commits older than our cutoff (full reindex only)
                if (fullReindex && commit.Author.When < since)
                    break;

                // Extract commit metadata
                var parentHashes = commit.Parents.Select(p => p.Sha).ToArray();
                var commitRecord = new CommitRecord
                {
                    Hash = commit.Sha,
                    AuthorName = commit.Author.Name,
                    AuthorEmail = commit.Author.Email,
                    AuthorDate = commit.Author.When,
                    CommitterName = commit.Committer.Name,
                    CommitterEmail = commit.Committer.Email,
                    CommitterDate = commit.Committer.When,
                    Message = commit.Message,
                    ParentHashes = parentHashes
                };

                // Get file changes by comparing with parent
                var parent = commit.Parents.FirstOrDefault();
                var changes = GetFileChanges(repo, commit, parent);

                commitRecord.FilesChanged = changes.Count;
                commitRecord.Insertions = changes.Sum(c => c.Insertions);
                commitRecord.Deletions = changes.Sum(c => c.Deletions);

                commitBatch.Add(commitRecord);
                fileChangeBatch.AddRange(changes);
                commitCount++;
                fileChangeCount += changes.Count;

                // Flush batch when full
                if (commitBatch.Count >= BatchSize)
                {
                    FlushBatch(commitBatch, fileChangeBatch, normalizedSourceUri);
                    commitBatch.Clear();
                    fileChangeBatch.Clear();
                }
            }

            // Flush remaining
            if (commitBatch.Count > 0)
            {
                FlushBatch(commitBatch, fileChangeBatch, normalizedSourceUri);
            }

            sw.Stop();
            if (commitCount > 0)
            {
                // Checkpoint to ensure data is persisted to disk
                _db.TryCheckpoint();

                _logger.LogInformation(
                    "Git history indexed ({Mode}) [source={SourceUri}]: {CommitCount} commits, {FileChangeCount} file changes in {ElapsedMs}ms",
                    mode, normalizedSourceUri, commitCount, fileChangeCount, sw.ElapsedMilliseconds);
            }
            else
            {
                _logger.LogDebug("Git history up to date, no new commits [source={SourceUri}]", normalizedSourceUri);
            }
        }
        catch (RepositoryNotFoundException ex)
        {
            _logger.LogWarning(ex, "Git repository not found at {Path}", repoPath);
        }
        catch (LibGit2SharpException ex)
        {
            _logger.LogWarning(ex, "Error reading git repository at {Path}", repoPath);
        }
    }

    private List<FileChangeRecord> GetFileChanges(Repository repo, Commit commit, Commit? parent)
    {
        var changes = new List<FileChangeRecord>();

        try
        {
            var treeChanges = repo.Diff.Compare<TreeChanges>(
                parent?.Tree,
                commit.Tree);

            // Get patch once for line stats and binary detection
            Patch? patch = null;
            try
            {
                patch = repo.Diff.Compare<Patch>(parent?.Tree, commit.Tree);
            }
            catch
            {
                // Patch unavailable, line stats will be 0
            }

            foreach (var change in treeChanges)
            {
                var patchEntry = patch?.FirstOrDefault(p => p.Path == change.Path);
                var insertions = patchEntry?.LinesAdded ?? 0;
                var deletions = patchEntry?.LinesDeleted ?? 0;

                // Detect binary: file changed but no line changes reported
                var isBinary = change.Status != ChangeKind.Unmodified &&
                               insertions == 0 && deletions == 0 &&
                               patchEntry != null;

                var record = new FileChangeRecord
                {
                    CommitHash = commit.Sha,
                    FilePath = change.Path,
                    ChangeType = MapChangeType(change.Status),
                    OldPath = change.OldPath != change.Path ? change.OldPath : null,
                    IsBinary = isBinary,
                    Insertions = insertions,
                    Deletions = deletions
                };

                changes.Add(record);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error getting file changes for commit {Hash}", commit.Sha[..8]);
        }

        return changes;
    }

    private static string MapChangeType(ChangeKind status) => status switch
    {
        ChangeKind.Added => "A",
        ChangeKind.Deleted => "D",
        ChangeKind.Modified => "M",
        ChangeKind.Renamed => "R",
        ChangeKind.Copied => "C",
        ChangeKind.TypeChanged => "T",
        _ => "M"
    };

    private void ClearGitTables(string sourceUri)
    {
        var prefix = EscapeSqlLiteral(BuildSourceUriPrefix(sourceUri));
        _db.ExecuteRaw(
            $"""
            DELETE FROM git_file_change
            WHERE starts_with(uri, '{prefix}')
               OR (old_uri IS NOT NULL AND starts_with(old_uri, '{prefix}'));

            DELETE FROM git_commit
            WHERE hash NOT IN (SELECT DISTINCT commit_hash FROM git_file_change);
            """);
    }

    private void FlushBatch(List<CommitRecord> commits, List<FileChangeRecord> fileChanges, string sourceUri)
    {
        if (commits.Count == 0)
            return;

        _db.WriteTransaction((conn, tx) =>
        {
            BulkInsertCommits(conn, tx, commits);

            if (fileChanges.Count > 0)
                BulkInsertFileChanges(conn, tx, fileChanges, sourceUri);
        });
    }

    private static void BulkInsertCommits(DuckDBConnection conn, DuckDBTransaction tx, IReadOnlyList<CommitRecord> commits)
    {
        const int batchSize = 50; // 12 columns, moderate batch size
        for (var offset = 0; offset < commits.Count; offset += batchSize)
        {
            var batch = commits.Skip(offset).Take(batchSize).ToList();
            var sb = new StringBuilder();
            sb.AppendLine("INSERT INTO git_commit (hash, author_name, author_email, author_date, committer_name, committer_email, committer_date, message, parent_hashes, files_changed, insertions, deletions) VALUES");

            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;

            for (var i = 0; i < batch.Count; i++)
            {
                if (i > 0) sb.Append(',');
                var p = i * 12;
                sb.Append($"(${p + 1},${p + 2},${p + 3},${p + 4},${p + 5},${p + 6},${p + 7},${p + 8},${p + 9},${p + 10},${p + 11},${p + 12})");

                var c = batch[i];
                cmd.Parameters.Add(new DuckDBParameter { Value = c.Hash });
                cmd.Parameters.Add(new DuckDBParameter { Value = c.AuthorName });
                cmd.Parameters.Add(new DuckDBParameter { Value = c.AuthorEmail });
                cmd.Parameters.Add(new DuckDBParameter { Value = c.AuthorDate.UtcDateTime });
                cmd.Parameters.Add(new DuckDBParameter { Value = c.CommitterName });
                cmd.Parameters.Add(new DuckDBParameter { Value = c.CommitterEmail });
                cmd.Parameters.Add(new DuckDBParameter { Value = c.CommitterDate.UtcDateTime });
                cmd.Parameters.Add(new DuckDBParameter { Value = c.Message });
                cmd.Parameters.Add(new DuckDBParameter { Value = c.ParentHashes });
                cmd.Parameters.Add(new DuckDBParameter { Value = c.FilesChanged });
                cmd.Parameters.Add(new DuckDBParameter { Value = c.Insertions });
                cmd.Parameters.Add(new DuckDBParameter { Value = c.Deletions });
            }

            sb.AppendLine(" ON CONFLICT (hash) DO NOTHING;");
            cmd.CommandText = sb.ToString();
            cmd.ExecuteNonQuery();
        }
    }

    private static void BulkInsertFileChanges(
        DuckDBConnection conn,
        DuckDBTransaction tx,
        IReadOnlyList<FileChangeRecord> changes,
        string sourceUri)
    {
        const int batchSize = 100; // 7 columns, larger batch ok
        for (var offset = 0; offset < changes.Count; offset += batchSize)
        {
            var batch = changes.Skip(offset).Take(batchSize).ToList();
            var sb = new StringBuilder();
            sb.AppendLine("INSERT INTO git_file_change (commit_hash, uri, change_type, old_uri, insertions, deletions, is_binary) VALUES");

            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;

            for (var i = 0; i < batch.Count; i++)
            {
                if (i > 0) sb.Append(',');
                var p = i * 7;
                sb.Append($"(${p + 1},${p + 2},${p + 3},${p + 4},${p + 5},${p + 6},${p + 7})");

                var fc = batch[i];
                var uri = PathToUri(fc.FilePath, sourceUri);
                var oldUri = fc.OldPath is not null ? PathToUri(fc.OldPath, sourceUri) : null;

                cmd.Parameters.Add(new DuckDBParameter { Value = fc.CommitHash });
                cmd.Parameters.Add(new DuckDBParameter { Value = uri });
                cmd.Parameters.Add(new DuckDBParameter { Value = fc.ChangeType });
                cmd.Parameters.Add(new DuckDBParameter { Value = oldUri ?? (object)DBNull.Value });
                cmd.Parameters.Add(new DuckDBParameter { Value = fc.Insertions });
                cmd.Parameters.Add(new DuckDBParameter { Value = fc.Deletions });
                cmd.Parameters.Add(new DuckDBParameter { Value = fc.IsBinary });
            }

            cmd.CommandText = sb.ToString();
            cmd.ExecuteNonQuery();
        }
    }

    private static string PathToUri(string relativePath, string sourceUri)
    {
        var normalizedPath = relativePath.Replace('\\', '/').TrimStart('/');
        var normalizedSource = NormalizeSourceUri(sourceUri);
        return $"{normalizedSource}/{normalizedPath}";
    }

    private static string NormalizeSourceUri(string sourceUri)
    {
        if (string.IsNullOrWhiteSpace(sourceUri))
            return DefaultSourceUri;

        var normalized = sourceUri.Trim();
        if (normalized.Equals("file:///", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("file://", StringComparison.OrdinalIgnoreCase))
            return DefaultSourceUri;

        if (normalized.EndsWith("://", StringComparison.Ordinal))
            return normalized;

        return normalized.TrimEnd('/');
    }

    private static string BuildSourceUriPrefix(string sourceUri)
        => $"{NormalizeSourceUri(sourceUri)}/";

    private static string EscapeSqlLiteral(string value)
        => value.Replace("'", "''", StringComparison.Ordinal);

    private sealed record CommitRecord
    {
        public required string Hash { get; init; }
        public required string AuthorName { get; init; }
        public required string AuthorEmail { get; init; }
        public required DateTimeOffset AuthorDate { get; init; }
        public required string CommitterName { get; init; }
        public required string CommitterEmail { get; init; }
        public required DateTimeOffset CommitterDate { get; init; }
        public required string Message { get; init; }
        public required string[] ParentHashes { get; init; }
        public int FilesChanged { get; set; }
        public int Insertions { get; set; }
        public int Deletions { get; set; }
    }

    private sealed record FileChangeRecord
    {
        public required string CommitHash { get; init; }
        public required string FilePath { get; init; }
        public required string ChangeType { get; init; }
        public string? OldPath { get; init; }
        public int Insertions { get; set; }
        public int Deletions { get; set; }
        public bool IsBinary { get; init; }
    }
}
