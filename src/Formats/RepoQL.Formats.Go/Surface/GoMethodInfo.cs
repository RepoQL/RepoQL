namespace RepoQL.Formats.Go.Surface;

public sealed record GoMethodInfo(
    string Name,
    bool IsExported,
    string ReceiverName,
    string ReceiverType,
    bool IsPointerReceiver,
    string? Parameters,
    string? ReturnType,
    GoByteRange ByteRange);

