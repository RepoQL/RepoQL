using ConsoleAppFramework;
using Microsoft.Extensions.Hosting;

// Force auto-flush on stdout so JSON-RPC responses aren't buffered.
Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });

var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings
{
    ApplicationName = "RepoQL.DevHarness",
    Args = args
});

var app = builder.ToConsoleAppBuilder();
app.UseFilter<ExceptionLoggingFilter>();

await app.RunAsync(args);

/// <summary>
/// Purpose: Captures unhandled command exceptions and reports them on stderr.
/// Complexity: Thin wrapper around ConsoleAppFramework that centralizes error reporting for stdio use.
/// </summary>
internal sealed class ExceptionLoggingFilter(ConsoleAppFramework.ConsoleAppFilter next)
    : ConsoleAppFramework.ConsoleAppFilter(next)
{
    public override async Task InvokeAsync(ConsoleAppFramework.ConsoleAppContext context, CancellationToken cancellationToken)
    {
        try
        {
            await Next.InvokeAsync(context, cancellationToken);
        }
        catch (Exception e)
        {
            await Console.Error.WriteLineAsync(e.GetBaseException().ToString());
            Environment.ExitCode = 1;
        }
    }
}
