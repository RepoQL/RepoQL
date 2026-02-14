namespace RepoQL.Formats.Json;

public sealed record JsonParseResult
{
    public JsonShape Shape { get; init; }
    public IReadOnlyList<JsonKeyInfo> Keys { get; init; } = [];
    public int TotalKeyCount { get; init; }
    public int MaxDepth { get; init; }
    public int? ArrayLength { get; init; }

    public static JsonParseResult Empty { get; } = new()
    {
        Shape = JsonShape.Empty,
        Keys = [],
        TotalKeyCount = 0,
        MaxDepth = 0,
        ArrayLength = null
    };
}
