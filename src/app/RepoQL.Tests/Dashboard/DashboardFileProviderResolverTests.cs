using AwesomeAssertions;
using RepoQL.ConsoleApp.Dashboard;
using RepoQL.Client.Diagnostics;
using RepoQL.Client.Host;
using RepoQL.ConsoleApp.Host;

namespace RepoQL.Tests.Dashboard;

/// <summary>
/// Purpose: Verify dashboard file resolution across extracted files and embedded assets.
/// Complexity: Exercises the resolver directly without starting the full host.
/// </summary>
[NotInParallel(nameof(DashboardFileProviderResolverTests))]
internal sealed class DashboardFileProviderResolverTests : IDisposable
{
    private readonly List<string> _tempDirs = [];

    [Test]
    public void Resolve_PrefersExtractedWwwrootFiles_WhenPresent()
    {
        var tempDir = CreateTempDirectory();
        var wwwrootDir = Path.Combine(tempDir, "wwwroot");
        Directory.CreateDirectory(wwwrootDir);
        File.WriteAllText(Path.Combine(wwwrootDir, "index.html"), "physical-dashboard");

        var provider = DashboardFileProviderResolver.Resolve(typeof(HostState).Assembly, tempDir);

        provider.Should().NotBeNull();
        Read(provider!, "index.html").Should().Be("physical-dashboard");
    }

    [Test]
    public void Resolve_FallsBackToEmbeddedDashboard_WhenExtractedFilesAreMissing()
    {
        var tempDir = CreateTempDirectory();

        var provider = DashboardFileProviderResolver.Resolve(typeof(HostState).Assembly, tempDir);

        provider.Should().NotBeNull();
        Read(provider!, "index.html").Should().Contain("RepoQL Dashboard");
    }
    [Test]
    public void HostDiagnosticsStore_RoundTripsDashboardBindReport()
    {
        var tempDir = CreateTempDirectory();

        HostDiagnosticsStore.TryWriteReport(tempDir, "dashboard-bind.json", new DashboardBindReport("http://127.0.0.1:53333", "2026-03-10T19:35:03.0000000Z"), HostDiagnosticsStore.JsonContext.DashboardBindReport).Should().BeTrue();
        HostDiagnosticsStore.TryReadReport(tempDir, "dashboard-bind.json", HostDiagnosticsStore.JsonContext.DashboardBindReport, out var report).Should().BeTrue();
        report.Should().NotBeNull();
        report!.Url.Should().Be("http://127.0.0.1:53333");
        report.StartedAt.Should().Be("2026-03-10T19:35:03.0000000Z");
    }

    public void Dispose()
    {
        foreach (var directory in _tempDirs)
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch
            {
                // Best-effort cleanup for temp test directories.
            }
        }
    }

    private string CreateTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"repoql-dashboard-provider-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        _tempDirs.Add(directory);
        return directory;
    }

    private static string Read(Microsoft.Extensions.FileProviders.IFileProvider provider, string path)
    {
        var file = provider.GetFileInfo(path);
        file.Exists.Should().BeTrue();
        using var reader = new StreamReader(file.CreateReadStream());
        return reader.ReadToEnd();
    }
}

