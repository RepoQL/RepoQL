using RepoQL.Contracts;
using RepoQL.Formats.Python.Surface;

namespace RepoQL.Formats.Python;

internal sealed record PythonDocumentState(
    PythonDocumentSurface Surface,
    string Digest,
    long Size,
    SemanticMediaType MediaType,
    string StoreUri);
