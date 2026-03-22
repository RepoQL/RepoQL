namespace RepoQL.Formats.Python.Surface;

public sealed record PythonMetaprogrammingHint(
    string PatternName,
    PythonByteRange ByteRange,
    bool Extractable);
