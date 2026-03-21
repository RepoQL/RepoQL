using RepoQL.Contracts;

namespace RepoQL.Protocol;

/// <summary>
/// Purpose: Write repository-local ".repoql" files on the physical filesystem.
/// Complexity: Ensures the directory exists and normalizes relative paths.
/// </summary>
public sealed class PhysicalRepoqlFileWriter : IRepoqlFileWriter
{
    public PhysicalRepoqlFileWriter(string repoRoot)
    {
        if (string.IsNullOrWhiteSpace(repoRoot))
            throw new ArgumentException("Repository root cannot be null or empty.", nameof(repoRoot));

        RepoRoot = Path.GetFullPath(repoRoot);
        RepoqlDirectory = RepoLocator.EnsureRepoqlDirectory(RepoRoot);
    }

    public string RepoRoot { get; }

    public string RepoqlDirectory { get; }

    public void WriteAllText(string relativePath, string contents)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            throw new ArgumentException("Relative path cannot be null or empty.", nameof(relativePath));

        var trimmed = relativePath.Trim().TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullPath = Path.Combine(RepoqlDirectory, trimmed);
        File.WriteAllText(fullPath, contents);
    }
}
