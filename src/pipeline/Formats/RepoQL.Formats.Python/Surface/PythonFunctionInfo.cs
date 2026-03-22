namespace RepoQL.Formats.Python.Surface;

public sealed record PythonFunctionInfo(
    string Name,
    bool IsAsync,
    bool IsGenerator,
    bool IsAsyncGenerator,
    bool UsesAsyncWith,
    bool UsesAsyncFor,
    IReadOnlyList<PythonDecoratorInfo> Decorators,
    IReadOnlyList<PythonParameterInfo> Parameters,
    string? ReturnType,
    string? Docstring,
    PythonByteRange ByteRange);
