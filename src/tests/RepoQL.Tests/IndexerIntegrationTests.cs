using RepoQL.Data.DuckDB;
using AwesomeAssertions;
using RepoQL.Contracts;
using RepoQL.Contracts.Models;
using RepoQL.FileSystem.Abstractions;
using RepoQL.Testing.Scaffolding;
using RepoQL.FileSystem.Embedded;
using RepoQL.Indexing.FileSystems;

namespace RepoQL.Tests;

internal class IndexerIntegrationTests
{
    private sealed class IncludeOnlyUriFilter : IUriFilter
    {
        private readonly HashSet<string> _allow;

        public IncludeOnlyUriFilter(params RepoUri[] allowed)
        {
            _allow = new HashSet<string>(allowed.Select(u => u.AbsoluteUri.ToLowerInvariant()));
        }

        public bool IncludeFile(RepoUri uri) => _allow.Contains(uri.AbsoluteUri.ToLowerInvariant());
    }

    [Test]
    [Timeout(180_000)] // 3 minutes - can be slow in CI
    public async Task StartAndWaitForIdle_IndexesMarkdownDocument_InMemoryDb(CancellationToken token)
    {
        var asm = typeof(IndexerIntegrationTests).Assembly;
        var uri = RepoUri.Parse("embed:///Resources/Doc1.md");

        await using var repo = await IndexedRepoBuilder.CreateAsync(options =>
        {
            options.Filter = new IncludeOnlyUriFilter(uri);
            options.EnableWatching = false;
            options.RunFullScanOnStartup = false;
            ConfigureFormats(options);
            options.AdditionalMounts.Add(
                CompositeFileSystemMount.ForScheme(
                    id: "embedded-docs",
                    fileSystem: new EmbeddedStore(asm),
                    scheme: "embed",
                    includeInEnumeration: true));
        });

        await repo.IndexUriAsync(uri, skipUnchanged: false, token);

        var nodes = repo.Store.GetAllNodes().ToArray();
        nodes.Should().NotBeEmpty();
        nodes.Count(n => n.Kind == "document").Should().Be(1);
        nodes.Any(n => n.Kind == "md_heading").Should().BeTrue();
        nodes.Any(n => n.Kind == "md_link").Should().BeTrue();
        nodes.Any(n => n.Kind == "md_code_block").Should().BeTrue();
    }

    [Test]
    public async Task WaitForIdle_ReflectsNewlyQueuedFiles()
    {
        var asm = typeof(IndexerIntegrationTests).Assembly;
        var uri1 = RepoUri.Parse("embed:///Resources/Doc1.md");
        var uri2 = RepoUri.Parse("embed:///Resources/Doc2.md");

        await using var repo = await IndexedRepoBuilder.CreateAsync(options =>
        {
            options.Filter = new IncludeOnlyUriFilter(uri1, uri2);
            options.EnableWatching = false;
            options.RunFullScanOnStartup = false;
            ConfigureFormats(options);
            options.AdditionalMounts.Add(
                CompositeFileSystemMount.ForScheme(
                    id: "embedded-docs",
                    fileSystem: new EmbeddedStore(asm),
                    scheme: "embed",
                    includeInEnumeration: true));
        });

        await repo.IndexUriAsync(uri1);
        var before = repo.Store.GetAllNodes().Count(n => n.Kind == "document");
        before.Should().Be(1);

        await repo.IndexUriAsync(uri2);
        var after = repo.Store.GetAllNodes().Count(n => n.Kind == "document");
        after.Should().Be(2);
    }

    private static void ConfigureFormats(IndexedRepoOptions options)
    {
        options.AddMarkdownFormat();
        options.AddMermaidFormat();
    }
}
