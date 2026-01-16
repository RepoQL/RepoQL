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
        => IndexCoreAsync(repoPath, fullReindex: true, cancellationToken);

    /// <summary>
    /// Incrementally indexes new commits since the last indexed commit.
    /// If no commits are indexed yet, performs a full index.
    /// </summary>
    public Task IndexIncrementalAsync(string repoPath, CancellationToken cancellationToken = default)
        => IndexCoreAsync(repoPath, fullReindex: false, cancellationToken);

    /// <summary>
    /// Gets the hash of the most recently indexed commit, or null if none indexed.
    /// </summary>
    public string? GetLatestIndexedCommitHash()
    {
        try
        {
            return _db.ReadScalar<string?>(
                "SELECT hash FROM git_commit ORDER BY committer_date DESC LIMIT 1");
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Gets the count of indexed commits.
    /// </summary>
    public int GetIndexedCommitCount()
    {
        try
        {
            return _db.ReadScalar<int>("SELECT COUNT(*) FROM git_commit");
        }
        catch
        {
            return 0;
        }
    }

    private async Task IndexCoreAsync(string repoPath, bool fullReindex, CancellationToken cancellationToken)
    {
        if (!Repository.IsValid(repoPath))
        {
            _logger.LogDebug("Path is not a valid git repository: {Path}", repoPath);
            return;
        }

        // For incremental, check if we have any commits indexed
        string? lastIndexedHash = null;
        if (!fullReindex)
        {
            lastIndexedHash = GetLatestIndexedCommitHash();
            if (lastIndexedHash is null)
            {
                _logger.LogInformation("No commits indexed yet, performing full git history index");
                fullReindex = true;
            }
        }

        var mode = fullReindex ? "full" : "incremental";
        _logger.LogInformation("Indexing git history ({Mode}) from {Path}...", mode, repoPath);
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            if (fullReindex)
            {
                ClearGitTables();
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
                    FlushBatch(commitBatch, fileChangeBatch);
                    commitBatch.Clear();
                    fileChangeBatch.Clear();
                }
            }

            // Flush remaining
            if (commitBatch.Count > 0)
            {
                FlushBatch(commitBatch, fileChangeBatch);
            }

            sw.Stop();
            if (commitCount > 0)
            {
                // Checkpoint to ensure data is persisted to disk
                _db.TryCheckpoint();

                _logger.LogInformation(
                    "Git history indexed ({Mode}): {CommitCount} commits, {FileChangeCount} file changes in {ElapsedMs}ms",
                    mode, commitCount, fileChangeCount, sw.ElapsedMilliseconds);
            }
            else
            {
                _logger.LogDebug("Git history up to date, no new commits");
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

    private void ClearGitTables()
    {
        _db.ExecuteRaw("DELETE FROM git_file_change; DELETE FROM git_commit;");
    }

    private void FlushBatch(List<CommitRecord> commits, List<FileChangeRecord> fileChanges)
    {
        if (commits.Count == 0)
            return;

        _db.WriteTransaction((conn, tx) =>
        {
            BulkInsertCommits(conn, tx, commits);

            if (fileChanges.Count > 0)
                BulkInsertFileChanges(conn, tx, fileChanges);
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

    private static void BulkInsertFileChanges(DuckDBConnection conn, DuckDBTransaction tx, IReadOnlyList<FileChangeRecord> changes)
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
                var uri = PathToUri(fc.FilePath);
                var oldUri = fc.OldPath is not null ? PathToUri(fc.OldPath) : null;

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

    private static string PathToUri(string relativePath)
    {
        // Normalize to forward slashes for URI
        var normalized = relativePath.Replace('\\', '/');
        return $"file:///{normalized}";
    }

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
