using Microsoft.Extensions.Logging;

namespace RepoQL.Indexing.Tests.TestHelpers;

/// <summary>
/// Base class for format integration tests.
/// Provides common infrastructure and utilities that all format tests need.
/// </summary>
public abstract class FormatIntegrationTestBase
{
    /// <summary>
    /// Creates a logger for the specified type using TUnit's logging infrastructure.
    /// </summary>
    protected static ILogger<T> CreateLogger<T>() => TestLogging.CreateLogger<T>();
    

    /// <summary>
    /// Creates a test item builder for the specified file.
    /// </summary>
    protected static TestItemBuilder CreateTestItem(string filename)
    {
        return TestItemBuilder.ForFile(filename);
    }

    /// <summary>
    /// Creates a new harness builder.
    /// </summary>
    protected static FormatTestHarness.Builder CreateHarness()
    {
        return FormatTestHarness.Create();
    }
}
