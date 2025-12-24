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
