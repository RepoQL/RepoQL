namespace RepoQL.Formats.Rust.Surface;

public sealed record RustMacroInvocationInfo(
    string MacroName,
    RustByteRange ByteRange);