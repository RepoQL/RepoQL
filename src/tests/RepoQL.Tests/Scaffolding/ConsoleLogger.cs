using Microsoft.Extensions.Logging;

namespace RepoQL.Tests.Scaffolding;

public sealed class ConsoleLogger<T> : ILogger<T>
{
    public IDisposable BeginScope<TState>(TState state) where TState : notnull => new Noop();
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        try
        {
            Console.WriteLine($"[{logLevel}] {formatter(state, exception)}");
            if (exception != null) Console.WriteLine(exception);
        }
        catch { /* avoid test crash on logging */ }
    }
    private sealed class Noop : IDisposable { public void Dispose() { } }
}