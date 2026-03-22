using System.Collections.Immutable;

namespace RepoQL.Contracts;

/// <summary>
///     Maps character offsets to 1-based line/column positions for UTF-16 strings.
/// </summary>
public sealed class TextLineMap
{
    private readonly ImmutableArray<int> _lineStarts;

    public TextLineMap(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var builder = ImmutableArray.CreateBuilder<int>(Math.Max(8, text.Length / 32));
        builder.Add(0);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                builder.Add(i + 1);
            }
        }
        _lineStarts = builder.ToImmutable();
        TextLength = text.Length;
    }

    public int LineCount => _lineStarts.Length;

    public int TextLength { get; }

    public DocumentSpan GetSpan(int startChar, int endChar)
    {
        if (startChar < 0 || startChar > TextLength) throw new ArgumentOutOfRangeException(nameof(startChar));
        if (endChar < startChar || endChar > TextLength) throw new ArgumentOutOfRangeException(nameof(endChar));

        var (startLine, startColumn) = GetPosition(startChar);
        var (endLine, endColumn) = startChar == endChar ? (startLine, startColumn) : GetPosition(endChar - 1);
        return new DocumentSpan(startChar, endChar, startLine, startColumn, endLine, endColumn);
    }

    public (int line, int column) GetPosition(int charIndex)
    {
        if (charIndex < 0 || charIndex > TextLength)
            throw new ArgumentOutOfRangeException(nameof(charIndex));

        var line = BinarySearchLine(charIndex);
        var lineStart = _lineStarts[line];
        return (line + 1, (charIndex - lineStart) + 1);
    }

    public int GetOffset(int line, int column)
    {
        if (line < 1 || line > _lineStarts.Length)
            throw new ArgumentOutOfRangeException(nameof(line));
        var start = _lineStarts[line - 1];
        var offset = start + Math.Max(0, column - 1);
        if (offset > TextLength)
            return TextLength;
        return offset;
    }

    private int BinarySearchLine(int charIndex)
    {
        var lo = 0;
        var hi = _lineStarts.Length - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >> 1;
            var start = _lineStarts[mid];
            if (start <= charIndex)
            {
                if (mid == _lineStarts.Length - 1) return mid;
                if (_lineStarts[mid + 1] > charIndex) return mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }
        return Math.Max(0, lo - 1);
    }
}
