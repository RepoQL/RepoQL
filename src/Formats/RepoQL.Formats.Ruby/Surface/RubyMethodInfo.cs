namespace RepoQL.Formats.Ruby.Surface;

public sealed record RubyMethodInfo(
    string Name,
    string Visibility,
    bool IsStatic,
    string? ParameterText,
    bool AcceptsBlock,
    RubyByteRange ByteRange);
