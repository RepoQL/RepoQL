namespace RepoQL.Formats.Go.Surface;

public sealed record GoQueryCapture(
    string Name,
    string Text,
    GoByteRange ByteRange);

