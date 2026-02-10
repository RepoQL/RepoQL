namespace RepoQL.Formats.Ruby.Surface;

public sealed record RubyClassInfo(
    string Name,
    string QualifiedName,
    string? Superclass,
    bool HasSuperclassDeclaration,
    IReadOnlyList<RubyMethodInfo> Methods,
    IReadOnlyList<RubySingletonMethodInfo> SingletonMethods,
    IReadOnlyList<RubyConstantInfo> Constants,
    IReadOnlyList<RubyAttributeInfo> Attributes,
    IReadOnlyList<RubyMixinInfo> Mixins,
    RubyByteRange ByteRange);
