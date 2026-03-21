namespace RepoQL.Protocol;

/// <summary>
/// Thrown when no repository markers (".git" or ".repoql") can be found starting from a working directory.
/// </summary>
public sealed class RepoRootNotFoundException : InvalidOperationException
{
    public RepoRootNotFoundException()
        : base("No repository markers (.git or .repoql) were found.")
    {
        SearchedFrom = string.Empty;
    }

    public RepoRootNotFoundException(string searchedFrom)
        : base($"No repository markers (.git or .repoql) were found starting at '{searchedFrom}'.")
    {
        SearchedFrom = searchedFrom;
    }

    public RepoRootNotFoundException(string message, Exception innerException)
        : base(message, innerException)
    {
        SearchedFrom = string.Empty;
    }

    public string SearchedFrom { get; }
}
