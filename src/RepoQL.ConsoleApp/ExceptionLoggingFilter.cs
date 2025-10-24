using Grpc.Core;
using JetBrains.Annotations;
using Spectre.Console;

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