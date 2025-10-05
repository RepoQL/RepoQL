using Spectre.Console.Cli;

internal sealed class QuerySettings : RepoOptionSettings
{
    [CommandArgument(0, "<SQL>")]
    public string Sql { get; init; } = string.Empty;
}