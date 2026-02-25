namespace RepoQL.Formats.Go.Surface;

public sealed record GoTypeDefinitionInfo(
    string Name,
    string? UnderlyingType,
    bool IsAlias,
    bool IsExported,
    GoByteRange ByteRange);

