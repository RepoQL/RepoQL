using System.ComponentModel;
using Spectre.Console.Cli;

internal sealed class XraySettings : RepoOptionSettings
{
    [CommandOption("--search <TEXT>")]
    [Description("Search text for file_search(). If provided, globs are ignored.")]
    public string? Search { get; init; }

    [CommandOption("--level <auto|headline|summary|structure>")]
    [Description("X-ray level (default: auto).")]
    public string? Level { get; init; }

    [CommandOption("--top <N>")]
    [Description("Max results (default 50).")]
    public int Top { get; init; } = 50;

    [CommandArgument(0, "[inputs]")]
    [Description("Globs/dirs/files/URIs (comma or space separated), e.g., docs/**/*.md")]
    public string[] Patterns { get; init; } = [];
}