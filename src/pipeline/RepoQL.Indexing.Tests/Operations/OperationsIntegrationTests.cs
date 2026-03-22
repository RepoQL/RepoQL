using AwesomeAssertions;
using FakeItEasy;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RepoQL.Contracts;
using RepoQL.Data.DuckDB;
using RepoQL.FileSystem.Abstractions;
using RepoQL.FileSystem.InMemory;
using RepoQL.Indexing.FileSystems;
using RepoQL.Indexing.FileSystems.Imports;
using RepoQL.Indexing.Hosting;
using RepoQL.Indexing.Indexing.Pipelines;
using RepoQL.Testing.Indexing;

namespace RepoQL.Indexing.Tests.Operations;

internal class OperationsIntegrationTests
{
    [Test]
    [DisplayName("ImportService creates operation with description and scope")]
    public async Task ImportService_CreatesOperation_WithDescriptionAndScope()
    {
        var source = RepoUri.Parse("github://owner/repo");
        var fileSystem = new MemoryFileSystem("imported");
        fileSystem.AddOrUpdateText("docs/readme.md", "readme");
        fileSystem.AddOrUpdateText("src/app.cs", "class App {}");

        var mount = CompositeFileSystemMount.ForScheme(
            id: "imported",
            fileSystem: fileSystem,
            scheme: fileSystem.Scheme,
            includeInEnumeration: true,
            enableWatching: false,
            enableAnalysis: false);

        var importer = A.Fake<IVirtualFileSystemImporter>();
        A.CallTo(() => importer.CanHandle(source)).Returns(true);
        A.CallTo(() => importer.ImportAsync(source, A<bool>._, A<CancellationToken>._)).Returns(mount);

        var mountManager = A.Fake<ICompositeFileSystemManager>();
        var uriRegistry = new UriRegistry();
        var operationManager = A.Fake<IOperationManager>();
        var operation = A.Fake<IOperation>();

        string? capturedDescription = null;
        List<RepoUri>? capturedScope = null;

        A.CallTo(() => operationManager.CreateOperation(
                A<string>._,
                A<IEnumerable<RepoUri>>._,
                A<IProgress<OperationProgress>?>._))
            .Invokes(call =>
            {
                capturedDescription = call.GetArgument<string>(0);
                var scope = call.GetArgument<IEnumerable<RepoUri>>(1)
                    ?? throw new InvalidOperationException("Operation scope was null.");
                capturedScope = scope.ToList();
            })
            .Returns(operation);

        var service = new FileSystemImportService(
            new[] { importer },
            mountManager,
            NullLogger<FileSystemImportService>.Instance,
            uriRegistry,
            operationManager);

        var result = await service.ImportAsync(source);

        result.Mount.Should().Be(mount);
        result.Operation.Should().Be(operation);
        capturedDescription.Should().Be($"import: {source}");

        var expectedUris = await EnumerateUrisAsync(fileSystem);
        capturedScope.Should().BeEquivalentTo(expectedUris);
        uriRegistry.FileUris.Should().BeEquivalentTo(expectedUris);

        A.CallTo(() => mountManager.AddOrUpdateMount(mount)).MustHaveHappenedOnceExactly();
        A.CallTo(() => operationManager.CreateOperation(
                A<string>._,
                A<IEnumerable<RepoUri>>._,
                A<IProgress<OperationProgress>?>._))
            .MustHaveHappenedOnceExactly();
    }

    [Test]
    [DisplayName("RepoqlHost creates startup operation with description and scope")]
    public async Task RepoqlHost_CreatesStartupOperation_WithDescriptionAndScope()
    {
        var primary = new MemoryFileSystem("primary");
        primary.AddOrUpdateText("docs/readme.md", "text");

        var composite = new CompositeFileSystem(CompositeFileSystemMount.CreatePrimary(primary));
        var options = Options.Create(new RepoqlHostOptions
        {
            RunFullScanOnStartup = true,
            EnableWatching = false
        });

        var uriRegistry = new UriRegistry();
        var operationManager = A.Fake<IOperationManager>();
        var operation = A.Fake<IOperation>();

        string? capturedDescription = null;
        List<RepoUri>? capturedScope = null;

        A.CallTo(() => operationManager.CreateOperation(
                A<string>._,
                A<IEnumerable<RepoUri>>._,
                A<IProgress<OperationProgress>?>._))
            .Invokes(call =>
            {
                capturedDescription = call.GetArgument<string>(0);
                var scope = call.GetArgument<IEnumerable<RepoUri>>(1)
                    ?? throw new InvalidOperationException("Operation scope was null.");
                capturedScope = scope.ToList();
            })
            .Returns(operation);

        var host = new RepoqlHost(
            composite,
            (_, _, _) => Task.CompletedTask,
            options,
            NullLogger<RepoqlHost>.Instance,
            coordinator: null,
            degradation: null,
            filter: null,
            operationManager: operationManager,
            uriRegistry: uriRegistry);

        await host.StartAsync(CancellationToken.None);
        await host.WaitForStartupAsync();
        await host.StopAsync(CancellationToken.None);
        host.Dispose();

        var expectedRepoRoot = RepoLocator.FindRepoRoot();
        var expectedUris = await EnumerateUrisAsync(primary);

        capturedDescription.Should().Be($"startup: {expectedRepoRoot}");
        capturedScope.Should().BeEquivalentTo(expectedUris);
        uriRegistry.FileUris.Should().BeEquivalentTo(expectedUris);

        A.CallTo(() => operationManager.CreateOperation(
                A<string>._,
                A<IEnumerable<RepoUri>>._,
                A<IProgress<OperationProgress>?>._))
            .MustHaveHappenedOnceExactly();
    }

    [Test]
    [DisplayName("IndexingCoordinator creates reindex operation with description")]
    public async Task IndexingCoordinator_CreatesReindexOperation_WithDescription()
    {
        var fileSystem = new MemoryFileSystem("primary");
        fileSystem.AddOrUpdateText("docs/readme.md", "text");
        fileSystem.AddOrUpdateText("src/app.cs", "class App {}");

        var composite = new CompositeFileSystem(CompositeFileSystemMount.CreatePrimary(fileSystem));

        using var dataStore = new DuckDbDataStore(path: ":memory:");
        var filter = A.Fake<IUriFilter>();
        A.CallTo(() => filter.IncludeFile(A<RepoUri>._)).Returns(true);

        var uriRegistry = new UriRegistry();
        var context = IndexingEngineTestFactory.Create(builder =>
        {
            builder.WithFilter(filter);
            builder.WithUriRegistry(uriRegistry);
        });

        var operationManager = A.Fake<IOperationManager>();
        var operation = A.Fake<IOperation>();

        string? capturedDescription = null;
        List<RepoUri>? capturedScope = null;

        A.CallTo(() => operationManager.CreateOperation(
                A<string>._,
                A<IEnumerable<RepoUri>>._,
                A<IProgress<OperationProgress>?>._))
            .Invokes(call =>
            {
                capturedDescription = call.GetArgument<string>(0);
                var scope = call.GetArgument<IEnumerable<RepoUri>>(1)
                    ?? throw new InvalidOperationException("Operation scope was null.");
                capturedScope = scope.ToList();
            })
            .Returns(operation);

        var coordinator = new IndexingCoordinator(
            composite,
            context.Engine,
            dataStore,
            NullLogger<IndexingCoordinator>.Instance,
            mountManager: null,
            gitIndexer: null,
            operationManager: operationManager,
            uriRegistry: uriRegistry);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var operationResult = await DriveReindexAndWaitForOperationAsync(coordinator, cts.Token);

        var expectedUris = await EnumerateUrisAsync(fileSystem);

        operationResult.Should().Be(operation);
        capturedDescription.Should().Be($"reindex: {expectedUris.Count} files");
        capturedScope.Should().BeEquivalentTo(expectedUris);

        A.CallTo(() => operationManager.CreateOperation(
                A<string>._,
                A<IEnumerable<RepoUri>>._,
                A<IProgress<OperationProgress>?>._))
            .MustHaveHappenedOnceExactly();

        await context.Engine.DisposeAsync();
    }

    [Test]
    [DisplayName("Operations degrade gracefully when IOperationManager is null")]
    public async Task Operations_DegradeGracefully_WhenOperationManagerIsNull()
    {
        var source = RepoUri.Parse("github://owner/repo");
        var importFileSystem = new MemoryFileSystem("imported");
        importFileSystem.AddOrUpdateText("docs/readme.md", "readme");

        var mount = CompositeFileSystemMount.ForScheme(
            id: "imported",
            fileSystem: importFileSystem,
            scheme: importFileSystem.Scheme,
            includeInEnumeration: true,
            enableWatching: false,
            enableAnalysis: false);

        var importer = A.Fake<IVirtualFileSystemImporter>();
        A.CallTo(() => importer.CanHandle(source)).Returns(true);
        A.CallTo(() => importer.ImportAsync(source, A<bool>._, A<CancellationToken>._)).Returns(mount);

        var mountManager = A.Fake<ICompositeFileSystemManager>();
        var importService = new FileSystemImportService(
            new[] { importer },
            mountManager,
            NullLogger<FileSystemImportService>.Instance,
            uriRegistry: new UriRegistry(),
            operationManager: null);

        var importResult = await importService.ImportAsync(source);

        importResult.Operation.Should().BeNull();
        importResult.Mount.Should().Be(mount);
        A.CallTo(() => mountManager.AddOrUpdateMount(mount)).MustHaveHappenedOnceExactly();

        var primary = new MemoryFileSystem("primary");
        primary.AddOrUpdateText("docs/readme.md", "text");
        var composite = new CompositeFileSystem(CompositeFileSystemMount.CreatePrimary(primary));
        var options = Options.Create(new RepoqlHostOptions
        {
            RunFullScanOnStartup = true,
            EnableWatching = false
        });

        var sink = new RecordingSink();
        var host = new RepoqlHost(
            composite,
            sink.Handler,
            options,
            NullLogger<RepoqlHost>.Instance,
            coordinator: null,
            degradation: null,
            filter: null,
            operationManager: null,
            uriRegistry: null);

        await host.StartAsync(CancellationToken.None);
        await sink.WaitForAsync(1, TimeSpan.FromSeconds(2));
        await host.StopAsync(CancellationToken.None);
        host.Dispose();

        sink.Uris.Should().ContainSingle(uri => uri.AbsoluteUri == "mem://primary/docs/readme.md");

        var reindexFileSystem = new MemoryFileSystem("reindex");
        reindexFileSystem.AddOrUpdateText("docs/readme.md", "text");
        var reindexComposite = new CompositeFileSystem(CompositeFileSystemMount.CreatePrimary(reindexFileSystem));

        using var dataStore = new DuckDbDataStore(path: ":memory:");
        var filter = A.Fake<IUriFilter>();
        A.CallTo(() => filter.IncludeFile(A<RepoUri>._)).Returns(true);

        var uriRegistry = new UriRegistry();
        var context = IndexingEngineTestFactory.Create(builder =>
        {
            builder.WithFilter(filter);
            builder.WithUriRegistry(uriRegistry);
        });

        var coordinator = new IndexingCoordinator(
            reindexComposite,
            context.Engine,
            dataStore,
            NullLogger<IndexingCoordinator>.Instance,
            mountManager: null,
            gitIndexer: null,
            operationManager: null,
            uriRegistry: uriRegistry);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var reindexOperation = await DriveReindexAndWaitForOperationAsync(coordinator, cts.Token);

        reindexOperation.Should().BeNull();

        await context.Engine.DisposeAsync();
    }

    private static async Task<IReadOnlyList<RepoUri>> EnumerateUrisAsync(MemoryFileSystem fileSystem)
    {
        var uris = new List<RepoUri>();
        await foreach (var file in fileSystem.EnumerateAsync(CancellationToken.None))
        {
            if (!file.Exists)
                continue;

            uris.Add(fileSystem.GetUri(file));
        }

        return uris;
    }

    private static async Task<IOperation?> DriveReindexAndWaitForOperationAsync(
        IndexingCoordinator coordinator,
        CancellationToken cancellationToken)
    {
        var reindex = (IReindexOperation)coordinator.ReindexAsync(new ReindexRequestOptions(false), cancellationToken);

        await using var enumerator = reindex.GetAsyncEnumerator(cancellationToken);
        while (await enumerator.MoveNextAsync())
        {
            if (reindex.Operation.IsCompleted)
                break;
        }

        return await reindex.Operation.WaitAsync(cancellationToken);
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
