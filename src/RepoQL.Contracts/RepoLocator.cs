namespace RepoQL.Contracts;

/// <summary>
///     Utilities to discover the repository root on disk.
///     Searches upward from a start path for markers like ".git" or ".repoql".
///     Falls back to the provided start path or current directory if nothing found.
/// </summary>
public static class RepoLocator
{
    /// <summary>
    ///     Find the repository root starting at <paramref name="startPath" />.
    ///     If <paramref name="startPath" /> is null or empty the current working directory is used.
    ///     The method looks for ".git" (directory or file) or ".repoql" as markers.
    ///     If no marker is found the topmost directory is returned.
    /// </summary>
    public static string FindRepoRoot(string? startPath = null)
    {
        var dir = new DirectoryInfo(Path.GetFullPath(string.IsNullOrWhiteSpace(startPath)
            ? Directory.GetCurrentDirectory()
            : startPath));
        var last = dir;
        while (dir != null)
        {
            // marker: .git folder or file (worktrees) or .repoql folder
            if (Directory.Exists(Path.Combine(dir.FullName, ".git")) ||
                File.Exists(Path.Combine(dir.FullName, ".git")) ||
                Directory.Exists(Path.Combine(dir.FullName, ".repoql")))
            {
                return dir.FullName;
            }

            last = dir;
            dir = dir.Parent;
        }

        // no marker; return the topmost directory we reached (usually drive root)
        return last.FullName;
    }

    /// <summary>
    ///     Build the canonical repo-relative DB relative path (".repoql/index.duckdb").
    /// </summary>
    public static string DefaultDbRelativePath(string dbFileName = "index.duckdb")
    {
        return Path.Combine(".repoql", dbFileName).Replace('\\', '/');
    }


    /// <summary>
    ///     Ensure the repository-local ".repoql" directory exists and return its absolute path.
    ///     If a file already occupies that path it is renamed aside so RepoQL can create the required directory.
    /// </summary>
    public static string EnsureRepoqlDirectory(string repoRootPath)
    {
        if (string.IsNullOrWhiteSpace(repoRootPath))
        {
            throw new ArgumentException("Repository root path cannot be null or empty", nameof(repoRootPath));
        }

        var resolvedRoot = Path.GetFullPath(repoRootPath);
        var repoqlDir = Path.Combine(resolvedRoot, ".repoql");

        if (Directory.Exists(repoqlDir))
        {
            return repoqlDir;
        }

        if (File.Exists(repoqlDir))
        {
            var backup = BuildBackupPath(repoqlDir);
            try
            {
                File.Move(repoqlDir, backup);
            }
            catch (Exception ex)
            {
                throw new IOException(
                    $"A file named .repoql exists at {repoqlDir}, but RepoQL needs that path to be a directory. " +
                    "Delete or move the file and rerun the command.",
                    ex);
            }
        }

        Directory.CreateDirectory(repoqlDir);
        return repoqlDir;
    }

    private static string BuildBackupPath(string originalPath)
    {
        var timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
        var candidate = $"{originalPath}.bak-{timestamp}";
        var suffix = 0;
        while (Directory.Exists(candidate) || File.Exists(candidate))
        {
            suffix++;
            candidate = $"{originalPath}.bak-{timestamp}-{suffix}";
        }

        return candidate;
    }
}