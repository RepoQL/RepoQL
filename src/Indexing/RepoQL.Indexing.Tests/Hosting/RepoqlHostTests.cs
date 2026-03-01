using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RepoQL.Contracts;
using RepoQL.FileSystem.InMemory;
using RepoQL.Indexing.FileSystems;
using RepoQL.Indexing.Hosting;
using RepoQL.Indexing.Indexing.Pipelines;

namespace RepoQL.Indexing.Tests.Hosting;

internal class RepoqlHostTests
{
    [Test]
    [DisplayName("Full scan on startup enqueues every discovered file")]
    public async Task Given_FullScanEnabled_When_Starts_Then_QueuesEnumeratedFiles()
    {
        var primary = new MemoryFileSystem("primary");
        primary.AddOrUpdateText("docs/readme.md", "text");

        var composite = new CompositeFileSystem(CompositeFileSystemMount.CreatePrimary(primary));
        var sink = new RecordingSink();
        var options = Options.Create(new RepoqlHostOptions
        {
            RunFullScanOnStartup = true,
            EnableWatching = false
        });

        var host = new RepoqlHost(composite, sink.Handler, options, NullLogger<RepoqlHost>.Instance);

        await host.StartAsync(CancellationToken.None);
        await sink.WaitForAsync(1, TimeSpan.FromSeconds(2));
        await host.StopAsync(CancellationToken.None);

        sink.Uris.Should().ContainSingle(uri => uri.AbsoluteUri == "mem://primary/docs/readme.md");
    }

    [Test]
    [DisplayName("Full scan skips URIs listed in .repoql/skip-list.txt")]
    public async Task Given_SkipListEntry_When_StartupScanRuns_Then_SkippedUriIsNotEnqueued()
    {
        var tempRoot = CreateRepoRoot();
        try
        {
            var skipUri = "mem://primary/docs/skip.md";
            File.WriteAllText(Path.Combine(tempRoot, ".repoql", "skip-list.txt"), $"{skipUri}{Environment.NewLine}");

            var primary = new MemoryFileSystem("primary");
            primary.AddOrUpdateText("docs/skip.md", "skip me");
            primary.AddOrUpdateText("docs/keep.md", "keep me");

            var composite = new CompositeFileSystem(CompositeFileSystemMount.CreatePrimary(primary));
            var sink = new RecordingSink();
            var registry = new UriRegistry();
            var options = Options.Create(new RepoqlHostOptions
            {
                RunFullScanOnStartup = true,
                EnableWatching = false
            });

            var host = new RepoqlHost(
                composite,
                sink.Handler,
                options,
                NullLogger<RepoqlHost>.Instance,
                uriRegistry: registry,
                repoConfig: new RepositoryConfiguration { Path = tempRoot });

            await host.StartAsync(CancellationToken.None);
            await sink.WaitForAsync(1, TimeSpan.FromSeconds(2));
            await host.StopAsync(CancellationToken.None);

            sink.Uris.Should().ContainSingle(uri => uri.AbsoluteUri == "mem://primary/docs/keep.md");

            var skippedRepoUri = RepoUri.Parse(skipUri);
            registry[skippedRepoUri].Status.Should().Be(UriStatus.Skipped);
        }
        finally
        {
            CleanupRepoRoot(tempRoot);
        }
    }

    [Test]
    [DisplayName("Removing a URI from skip-list re-enqueues it on next startup scan")]
    public async Task Given_UriRemovedFromSkipList_When_HostRestarts_Then_UriIsEnqueued()
    {
        var tempRoot = CreateRepoRoot();
        try
        {
            var skipUri = "mem://primary/docs/retry.md";
            var skipPath = Path.Combine(tempRoot, ".repoql", "skip-list.txt");
            File.WriteAllText(skipPath, $"{skipUri}{Environment.NewLine}");

            var primary = new MemoryFileSystem("primary");
            primary.AddOrUpdateText("docs/retry.md", "retry me");

            var composite = new CompositeFileSystem(CompositeFileSystemMount.CreatePrimary(primary));
            var options = Options.Create(new RepoqlHostOptions
            {
                RunFullScanOnStartup = true,
                EnableWatching = false
            });

            var firstSink = new RecordingSink();
            var firstHost = new RepoqlHost(
                composite,
                firstSink.Handler,
                options,
                NullLogger<RepoqlHost>.Instance,
                repoConfig: new RepositoryConfiguration { Path = tempRoot });

            await firstHost.StartAsync(CancellationToken.None);
            await Task.Delay(150);
            await firstHost.StopAsync(CancellationToken.None);
            firstSink.Uris.Should().BeEmpty();

            File.WriteAllText(skipPath, string.Empty);

            var secondSink = new RecordingSink();
            var secondHost = new RepoqlHost(
                composite,
                secondSink.Handler,
                options,
                NullLogger<RepoqlHost>.Instance,
                repoConfig: new RepositoryConfiguration { Path = tempRoot });

            await secondHost.StartAsync(CancellationToken.None);
            await secondSink.WaitForAsync(1, TimeSpan.FromSeconds(2));
            await secondHost.StopAsync(CancellationToken.None);

            secondSink.Uris.Should().ContainSingle(uri => uri.AbsoluteUri == skipUri);
        }
        finally
        {
            CleanupRepoRoot(tempRoot);
        }
    }

    [Test]
    [DisplayName("File watcher enqueues updates after startup")]
    public async Task Given_WatchingEnabled_When_FileAdded_Then_EnqueuesArtifact()
    {
        var primary = new MemoryFileSystem("primary");
        var composite = new CompositeFileSystem(CompositeFileSystemMount.CreatePrimary(primary));
        var sink = new RecordingSink();
        var options = Options.Create(new RepoqlHostOptions
        {
            RunFullScanOnStartup = false,
            EnableWatching = true
        });

        var host = new RepoqlHost(composite, sink.Handler, options, NullLogger<RepoqlHost>.Instance);
        await host.StartAsync(CancellationToken.None);
        await host.WaitForStartupAsync();

        primary.AddOrUpdateText("docs/new-file.md", "hello");

        await sink.WaitForAsync(1, TimeSpan.FromSeconds(2));
        await host.StopAsync(CancellationToken.None);

        sink.Uris.Should().Contain(uri => uri.AbsoluteUri == "mem://primary/docs/new-file.md");
    }

    private static string CreateRepoRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), "repoql-host-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        Directory.CreateDirectory(Path.Combine(path, ".repoql"));
        return path;
    }

    private static void CleanupRepoRoot(string path)
    {
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
    }

    private sealed class RecordingSink
    {
        private readonly List<RepoUri> _uris = new();
        private readonly TaskCompletionSource<bool> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Func<RawArtifact, IndexItemOptions, CancellationToken, Task> Handler =>
            (artifact, _, _) =>
            {
                lock (_uris)
                {
                    _uris.Add(artifact.Uri);
                    _tcs.TrySetResult(true);
                }

                return Task.CompletedTask;
            };

        public IReadOnlyList<RepoUri> Uris
        {
            get
            {
                lock (_uris)
                    return _uris.ToList();
            }
        }

        public async Task WaitForAsync(int expected, TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            while (!cts.IsCancellationRequested)
            {
                bool satisfied;
                lock (_uris)
                {
                    satisfied = _uris.Count >= expected;
                    if (satisfied)
                        return;
                }

                var wait = _tcs.Task;
                await Task.WhenAny(wait, Task.Delay(10, cts.Token)).ConfigureAwait(false);
            }

            int observed;
            lock (_uris)
            {
                observed = _uris.Count;
            }
            throw new TimeoutException($"Expected at least {expected} enqueued artifacts but saw {observed}.");
        }
    }
}
