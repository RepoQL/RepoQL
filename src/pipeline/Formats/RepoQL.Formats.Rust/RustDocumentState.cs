using RepoQL.Contracts;
using RepoQL.Formats.Rust.Surface;

namespace RepoQL.Formats.Rust;

internal sealed record RustDocumentState(
    RustDocumentSurface Surface,
    string Digest,
    long Size,
    SemanticMediaType MediaType,
    string StoreUri);
