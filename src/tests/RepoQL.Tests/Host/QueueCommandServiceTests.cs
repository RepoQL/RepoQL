using AwesomeAssertions;
using FakeItEasy;
using RepoQL.ConsoleApp.Host;
using RepoQL.Contracts;
using RepoQL.Contracts.Diagnostics;

namespace RepoQL.Tests.Host;

internal sealed class QueueCommandServiceTests : IDisposable
{
    private readonly string _repoRoot;
    private readonly string _skipListPath;
    private readonly UriRegistry _registry;
    private readonly IIndexingDiagnosticsProvider _diagnostics;
    private readonly QueueCommandService _service;

    public QueueCommandServiceTests()
    {
        _repoRoot = Path.Combine(Path.GetTempPath(), "repoql-queue-command-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_repoRoot, ".repoql"));
        _skipListPath = Path.Combine(_repoRoot, ".repoql", "skip-list.txt");

        _registry = new UriRegistry();
        _diagnostics = A.Fake<IIndexingDiagnosticsProvider>();
        A.CallTo(() => _diagnostics.GetQueuedItems()).Returns(Array.Empty<QueuedItemInfo>());
        _service = new QueueCommandService(_registry, new RepositoryConfiguration { Path = _repoRoot }, _diagnostics);
    }

    public void Dispose()
    {
        if (Directory.Exists(_repoRoot))
            Directory.Delete(_repoRoot, recursive: true);
    }

    [Test]
    public void Cancel_SetsFailedForQueuedUri()
    {
        var uri = RepoUri.Parse("file:///repo/cancel-me.cs");
        _registry.TryRegisterDiscovered(uri);
        _registry.SetIndexing(uri);
        A.CallTo(() => _diagnostics.GetQueuedItems()).Returns(
        [
            new QueuedItemInfo
            {
                Uri = uri.AbsoluteUri,
                Name = "cancel-me.cs",
                Stage = "HotPath",
                Status = "processing",
                EnqueuedAt = DateTimeOffset.UtcNow,
                Epoch = 1,
                MimeType = "text/plain",
                Size = 10,
                ReadOnly = false
            }
        ]);

        var result = _service.Execute(QueueControlAction.Cancel, uri.AbsoluteUri);

        result.Success.Should().BeTrue();
        result.Message.Should().Contain($"Cancelled: {uri}");
        result.Message.Should().Contain("was Indexing in HotPath");
        _registry[uri].Status.Should().Be(UriStatus.Failed);
        _registry[uri].Error.Should().Be("Cancelled by user");
    }

    [Test]
    public void Cancel_UnknownUri_ReturnsNotFoundError()
    {
        var result = _service.Execute(QueueControlAction.Cancel, "file:///repo/missing.cs");

        result.Success.Should().BeFalse();
        result.Message.Should().Be("Not found: file:///repo/missing.cs");
    }

    [Test]
    public void Cancel_IndexedUri_ReturnsAlreadyMessage()
    {
        var uri = RepoUri.Parse("file:///repo/indexed.cs");
        _registry.SetIndexed(uri, lineCount: 1, new Dictionary<RepoUri, SymbolEntry>().AsReadOnly());

        var result = _service.Execute(QueueControlAction.Cancel, uri.AbsoluteUri);

        result.Success.Should().BeTrue();
        result.Message.Should().Be($"Already Indexed: {uri}");
    }

    [Test]
    public void Skip_PersistsAndMarksRegistry()
    {
        var uri = RepoUri.Parse("file:///repo/skip.cs");
        _registry.TryRegisterDiscovered(uri);

        var result = _service.Execute(QueueControlAction.Skip, uri.AbsoluteUri);

        result.Success.Should().BeTrue();
        result.Message.Should().Be($"Skipped: {uri} (will not be processed)");
        _registry[uri].Status.Should().Be(UriStatus.Skipped);
        File.Exists(_skipListPath).Should().BeTrue();
        File.ReadAllText(_skipListPath).Should().Contain(uri.AbsoluteUri);
    }

    [Test]
    public void Skip_AlreadySkipped_IsIdempotent()
    {
        var uri = RepoUri.Parse("file:///repo/already-skipped.cs");
        _registry.SetSkipped(uri, "Skipped by user");
        File.WriteAllText(_skipListPath, $"{uri}{Environment.NewLine}");

        var result = _service.Execute(QueueControlAction.Skip, uri.AbsoluteUri);

        result.Success.Should().BeTrue();
        result.Message.Should().Be($"Already skipped: {uri}");
    }

    [Test]
    public void Retry_ResetsFailedToDiscovered()
    {
        var uri = RepoUri.Parse("file:///repo/retry-failed.cs");
        _registry.SetFailed(uri, "parse failed");

        var result = _service.Execute(QueueControlAction.Retry, uri.AbsoluteUri);

        result.Success.Should().BeTrue();
        result.Message.Should().Contain($"Re-enqueued: {uri}");
        result.Message.Should().Contain("previous: Failed, error: parse failed");
        _registry[uri].Status.Should().Be(UriStatus.Discovered);
        _registry[uri].Error.Should().BeNull();
    }

    [Test]
    public void Retry_ResetsSkippedAndRemovesFromSkipList()
    {
        var uri = RepoUri.Parse("file:///repo/retry-skipped.cs");
        _registry.SetSkipped(uri, "Skipped by user");
        File.WriteAllText(_skipListPath, $"{uri}{Environment.NewLine}");

        var result = _service.Execute(QueueControlAction.Retry, uri.AbsoluteUri);

        result.Success.Should().BeTrue();
        result.Message.Should().Contain("previous: Skipped");
        _registry[uri].Status.Should().Be(UriStatus.Discovered);
        File.ReadAllText(_skipListPath).Should().NotContain(uri.AbsoluteUri);
    }

    [Test]
    public void Retry_OnNonRetryableState_ReturnsError()
    {
        var uri = RepoUri.Parse("file:///repo/cannot-retry.cs");
        _registry.TryRegisterDiscovered(uri);
        _registry.SetIndexing(uri);

        var result = _service.Execute(QueueControlAction.Retry, uri.AbsoluteUri);

        result.Success.Should().BeFalse();
        result.Message.Should().Be($"Cannot retry: {uri} is Indexing");
    }
}
