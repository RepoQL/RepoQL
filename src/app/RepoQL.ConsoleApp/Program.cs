using Grpc.Core;
using JetBrains.Annotations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using RepoQL.ConsoleApp.Commands;
using RepoQL.ConsoleApp.Helpers;
using RepoQL.ConsoleApp.Logging;
using ConsoleAppFramework;
using RepoQL.McpServer.Commands;

AnsiConsole.Profile.Width = 1000;

var isMcpMode = args.Any(a => a.Equals("mcp", StringComparison.OrdinalIgnoreCase));
if (isMcpMode)
{
    AnsiConsole.Profile.Capabilities.Ansi = false;
    AnsiConsole.Profile.Capabilities.Links = false;
    AnsiConsole.Profile.Capabilities.Interactive = false;

    if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("REPOQL_IMPLICIT_SOURCE")))
        Environment.SetEnvironmentVariable("REPOQL_IMPLICIT_SOURCE", "mcp");

    Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
}

var explicitWorkingDirectory = Environment.GetEnvironmentVariable("REPOQL_CWD");
if (!string.IsNullOrWhiteSpace(explicitWorkingDirectory) &&
    !explicitWorkingDirectory.Contains('{') &&
    Directory.Exists(explicitWorkingDirectory))
{
    Environment.CurrentDirectory = explicitWorkingDirectory;
}

Environment.ExitCode = await RunAsync(args, isMcpMode).ConfigureAwait(false);

static async Task<int> RunAsync(string[] commandLineArgs, bool isMcpMode)
{
    try
    {
        var mode = StartupMode.Parse(commandLineArgs);
        switch (mode.Kind)
        {
            case StartupKind.RootHelp:
                WriteRootHelp();
                return 0;

            case StartupKind.Version:
                Console.WriteLine(HostLogging.GetHostVersion());
                return 0;

            case StartupKind.Serve:
                return await RunServeAsync(mode.RemainingArgs).ConfigureAwait(false);

            case StartupKind.Mcp:
                return await RunMcpAsync(mode.RemainingArgs).ConfigureAwait(false);

            default:
                return await RunCliAsync(commandLineArgs).ConfigureAwait(false);
        }
    }
    catch (RpcException rpcEx)
    {
        await WriteErrorAsync(rpcEx.Status.Detail, isMcpMode).ConfigureAwait(false);
        return 1;
    }
    catch (Exception ex)
    {
        await WriteErrorAsync(ex.GetBaseException().ToString(), isMcpMode).ConfigureAwait(false);
        return 1;
    }
}

static async Task<int> RunServeAsync(string[] args)
{
    if (args.Any(IsHelpFlag))
    {
        WriteServeHelp();
        return 0;
    }

    var options = ParseServeOptions(args);
    var command = new HostCommands(AnsiConsole.Console);
    await command.Serve(options.Repository, options.ImplicitStart).ConfigureAwait(false);
    return 0;
}

static async Task<int> RunMcpAsync(string[] args)
{
    if (args.Any(IsHelpFlag))
    {
        WriteMcpHelp();
        return 0;
    }

    if (args.Length > 0)
        throw new ArgumentException($"Unknown arguments for mcp: {string.Join(' ', args)}");

    var command = new McpCommands();
    await command.Mcp().ConfigureAwait(false);
    return 0;
}

static async Task<int> RunCliAsync(string[] args)
{
    var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings
    {
        ApplicationName = "RepoQL",
        Args = args
    });

    builder.Configuration
        .AddEnvironmentVariables()
        .AddCommandLine(args);
    builder.Logging.ClearProviders();
    builder.Services.AddRepoQlCliServices();

    var app = builder.ToConsoleAppBuilder();
    app.UseFilter<ExceptionLoggingFilter>();

    await app.RunAsync(args).ConfigureAwait(false);
    return 0;
}

static ServeOptions ParseServeOptions(string[] args)
{
    string? repository = null;
    var implicitStart = false;

    for (var i = 0; i < args.Length; i++)
    {
        var arg = args[i];
        if (string.IsNullOrWhiteSpace(arg))
            continue;

        switch (arg)
        {
            case "--implicit-start":
                implicitStart = true;
                continue;

            case "--repository":
            case "-r":
                if (i + 1 >= args.Length)
                    throw new ArgumentException("Missing value for --repository.");

                repository = args[++i];
                continue;
        }

        if (arg.StartsWith("--repository=", StringComparison.OrdinalIgnoreCase))
        {
            repository = arg["--repository=".Length..];
            continue;
        }

        if (arg.Length > 0 && arg[0] == '-')
            throw new ArgumentException($"Unknown serve option '{arg}'.");

        if (repository is not null)
            throw new ArgumentException($"Unexpected extra serve argument '{arg}'.");

        repository = arg;
    }

    return new ServeOptions(repository, implicitStart);
}

static bool IsHelpFlag(string value)
    => value is "-h" or "--help";

static void WriteRootHelp()
{
    Console.WriteLine(
        "Usage: repoql [command] [options]\n" +
        "\n" +
        "Commands:\n" +
        "  serve      Start the RepoQL host and keep it running.\n" +
        "  mcp        Run RepoQL as an MCP server.\n" +
        "  query      Execute DuckDB SQL.\n" +
        "  execute    Run JavaScript in the RepoQL sandbox.\n" +
        "  command    Run an imperative RepoQL command.\n" +
        "  explore    Search and explore the repository.\n" +
        "  explain    Ask a question about the repository.\n" +
        "  read       Read repository content with budget control.\n" +
        "  import     Import or remove an external repository.\n" +
        "  install    Install RepoQL as an MCP server for AI agents.\n" +
        "  update     Check for and install RepoQL updates.\n" +
        "  login      Log in to RepoQL cloud services.\n" +
        "  logout     Clear the locally stored RepoQL session.\n" +
        "  whoami     Show the current RepoQL authentication identity.\n" +
        "\n" +
        "Examples:\n" +
        "  repoql read src/** --tree folders\n" +
        "  repoql read src/RepoQL.Commands/CommandRegistry.cs --symbol CommandRegistry.ExecuteAsync\n" +
        "  repoql explore authentication\n" +
        "  repoql explore --mode inventory --uri src/**\n" +
        "  Get-Content query.sql | repoql query\n" +
        "  repoql command diagnostics.fast\n" +
        "  Get-Content script.js | repoql execute \"List controllers\"\n" +
        "  repoql import ../other-repo --analyze\n" +
        "\n" +
        "Flags:\n" +
        "  -h, --help     Show this help message.\n" +
        "  --version      Show the RepoQL version.\n" +
        "\n" +
        "Run 'repoql <command> --help' for command-specific help.");
}

static void WriteServeHelp()
{
    Console.WriteLine(
        "Usage: repoql serve [repository] [--repository PATH] [--implicit-start]\n" +
        "\n" +
        "Start the RepoQL host for a repository and keep it running.\n" +
        "\n" +
        "Options:\n" +
        "  [repository]       Repository root to serve. Defaults to the current directory.\n" +
        "  --repository PATH  Explicit repository root to serve.\n" +
        "  --implicit-start   Marks the host launch as client-triggered.\n" +
        "  -h, --help         Show this help message.");
}

static void WriteMcpHelp()
{
    Console.WriteLine(
        "Usage: repoql mcp\n" +
        "\n" +
        "Run RepoQL as an MCP server over stdio.\n" +
        "\n" +
        "Options:\n" +
        "  -h, --help   Show this help message.");
}

static async Task WriteErrorAsync(string message, bool isMcpMode)
{
    if (isMcpMode)
        await Console.Error.WriteLineAsync(message).ConfigureAwait(false);
    else
        AnsiConsole.Console.WriteLine(message, Color.Red);
}

internal enum StartupKind
{
    RootHelp,
    Version,
    Serve,
    Mcp,
    Cli
}

internal readonly record struct StartupMode(StartupKind Kind, string[] RemainingArgs)
{
    public static StartupMode Parse(string[] args)
    {
        if (args.Length == 0)
            return new StartupMode(StartupKind.RootHelp, []);

        var first = args[0];
        if (first is "-h" or "--help")
            return new StartupMode(StartupKind.RootHelp, args[1..]);

        if (string.Equals(first, "--version", StringComparison.OrdinalIgnoreCase))
            return new StartupMode(StartupKind.Version, args[1..]);

        if (string.Equals(first, "serve", StringComparison.OrdinalIgnoreCase))
            return new StartupMode(StartupKind.Serve, args[1..]);

        if (string.Equals(first, "mcp", StringComparison.OrdinalIgnoreCase))
            return new StartupMode(StartupKind.Mcp, args[1..]);

        return new StartupMode(StartupKind.Cli, args);
    }
}

internal readonly record struct ServeOptions(string? Repository, bool ImplicitStart);

[UsedImplicitly]
internal class ExceptionLoggingFilter(ConsoleAppFramework.ConsoleAppFilter next, IAnsiConsole console) : ConsoleAppFramework.ConsoleAppFilter(next)
{
    public override async Task InvokeAsync(ConsoleAppFramework.ConsoleAppContext context, CancellationToken cancellationToken)
    {
        try
        {
            await Next.InvokeAsync(context, cancellationToken);
        }
        catch (RpcException rpcEx)
        {
            if (IsMcpMode(context))
                await Console.Error.WriteLineAsync(rpcEx.Status.Detail);
            else
                console.WriteLine(rpcEx.Status.Detail, Color.Red);
        }
        catch (Exception e)
        {
            var errorText = e is ArgumentException or FileNotFoundException
                ? e.GetBaseException().Message
                : e.GetBaseException().ToString();

            if (IsMcpMode(context))
                await Console.Error.WriteLineAsync(errorText);
            else
                console.WriteLine(errorText, Color.Red);
        }
    }

    private static bool IsMcpMode(ConsoleAppFramework.ConsoleAppContext context)
        => context.Arguments.Any(a => a.Equals("mcp", StringComparison.OrdinalIgnoreCase));
}


