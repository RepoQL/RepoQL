namespace RepoQL.Formats.Go.GoMod;

internal sealed record GoModInfo(
    string? ModulePath,
    string? GoVersion,
    string? Toolchain,
    IReadOnlyList<GoModRequirement> Requirements,
    IReadOnlyList<GoModReplacement> Replacements,
    IReadOnlyList<GoModRetraction> Retractions,
    IReadOnlyList<GoModUse> Uses);
