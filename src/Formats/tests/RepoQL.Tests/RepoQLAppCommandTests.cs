using System.Text;
using RepoQL.Core;
using RepoQL.Contracts;
using RepoQL.Contracts.Data;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace RepoQL.Tests;

public class RepoQLAppCommandTests
{
    private string CreateTempRepo()
    {
        var dir = Path.Combine(Path.GetTempPath(), "repoql-app-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(Path.Combine(dir, ".repoql"));
        File.WriteAllText(Path.Combine(dir, "README.md"), "# Title\n\nText\n");
        File.WriteAllText(Path.Combine(dir, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup></Project>");
        return dir;
    }

    [Test]
    public void App_Core_XrayLikeQuery_Works()
    {
        var repo = CreateTempRepo();
        try
        {
            var provider = ProgramHelpers.BuildCoreProvider(repo);
            var indexer = provider.GetRequiredService<RepositoryIndexer>();
            indexer.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
            indexer.WaitForIdle(CancellationToken.None).GetAwaiter().GetResult();
            var store = provider.GetRequiredService<IGraphStore>();
            var rows = store.RawQuery("SELECT file_name, ord, line FROM xray_lines(1, 'md_heading,nuget.package', 5) ORDER BY lower(file_name), ord").ToArray();
            rows.Length.Should().BeGreaterThan(0);
            rows.Any(r => (r["file_name"]?.ToString() ?? string.Empty).ToLowerInvariant().Contains("readme.md")).Should().BeTrue();
            indexer.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        finally { try { Directory.Delete(repo, true); } catch { } }
    }

    [Test]
    public void App_Core_Query_Works()
    {
        var repo = CreateTempRepo();
        try
        {
            var provider = ProgramHelpers.BuildCoreProvider(repo);
            var indexer = provider.GetRequiredService<RepositoryIndexer>();
            indexer.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
            indexer.WaitForIdle(CancellationToken.None).GetAwaiter().GetResult();
            var store = provider.GetRequiredService<IGraphStore>();
            var rows = store.RawQuery("SELECT COUNT(*) AS c FROM node").ToArray();
            rows.Length.Should().Be(1);
            int.Parse(rows[0]["c"]!.ToString()!).Should().BeGreaterThan(0);
            indexer.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        finally { try { Directory.Delete(repo, true); } catch { } }
    }
}
