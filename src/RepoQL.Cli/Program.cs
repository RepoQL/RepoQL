using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.Parsing;
using Spectre.Console;

// Create root command
var rootCommand = new RootCommand("RepoQL - Query everything")
{

};

// Register commands
rootCommand.AddCommand(RepoQL.Cli.Commands.XrayCommand.Build());
rootCommand.AddCommand(RepoQL.Cli.Commands.QueryCommand.Build());

// Configure parser with custom version handling
var parser = new CommandLineBuilder(rootCommand)
    .UseDefaults()
    .UseExceptionHandler((e, inv) => AnsiConsole.WriteException(e, ExceptionFormats.ShortenEverything | ExceptionFormats.NoStackTrace))
    .UseVersionOption() // Use built-in version option
    .Build();

// Parse and invoke
return await parser.InvokeAsync(args);
