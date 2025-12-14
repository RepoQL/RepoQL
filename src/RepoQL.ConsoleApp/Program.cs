using Grpc.Core;
using JetBrains.Annotations;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Trace;
using Spectre.Console;
using RepoQL.ConsoleApp.Helpers;
using ConsoleAppFramework;

// Defaults to 80 :(
AnsiConsole.Profile.Width = 1000;

// Disable ANSI colors when running as MCP server to prevent JSON-RPC corruption
// (ANSI escape codes in stdout corrupt the JSON protocol)
var isMcpMode = args.Any(a => a.Equals("mcp", StringComparison.OrdinalIgnoreCase));
if (isMcpMode)
{
    AnsiConsole.Profile.Capabilities.Ansi = false;
    AnsiConsole.Profile.Capabilities.Links = false;
    AnsiConsole.Profile.Capabilities.Interactive = false;

    // Force auto-flush on stdout to prevent WSL buffering delays
    // This ensures JSON-RPC responses are sent immediately
    Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
}

var explicitWorkingDirectory = Environment.GetEnvironmentVariable("REPOQL_CWD");
if (!string.IsNullOrWhiteSpace(explicitWorkingDirectory) &&
    !explicitWorkingDirectory.Contains('{') &&
    Directory.Exists(explicitWorkingDirectory))
{
    Environment.CurrentDirectory = explicitWorkingDirectory;
}

var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings
{
    ApplicationName = "RepoQL", 
    Args = args
});

builder.Configuration
    .AddEnvironmentVariables()
    .AddCommandLine(args);
builder.Logging.ClearProviders();
builder.Logging.AddOpenTelemetry();
builder.Services.AddRepoQlConsoleServices(ShouldPrewarmClient(args));
builder.Services.AddOpenTelemetry()
    .WithTracing(t => t
        .AddSource("RepoQL.*")
        .AddAspNetCoreInstrumentation())
    .UseOtlpExporter();

var app = builder.ToConsoleAppBuilder();

app.UseFilter<ExceptionLoggingFilter>();

await app.RunAsync(args);

static bool ShouldPrewarmClient(string[] commandLineArgs)
{
    foreach (var arg in commandLineArgs)
    {
        if (string.IsNullOrWhiteSpace(arg))
            continue;
        if (arg.StartsWith('-'))
            continue;

        var normalized = arg.ToLowerInvariant();
        return normalized switch
        {
            "serve" => false,
            "query" => true,
            "xray" => true,
            "reindex" => true,
            "mcp" => true,
            _ => false
        };
    }

    return false;
}

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
            // In MCP mode, write errors to stderr to avoid corrupting JSON-RPC on stdout
            if (IsMcpMode(context))
                await Console.Error.WriteLineAsync(rpcEx.Status.Detail);
            else
                console.WriteLine(rpcEx.Status.Detail, Color.Red);
        }
        catch (Exception e)
        {
            // In MCP mode, write errors to stderr to avoid corrupting JSON-RPC on stdout
            if (IsMcpMode(context))
                await Console.Error.WriteLineAsync(e.GetBaseException().ToString());
            else
                console.WriteLine(e.GetBaseException().ToString(), Color.Red);
        }
    }

    private static bool IsMcpMode(ConsoleAppFramework.ConsoleAppContext context)
        => context.Arguments.Any(a => a.Equals("mcp", StringComparison.OrdinalIgnoreCase));
}
