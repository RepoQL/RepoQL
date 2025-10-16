using Microsoft.Extensions.Hosting;
using ConsoleAppFramework;
using Grpc.Core;
using JetBrains.Annotations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using RepoQL.ConsoleApp.Helpers;
using Spectre.Console;

// Defaults to 80 :(
AnsiConsole.Profile.Width = 300;

var explicitWorkingDirectory = Environment.GetEnvironmentVariable("REPOQL_CWD");
if (!string.IsNullOrWhiteSpace(explicitWorkingDirectory))
    Environment.CurrentDirectory = explicitWorkingDirectory;

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
builder.Services.AddOpenTelemetry()
    .WithMetrics(m => m
        .AddMeter("RepoQL.*")
        .AddAspNetCoreInstrumentation()
        .AddRuntimeInstrumentation())
    .WithTracing(t => t
        .AddSource("RepoQL.*")
        .AddAspNetCoreInstrumentation())
    .UseOtlpExporter();

var app = builder.ToConsoleAppBuilder();

app.ConfigureServices(s =>
{
    s.AddRepoQlConsoleServices(ShouldPrewarmClient(args));
});

app.UseFilter<ExceptionLoggingFilter>();

await app.RunAsync(args);

static bool ShouldPrewarmClient(string[] commandLineArgs)
{
    foreach (var arg in commandLineArgs)
    {
        if (string.IsNullOrWhiteSpace(arg))
            continue;
        if (arg.StartsWith("-", StringComparison.Ordinal))
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
internal class ExceptionLoggingFilter(ConsoleAppFilter next, IAnsiConsole console) : ConsoleAppFilter(next)
{
    public override async Task InvokeAsync(ConsoleAppContext context, CancellationToken cancellationToken)
    {
        try
        {
            await Next.InvokeAsync(context, cancellationToken);
        }
        catch (RpcException rpcEx)
        {
            console.WriteLine(rpcEx.Status.Detail, Color.Red);
        }
        catch (Exception e)
        {
            console.WriteLine(e.GetBaseException().Message, Color.Red);
        }
    }
} 
