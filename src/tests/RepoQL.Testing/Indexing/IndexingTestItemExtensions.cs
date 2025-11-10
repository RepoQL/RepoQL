using System.Reflection;
using RepoQL.Indexing.Indexing.Pipelines;

namespace RepoQL.Testing.Indexing;

public static class IndexingTestItemExtensions
{
    private static readonly MethodInfo SetEpochMethod =
        typeof(IndexItem).GetMethod("SetEpoch", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Unable to locate IndexItem.SetEpoch via reflection.");

    public static void SetEpoch(this IndexItem item, long epoch)
    {
        ArgumentNullException.ThrowIfNull(item);
        SetEpochMethod.Invoke(item, [epoch]);
    }
}
