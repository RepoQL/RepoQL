namespace RepoQL.Contracts;

/// <summary>
///     Character and line/column range within a document's text. Line and column are 1-based, char offsets are 0-based half-open.
/// </summary>
public readonly record struct DocumentSpan(
    int StartChar,
    int EndChar,
    int StartLine,
    int StartColumn,
    int EndLine,
    int EndColumn)
{
    public int Length => EndChar - StartChar;
}
