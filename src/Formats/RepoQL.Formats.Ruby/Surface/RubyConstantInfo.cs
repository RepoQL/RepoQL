namespace RepoQL.Formats.Ruby.Surface;

public sealed record RubyConstantInfo(
    string Name,
    RubyByteRange ByteRange);
