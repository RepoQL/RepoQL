namespace RepoQL.Formats.Rust.Surface;

public sealed record RustQueryCaptureGroup(
    int PatternIndex,
    IReadOnlyList<RustQueryCapture> Captures);