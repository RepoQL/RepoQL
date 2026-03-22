namespace RepoQL.Contracts;

/// <summary>
///     Represents embedded content within a parent document, such as a fenced code block.
/// </summary>
public sealed class EmbeddedFragment(
    RepoUri parentUri,
    string label,
    SemanticMediaType mediaType,
    string text,
    int startChar,
    int length,
    Guid? parentNodeId = null,
    Guid? parentSpanId = null,
    object? payload = null)
{
    public RepoUri ParentUri { get; } = parentUri ?? throw new ArgumentNullException(nameof(parentUri));

    public string Label { get; } = label ?? throw new ArgumentNullException(nameof(label));

    public SemanticMediaType MediaType { get; } = mediaType ?? throw new ArgumentNullException(nameof(mediaType));

    public string Text { get; } = text ?? throw new ArgumentNullException(nameof(text));

    public int StartChar { get; } = startChar;

    public int Length { get; } = length;

    public Guid? ParentNodeId { get; } = parentNodeId;

    public Guid? ParentSpanId { get; } = parentSpanId;

    public object? Payload { get; } = payload;

    public TextLineMap LineMap { get; } = new(text);

    public DocumentSpan MapToParent(DocumentModel parent, int relativeStartChar, int relativeLength)
    {
        ArgumentNullException.ThrowIfNull(parent);
        var absoluteStart = StartChar + relativeStartChar;
        var absoluteEnd = Math.Min(parent.Text.Length, absoluteStart + Math.Max(0, relativeLength));
        return parent.LineMap.GetSpan(absoluteStart, absoluteEnd);
    }
}
