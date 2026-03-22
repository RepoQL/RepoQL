namespace RepoQL.Formats.Ruby.Surface;

public sealed record RubyQueryCaptureGroup(
    int PatternIndex,
    IReadOnlyList<RubyQueryCapture> Captures);
