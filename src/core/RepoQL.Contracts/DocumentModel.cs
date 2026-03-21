using System.Collections.Immutable;

namespace RepoQL.Contracts;

/// <summary>
///     In-memory representation of a loaded document including raw text, semantic media type, and optional syntax payload.
/// </summary>
public sealed class DocumentModel(
    RepoUri uri,
    SemanticMediaType mediaType,
    string text,
    object? syntaxTree = null,
    IReadOnlyDictionary<string, object?>? metadata = null,
    DateTimeOffset? loadedAt = null)
{
    public RepoUri Uri { get; } = uri ?? throw new ArgumentNullException(nameof(uri));

    public SemanticMediaType MediaType { get; } = mediaType ?? throw new ArgumentNullException(nameof(mediaType));

    public string Text { get; } = text ?? throw new ArgumentNullException(nameof(text));

    public object? SyntaxTree { get; } = syntaxTree;

    public IReadOnlyDictionary<string, object?> Metadata { get; } = metadata is null ? ImmutableDictionary<string, object?>.Empty : metadata.ToImmutableDictionary();

    public TextLineMap LineMap { get; } = new(text);

    public DateTimeOffset LoadedAt { get; } = loadedAt ?? DateTimeOffset.UtcNow;

    public DocumentSpan GetSpan(int startChar, int length)
    {
        var endChar = Math.Min(Text.Length, startChar + Math.Max(0, length));
        return LineMap.GetSpan(startChar, endChar);
    }

    public DocumentModel WithMetadata(string key, object? value)
    {
        var dict = Metadata.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        dict[key] = value;
        return new DocumentModel(Uri, MediaType, Text, SyntaxTree, dict, LoadedAt);
    }

    public T? GetMetadataOrDefault<T>(string key)
    {
        if (Metadata.TryGetValue(key, out var value) && value is T typed)
        {
            return typed;
        }
        return default;
    }
}
