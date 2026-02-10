namespace RepoQL.Formats.Ruby.Surface;

public sealed record RubyModuleInfo(
    string Name,
    string QualifiedName,
    int NestingDepth,
    IReadOnlyList<RubyMethodInfo> Methods,
    IReadOnlyList<RubyConstantInfo> Constants,
    IReadOnlyList<RubyMixinInfo> Mixins,
    RubyByteRange ByteRange);
