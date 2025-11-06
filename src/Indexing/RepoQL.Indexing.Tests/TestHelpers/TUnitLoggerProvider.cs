using Microsoft.Extensions.Logging;
using TUnitLogger = TUnit.Core.Logging.ILogger;

namespace RepoQL.Indexing.Tests.TestHelpers;

/// <summary>Logger provider that forwards to TUnit's logger</summary>
internal sealed class TUnitLoggerProvider(TUnitLogger tunitLogger) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new TUnitLoggerWrapper(tunitLogger, categoryName);
    public void Dispose() { }
}

/// <summary>Wrapper that adapts TUnit's ILogger to Microsoft.Extensions.Logging.ILogger</summary>
internal sealed class TUnitLoggerWrapper(TUnitLogger tunitLogger, string categoryName) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var message = $"[{categoryName}] {formatter(state, exception)}";

        switch (logLevel)
        {
            case LogLevel.Trace:
            case LogLevel.Debug:
            case LogLevel.Information:
                tunitLogger.Log(TUnit.Core.Logging.LogLevel.Information, message, exception, (m, ex) => m);
                break;
            case LogLevel.Warning:
                tunitLogger.Log(TUnit.Core.Logging.LogLevel.Warning, message, exception, (m, ex) => m);
                break;
            case LogLevel.Error:
            case LogLevel.Critical:
                tunitLogger.Log(TUnit.Core.Logging.LogLevel.Error, message, exception, (m, ex) => m);
                break;
        }
    }
}
