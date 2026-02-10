namespace RepoQL.Formats.Ruby.Surface;

public sealed record RubyQueryCapture(
    string Name,
    string Text,
    RubyByteRange ByteRange);
