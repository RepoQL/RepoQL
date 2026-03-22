namespace RepoQL.Formats.Ruby.Surface;

public sealed record RubyRequireInfo(
    string Path,
    bool IsRelative,
    RubyByteRange ByteRange);
