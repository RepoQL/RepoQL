using Microsoft.Extensions.Hosting;
using ConsoleAppFramework;
using Grpc.Core;
using JetBrains.Annotations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RepoQL.ConsoleApp.Helpers;
using Spectre.Console;

var app = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings
{
    ApplicationName = "RepoQL", 
    Args = args
}).ToConsoleAppBuilder();

app.ConfigureDefaultConfiguration(c => c
    .AddEnvironmentVariables()
    .AddCommandLine(args));

AnsiConsole.Profile.Width = 250;

app.ConfigureServices(s =>
{
    s.AddRepoQlConsoleServices();
});

app.UseFilter<ExceptionLoggingFilter>();

await app.RunAsync(args);

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
