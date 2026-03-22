using System.Diagnostics.CodeAnalysis;
using AwesomeAssertions;
using RepoQL.Contracts;
using RepoQL.Indexing;
using RepoQL.Indexing.Indexing.Pipelines;
using RepoQL.Indexing.Indexing.Pipelines.Discovery;

namespace RepoQL.Formats.Json.Tests;

public sealed class JsonClassifierTests
{
    [Test]
    [Arguments("data.json")]
    [Arguments("data.jsonc")]
    [Arguments("data.json5")]
    [Arguments("data.jsonl")]
    [Arguments("data.ndjson")]
    [Arguments("DATA.JSON")]
    [DisplayName("Classifier claims JSON family extensions")]
    public async Task ProcessAsync_ClaimsJsonExtensions(string fileName)
    {
        var classifier = new JsonClassifier();
        var artifact = new TestDiscoveredArtifact(fileName);
        var nextCalled = false;

        var (result, status) = await classifier.ProcessAsync(
            artifact,
            _ =>
            {
                nextCalled = true;
                return Task.FromResult<(SemanticMediaType?, PipelineResult)>((null, PipelineResult.Success));
            },
            CancellationToken.None);

        nextCalled.Should().BeFalse();
        status.Should().Be(PipelineResult.Success);
        result.Should().NotBeNull();
        result!.Kind.Should().Be("json");
    }

    [Test]
    [DisplayName("Classifier calls next for non-JSON extensions")]
    public async Task ProcessAsync_DelegatesForNonJsonFiles()
    {
        var classifier = new JsonClassifier();
        var artifact = new TestDiscoveredArtifact("notes.txt");
        var nextCalled = false;

        var (result, status) = await classifier.ProcessAsync(
            artifact,
            _ =>
            {
                nextCalled = true;
                return Task.FromResult<(SemanticMediaType?, PipelineResult)>((null, PipelineResult.Success));
            },
            CancellationToken.None);

        nextCalled.Should().BeTrue();
        result.Should().BeNull();
        status.Should().Be(PipelineResult.Success);
    }

    private sealed class TestDiscoveredArtifact(string fileName)
        : Dictionary<string, object>(StringComparer.OrdinalIgnoreCase), IDiscoveredArtifact
    {
        public RawArtifact RawArtifact { get; } = null!;

        public IndexItemOptions Options { get; } = IndexItemOptions.Default;

        public RepoUri Uri { get; } = RepoUri.Parse($"file:///{fileName}");

        public T? Get<T>(string key)
            => TryGetValue(key, out var value) && value is T typed ? typed : default;

        public bool TryGet<T>(string key, [MaybeNullWhen(false)] out T value)
        {
            if (TryGetValue(key, out var obj) && obj is T typed)
            {
                value = typed;
                return true;
            }

            value = default;
            return false;
        }

        public bool Exists => true;
        public long Length => 0;
        public string? PhysicalPath => null;
        public string Name { get; } = fileName;
        public DateTimeOffset LastModified => DateTimeOffset.UtcNow;
        public bool IsDirectory => false;

        public Stream CreateReadStream() => new MemoryStream();
    }
}
