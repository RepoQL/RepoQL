namespace RepoQL.Formats.Go.GoMod;

internal sealed record GoModRequirement(
    string ModulePath,
    string Version,
    bool IsIndirect);
