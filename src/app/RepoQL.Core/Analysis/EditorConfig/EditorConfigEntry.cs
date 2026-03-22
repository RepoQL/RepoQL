namespace RepoQL.Core.Analysis.EditorConfig;

internal sealed class EditorConfigEntry
{
    public IReadOnlyList<string> Patterns { get; init; } = new List<string>();
    public IDictionary<string, string> Properties { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}
