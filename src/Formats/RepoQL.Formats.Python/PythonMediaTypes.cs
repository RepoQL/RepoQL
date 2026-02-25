using RepoQL.Contracts;

namespace RepoQL.Formats.Python;

internal static class PythonMediaTypes
{
    public static readonly SemanticMediaType Python =
        SemanticMediaType.Create("text", "x-python").WithKind("code.python");

    public static readonly SemanticMediaType PythonStub =
        SemanticMediaType.Create("text", "x-python").WithKind("code.python.stub");

    public static bool IsSupportedKind(string? kind)
        => kind is "code.python" or "code.python.stub";

    public static bool TryResolve(string fileName, out SemanticMediaType? mediaType)
    {
        var ext = Path.GetExtension(fileName);
        if (ext is ".py" or ".pyw")
        {
            mediaType = Python;
            return true;
        }

        if (ext is ".pyi")
        {
            mediaType = PythonStub;
            return true;
        }

        var name = Path.GetFileName(fileName);
        if (name is "conftest.py" or "setup.py" or "__init__.py" or "__main__.py")
        {
            mediaType = Python;
            return true;
        }

        mediaType = null;
        return false;
    }
}
