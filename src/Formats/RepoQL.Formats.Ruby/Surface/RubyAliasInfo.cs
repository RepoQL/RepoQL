namespace RepoQL.Formats.Ruby.Surface;

public sealed record RubyAliasInfo(
    string NewName,
    string OriginalName,
    string AliasType,
    RubyByteRange ByteRange);
