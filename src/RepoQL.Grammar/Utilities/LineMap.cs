namespace RepoQL.Grammar.Utilities;

internal sealed class LineMap
{
    private readonly int[] _starts;
    public LineMap(string s)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < s.Length; i++)
            if (s[i] == '\n') starts.Add(i + 1);
        _starts = starts.ToArray();
    }

    public (int line, int col) ToLineCol(int index)
    {
        index = Math.Clamp(index, 0, Math.Max(0, _starts[^1]));
        var line = Array.BinarySearch(_starts, index);
        if (line < 0) line = ~line - 1;
        return (line + 1, index - _starts[line] + 1);
    }
}
