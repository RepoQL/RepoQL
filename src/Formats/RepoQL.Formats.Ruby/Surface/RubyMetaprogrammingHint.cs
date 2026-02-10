namespace RepoQL.Formats.Ruby.Surface;

public sealed record RubyMetaprogrammingHint(
    string PatternName,
    RubyByteRange ByteRange,
    bool Extractable);
