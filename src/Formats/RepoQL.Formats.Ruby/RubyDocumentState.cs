using RepoQL.Contracts;
using RepoQL.Formats.Ruby.Surface;

namespace RepoQL.Formats.Ruby;

internal sealed record RubyDocumentState(
    RubyDocumentSurface Surface,
    string Digest,
    long Size,
    SemanticMediaType MediaType,
    string StoreUri);
