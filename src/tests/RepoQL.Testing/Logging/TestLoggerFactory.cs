using Microsoft.Extensions.Logging;
using TUnitLogger = TUnit.Core.Logging.ILogger;

namespace RepoQL.Testing.Logging;

/// <summary>
/// Lightweight logger factory that routes Microsoft.Extensions.Logging calls into TUnit's logger.
/// </summary>
public sealed class TestLoggerFactory : ILoggerFactory
{
    private readonly TUnitLogger _tunitLogger;

    private TestLoggerFactory(TUnitLogger tunitLogger)
    {
        _tunitLogger = tunitLogger ?? throw new ArgumentNullException(nameof(tunitLogger));
    }

    public static TestLoggerFactory Create()
    {
        var context = TUnit.Core.TestContext.Current ?? throw new InvalidOperationException(
            "TestLoggerFactory requires an active TUnit test context.");
        return new TestLoggerFactory(context.GetDefaultLogger());
    }

    public ILogger CreateLogger(string categoryName)
    {
        return new TUnitLoggerWrapper(_tunitLogger, categoryName);
    }

    public void AddProvider(ILoggerProvider provider)
    {
        throw new NotSupportedException("Custom logger providers are not supported in tests.");
    }

    public void Dispose()
    {
        // Nothing to dispose - the underlying TUnit logger is owned by the test runner.
    }
}

/// <summary>Logger provider that forwards to TUnit's logger.</summary>
internal sealed class TUnitLoggerProvider(TUnitLogger tunitLogger) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new TUnitLoggerWrapper(tunitLogger, categoryName);
    public void Dispose()
    {
    }
}

/// <summary>Wrapper that adapts TUnit's ILogger to Microsoft.Extensions.Logging.ILogger.</summary>
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

public static class TestLogging
{
    public static ILogger<T> CreateLogger<T>()
    {
        using var factory = TestLoggerFactory.Create();
        return factory.CreateLogger<T>();
    }

    public static ILoggerFactory CreateLoggerFactory() => TestLoggerFactory.Create();
}
