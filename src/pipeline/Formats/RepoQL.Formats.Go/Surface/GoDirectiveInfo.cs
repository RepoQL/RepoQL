namespace RepoQL.Formats.Go.Surface;

public sealed record GoDirectiveInfo(
    string Kind,
    string Text,
    GoByteRange ByteRange);

