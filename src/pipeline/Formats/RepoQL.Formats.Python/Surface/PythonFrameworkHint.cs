namespace RepoQL.Formats.Python.Surface;

public sealed record PythonFrameworkHint(
    string Kind,
    string RuleId,
    string Message,
    PythonByteRange ByteRange);
