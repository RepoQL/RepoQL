namespace RepoQL.Formats.Python.Surface;

public sealed record PythonQueryCaptureGroup(
    int PatternIndex,
    IReadOnlyList<PythonQueryCapture> Captures);
