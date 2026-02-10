namespace RepoQL.Formats.Ruby.Surface;

public sealed record RubySingletonMethodInfo(
    string Name,
    string Receiver,
    string? ParameterText,
    RubyByteRange ByteRange);
