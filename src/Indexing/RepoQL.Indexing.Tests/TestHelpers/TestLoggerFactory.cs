using System;
using Microsoft.Extensions.Logging;
using TUnit.Core;
using TUnitLogger = TUnit.Core.Logging.ILogger;

namespace RepoQL.Indexing.Tests.TestHelpers;

/// <summary>
/// Lightweight logger factory that routes Microsoft.Extensions.Logging calls into TUnit's logger.
/// </summary>
internal sealed class TestLoggerFactory : ILoggerFactory
{
    private readonly TUnitLogger _tunitLogger;

    private TestLoggerFactory(TUnitLogger tunitLogger)
    {
        _tunitLogger = tunitLogger ?? throw new ArgumentNullException(nameof(tunitLogger));
    }

    public static TestLoggerFactory Create()
    {
        var context = TestContext.Current ?? throw new InvalidOperationException(
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

internal static class TestLogging
{
    public static ILogger<T> CreateLogger<T>()
    {
        using var factory = TestLoggerFactory.Create();
        return factory.CreateLogger<T>();
    }

    public static ILoggerFactory CreateLoggerFactory() => TestLoggerFactory.Create();
}
