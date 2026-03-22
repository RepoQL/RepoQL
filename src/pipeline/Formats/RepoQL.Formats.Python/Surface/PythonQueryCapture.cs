namespace RepoQL.Formats.Python.Surface;

public sealed record PythonQueryCapture(
    string Name,
    string Text,
    PythonByteRange ByteRange);
