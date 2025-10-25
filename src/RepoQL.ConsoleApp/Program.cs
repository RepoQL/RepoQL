using Grpc.Core;
using JetBrains.Annotations;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Spectre.Console;
using RepoQL.ConsoleApp.Helpers;
using ConsoleAppFramework;

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
builder.Services.AddRepoQlConsoleServices(ShouldPrewarmClient(args));
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
            console.WriteLine(rpcEx.Status.Detail, Color.Red);
        }
        catch (Exception e)
        {
            console.WriteLine(e.GetBaseException().Message, Color.Red);
        }
    }
}