namespace RepoQL.Core.Analysis.EditorConfig;

internal sealed class EditorConfigDocument
{
    public bool IsRoot { get; init; }
    public IReadOnlyList<EditorConfigEntry> Entries { get; init; } = new List<EditorConfigEntry>();
}
