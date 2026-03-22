using System.Text.Json;

namespace RepoQL.Formats.Json;

public sealed record JsonKeyInfo
{
    public string Path { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public int Depth { get; init; }
    public JsonValueKind ValueKind { get; init; }
    public int StartLine { get; init; }
    public int EndLine { get; init; }
    public int EstimatedTokens { get; init; }
    public string? ScalarValue { get; init; }
    public int? ArrayLength { get; init; }
    public bool IsNodeEligible { get; init; }
}
