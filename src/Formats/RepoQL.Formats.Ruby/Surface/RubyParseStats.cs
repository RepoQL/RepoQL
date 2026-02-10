namespace RepoQL.Formats.Ruby.Surface;

public sealed record RubyParseStats(
    int ClassCount,
    int ModuleCount,
    int MethodCount,
    int LineCount);
