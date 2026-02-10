namespace RepoQL.Formats.Ruby.Surface;

public sealed record RubyAttributeInfo(
    string Name,
    string AccessorType,
    string Visibility,
    RubyByteRange ByteRange);
