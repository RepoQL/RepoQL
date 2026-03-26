using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RepoQL.Contracts.Configuration;
using RepoQL.Contracts.Embeddings;
using RepoQL.Core;

namespace RepoQL.Core.Tests;

internal sealed class RepoIndexerServiceCollectionExtensionsTests
{
    [Test]
    public void AddRepoIndexer_WithMissingExplicitEmbeddingModel_ThrowsActionableStartupError()
    {
        using var tempDir = new TempDir();
        var config = new RepoQlConfig
        {
            Embedding = new RepoQlConfig.EmbeddingSettings
            {
                ModelPath = Path.Combine(tempDir.Path, "missing-model.onnx")
            }
        };

        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Debug));
        services.AddSingleton(config);
        services.AddRepoIndexer(tempDir.Path);

        using var provider = services.BuildServiceProvider();
        var resolve = () => provider.GetRequiredService<IEmbeddingProvider>();

        var exception = resolve.Should().Throw<InvalidOperationException>().Which;
        exception.Message.Should().Contain("missing-model.onnx");
        exception.Message.Should().Contain("requires a working ONNX embedding model");
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "repoql-core-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
