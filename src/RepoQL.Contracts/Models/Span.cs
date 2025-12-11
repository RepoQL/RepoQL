namespace RepoQL.Contracts.Models;

/// <summary>
///     Byte and line range extent within a single document node used for precise edits and diagnostics.
/// </summary>
public sealed record Span
{
    /// <summary>
    ///     Gets the span identifier. Value is a generated Guid.
    /// </summary>
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>
    ///     Gets the identifier of the document node this span belongs to.
    /// </summary>
    public required Guid DocumentId { get; init; }

    /// <summary>
    ///     Gets the half-open start byte offset for the span, or <c>null</c> when not tracked.
    /// </summary>
    public long? StartByte { get; init; }

    /// <summary>
    ///     Gets the half-open end byte offset for the span, or <c>null</c> when not tracked.
    /// </summary>
    public long? EndByte { get; init; }

    /// <summary>
    ///     Gets the 1-based starting line number, or <c>null</c> when not tracked.
    /// </summary>
    public int? StartLine { get; init; }

    /// <summary>
    ///     Gets the 1-based starting column number, or <c>null</c> when not tracked.
    /// </summary>
    public int? StartColumn { get; init; }

    /// <summary>
    ///     Gets the 1-based ending line number, or <c>null</c> when not tracked.
    /// </summary>
    public int? EndLine { get; init; }

    /// <summary>
    ///     Gets the 1-based ending column number, or <c>null</c> when not tracked.
    /// </summary>
    public int? EndColumn { get; init; }
}
