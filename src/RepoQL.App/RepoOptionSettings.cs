using System.ComponentModel;
using Spectre.Console.Cli;

internal class RepoOptionSettings : CommandSettings
{
    [CommandOption("--repo <PATH>")]
    [Description("Path to repository root (optional). If omitted, auto-detect.")]
    public string? Repo { get; init; }
}