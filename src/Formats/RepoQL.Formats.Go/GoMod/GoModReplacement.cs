namespace RepoQL.Formats.Go.GoMod;

internal sealed record GoModReplacement(
    string OldPath,
    string? OldVersion,
    string NewPath,
    string? NewVersion,
    bool IsLocalPath);
