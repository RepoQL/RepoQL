namespace RepoQL.Formats.Go.Surface;

public sealed record GoVariableInfo(
    string Name,
    string? TypeName,
    string? Value,
    bool IsExported,
    bool IsSentinelError,
    bool IsInterfaceAssertion,
    string? AssertedInterface,
    string? AssertedType,
    GoByteRange ByteRange);

