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
        if (TryFindRepoRoot(startPath, out var root, out var _, allowFallback: true))
        {
            return root!;
        }

        // Fallback: this should never happen because allowFallback returns the last directory
        return Path.GetFullPath(string.IsNullOrWhiteSpace(startPath)
            ? Directory.GetCurrentDirectory()
            : startPath);
    }

    /// <summary>
    ///     Attempt to find a repository root starting at <paramref name="startPath" />.
    ///     Returns true and sets <paramref name="repoRoot" /> when a marker is found; otherwise false.
    ///     When <paramref name="allowFallback" /> is true, <paramref name="repoRoot" /> is populated with the last
    ///     directory visited (drive root) even when no marker is found.
    /// </summary>
    public static bool TryFindRepoRoot(string? startPath, out string? repoRoot, out string? searchedFrom, bool allowFallback = false)
    {
        var currentDirectory = Path.GetFullPath(Directory.GetCurrentDirectory());
        var start = Path.GetFullPath(string.IsNullOrWhiteSpace(startPath)
            ? currentDirectory
            : startPath);

        var implicitCurrentStart =
            string.IsNullOrWhiteSpace(startPath) ||
            PathsEqual(start, currentDirectory);

        if (implicitCurrentStart &&
            !HasRepoMarker(start) &&
            TryGetPwdCandidate(currentDirectory, out var pwdCandidate) &&
            TryFindMarkerRoot(pwdCandidate, out var pwdMarkerRoot, out _, out _))
        {
            repoRoot = pwdMarkerRoot;
            searchedFrom = start;
            return true;
        }

        if (TryFindMarkerRoot(start, out var markerRoot, out var startSearchedFrom, out var startFallbackRoot))
        {
            repoRoot = markerRoot;
            searchedFrom = startSearchedFrom;
            return true;
        }

        searchedFrom = startSearchedFrom;

        repoRoot = allowFallback ? startFallbackRoot : null;
        return false;
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

    private static bool TryFindMarkerRoot(
        string startPath,
        out string? repoRoot,
        out string searchedFrom,
        out string fallbackRoot)
    {
        var dir = new DirectoryInfo(startPath);
        searchedFrom = dir.FullName;
        var last = dir;

        while (dir != null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ".git")) ||
                File.Exists(Path.Combine(dir.FullName, ".git")) ||
                Directory.Exists(Path.Combine(dir.FullName, ".repoql")))
            {
                repoRoot = dir.FullName;
                fallbackRoot = dir.FullName;
                return true;
            }

            last = dir;
            dir = dir.Parent;
        }

        repoRoot = null;
        fallbackRoot = last.FullName;
        return false;
    }

    private static bool HasRepoMarker(string directoryPath)
        => Directory.Exists(Path.Combine(directoryPath, ".git")) ||
           File.Exists(Path.Combine(directoryPath, ".git")) ||
           Directory.Exists(Path.Combine(directoryPath, ".repoql"));

    private static bool TryGetPwdCandidate(string currentDirectory, out string pwdCandidate)
    {
        pwdCandidate = string.Empty;
        var pwd = Environment.GetEnvironmentVariable("PWD");
        if (string.IsNullOrWhiteSpace(pwd))
        {
            return false;
        }

        // Some launchers pass template placeholders when interpolation fails.
        var pwdSpan = pwd.AsSpan();
        if (pwdSpan.Contains('{') || pwdSpan.Contains('}'))
        {
            return false;
        }

        string fullPwd;
        try
        {
            fullPwd = Path.GetFullPath(pwd);
        }
        catch
        {
            return false;
        }

        if (PathsEqual(fullPwd, currentDirectory))
        {
            return false;
        }

        pwdCandidate = fullPwd;
        return true;
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(left),
            Path.TrimEndingDirectorySeparator(right),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);
}
