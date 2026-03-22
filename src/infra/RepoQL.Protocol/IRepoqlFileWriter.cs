namespace RepoQL.Protocol;

/// <summary>
/// Purpose: Provide a write surface for repository-local ".repoql" files.
/// Complexity: Keeps write operations explicit while read paths stay on IFileProvider.
/// </summary>
public interface IRepoqlFileWriter
{
    string RepoRoot { get; }

    string RepoqlDirectory { get; }

    void WriteAllText(string relativePath, string contents);
}
