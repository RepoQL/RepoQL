using Microsoft.Extensions.Logging;
using RepoQL.Testing.Indexing;
using RepoQL.Testing.Logging;

namespace RepoQL.Testing.Formats;

/// <summary>Base class for format integration suites. Supplies common helpers.</summary>
public abstract class FormatIntegrationTestBase
{
    protected static ILogger<T> CreateLogger<T>() => TestLogging.CreateLogger<T>();

    protected static IndexingTestItemBuilder CreateTestItem(string filename)
        => IndexingTestItemBuilder.ForFile(filename);

    protected static FormatTestHarness.FormatHarnessBuilder CreateHarness()
        => FormatTestHarness.Create();
}
