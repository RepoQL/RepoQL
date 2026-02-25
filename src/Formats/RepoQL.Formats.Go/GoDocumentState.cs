using RepoQL.Contracts;
using RepoQL.Formats.Go.GoMod;
using RepoQL.Formats.Go.Surface;

namespace RepoQL.Formats.Go;

internal sealed record GoDocumentState(
    GoDocumentSurface Surface,
    GoModInfo? ModuleInfo,
    string Digest,
    long Size,
    SemanticMediaType MediaType,
    string StoreUri);
