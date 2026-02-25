namespace RepoQL.Formats.Go.Surface;

public sealed record GoImportInfo(
    string Path,
    string? Alias,
    string Category,
    GoByteRange ByteRange);
