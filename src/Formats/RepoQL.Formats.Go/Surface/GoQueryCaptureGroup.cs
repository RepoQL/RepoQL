namespace RepoQL.Formats.Go.Surface;

public sealed record GoQueryCaptureGroup(
    int PatternIndex,
    IReadOnlyList<GoQueryCapture> Captures);
