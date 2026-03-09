namespace RepoQL.Formats.Mermaid.Core;

/// <summary>
/// Half-open span using 0-based offset and length.
/// </summary>
public readonly record struct TextSpan(int Start, int Length)
{
    public int End => Start + Length;

    public static TextSpan FromBounds(int start, int end)
        => new(start, end - start);

    public bool Contains(int position) => position >= Start && position < End;

    public bool Intersects(TextSpan other)
        => !(other.End <= Start || other.Start >= End);
}
