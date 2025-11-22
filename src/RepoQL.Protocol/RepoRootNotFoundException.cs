using System.Runtime.Serialization;

namespace RepoQL.Protocol;

/// <summary>
/// Thrown when no repository markers (".git" or ".repoql") can be found starting from a working directory.
/// </summary>
[Serializable]
public sealed class RepoRootNotFoundException : InvalidOperationException
{
    public RepoRootNotFoundException(string searchedFrom)
        : base($"No repository markers (.git or .repoql) were found starting at '{searchedFrom}'.")
    {
        SearchedFrom = searchedFrom;
    }

    private RepoRootNotFoundException(SerializationInfo info, StreamingContext context)
        : base(info, context)
    {
        SearchedFrom = info.GetString(nameof(SearchedFrom)) ?? string.Empty;
    }

    public string SearchedFrom { get; }

    public override void GetObjectData(SerializationInfo info, StreamingContext context)
    {
        base.GetObjectData(info, context);
        info.AddValue(nameof(SearchedFrom), SearchedFrom);
    }
}
