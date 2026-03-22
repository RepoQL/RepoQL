namespace RepoQL.Formats.Ruby.Surface;

public sealed record RubyDocumentSurface(
    IReadOnlyList<RubyClassInfo> Classes,
    IReadOnlyList<RubyModuleInfo> Modules,
    IReadOnlyList<RubyMethodInfo> Functions,
    IReadOnlyList<RubyRequireInfo> Requires,
    IReadOnlyList<RubyAliasInfo> Aliases,
    IReadOnlyList<RubyMetaprogrammingHint> MetaprogrammingHints,
    RubyParseStats Stats,
    int ErrorNodeCount);
