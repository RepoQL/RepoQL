namespace RepoQL.Formats.Ruby.Surface;

public sealed record RubyMixinInfo(
    string ModuleName,
    string Mechanism,
    int Ordinal);
