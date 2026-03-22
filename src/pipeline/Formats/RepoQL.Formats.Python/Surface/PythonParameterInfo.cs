namespace RepoQL.Formats.Python.Surface;

public sealed record PythonParameterInfo(
    string Name,
    string? Type,
    string? Default,
    PythonParameterKind Kind);

public enum PythonParameterKind
{
    PositionalOnly,
    PositionalOrKeyword,
    KeywordOnly,
    VarPositional,
    VarKeyword
}
