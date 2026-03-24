using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using RepoQL.Contracts;
using RepoQL.Contracts.Cloud;
using RepoQL.Contracts.Data;
using RepoQL.Contracts.Embeddings;
using RepoQL.Contracts.Models;
using RepoQL.Data.DuckDB;
using ArtifactModel = RepoQL.Contracts.Models.Artifact;

namespace RepoQL.Data.DuckDB.Tests;

internal sealed class UriRegistryHydratorTests
{
    [Test]
    [DisplayName("HydrateEmbeddings only marks files embedded when embeddings match the active model")]
    public void HydrateEmbeddings_IncompatibleEmbeddingsRemainPending()
    {
        var provider = new FixedModelEmbeddingProvider("current-model", 4);
        using var database = new DuckDbDataStore(path: null, embeddingProvider: provider, logger: NullLogger<DuckDbDataStore>.Instance);

        var compatible = SeedDocument(database, "file:///repo/compatible.md", "text/plain", "compatible text");
        var incompatible = SeedDocument(database, "file:///repo/incompatible.md", "text/plain", "incompatible text");
        var binary = SeedDocument(database, "file:///repo/blob.bin", "application/octet-stream", text: null);

        database.WriteEmbeddings([
            new DocumentEmbedding(
                compatible.Id,
                compatible.Id,
                0,
                DocumentEmbedding.TypeFull,
                compatible.Uri.AbsoluteUri,
                DocumentEmbedding.ScopeDocument,
                [1f, 2f, 3f, 4f],
                "current-model",
                4),
            new DocumentEmbedding(
                incompatible.Id,
                incompatible.Id,
                0,
                DocumentEmbedding.TypeFull,
                incompatible.Uri.AbsoluteUri,
                DocumentEmbedding.ScopeDocument,
                [9f, 8f, 7f, 6f],
                "old-model",
                4)
        ]);

        var registry = new UriRegistry();
        var hydrator = new UriRegistryHydrator(
            database,
            registry,
            provider,
            null,
            null,
            NullLogger<UriRegistryHydrator>.Instance);

        hydrator.Hydrate();
        hydrator.HydrateEmbeddings();

        registry[compatible.Uri].EmbeddingStatus.Should().Be(EmbeddingStatus.Embedded);
        registry[incompatible.Uri].EmbeddingStatus.Should().Be(EmbeddingStatus.Pending);
        registry[binary.Uri].EmbeddingStatus.Should().Be(EmbeddingStatus.NotApplicable);
    }

    [Test]
    [DisplayName("HydrateEmbeddings keeps ONNX-only files pending when paid cloud access prefers contextual embeddings")]
    public void HydrateEmbeddings_PaidCloudAccessPrefersContextualCompatibility()
    {
        var provider = new FixedModelEmbeddingProvider("onnx-model", 4);
        using var database = new DuckDbDataStore(path: null, embeddingProvider: provider, logger: NullLogger<DuckDbDataStore>.Instance);

        var document = SeedDocument(database, "file:///repo/onnx-only.md", "text/plain", "text");
        database.WriteEmbeddings([
            new DocumentEmbedding(
                document.Id,
                document.Id,
                0,
                DocumentEmbedding.TypeFull,
                document.Uri.AbsoluteUri,
                DocumentEmbedding.ScopeDocument,
                [1f, 2f, 3f, 4f],
                "onnx-model",
                4)
        ]);

        var registry = new UriRegistry();
        var hydrator = new UriRegistryHydrator(
            database,
            registry,
            provider,
            new ThrowingContextualEmbeddingProvider(),
            new FixedCloudAuthStatusProvider(isAuthenticated: true, isPayingCustomer: true),
            NullLogger<UriRegistryHydrator>.Instance);

        hydrator.Hydrate();
        hydrator.HydrateEmbeddings();

        registry[document.Uri].EmbeddingStatus.Should().Be(EmbeddingStatus.Pending);
    }

    private static Node SeedDocument(DuckDbDataStore database, string uri, string mediaType, string? text)
    {
        var artifact = new ArtifactModel
        {
            Id = Guid.NewGuid(),
            Digest = Guid.NewGuid().ToString("N"),
            Size = text?.Length ?? 0,
            MediaType = SemanticMediaType.Parse(mediaType),
            Text = text,
            Headline = "Headline",
            Structure = "- Item"
        };

        var documentNode = new Node
        {
            Id = Guid.NewGuid(),
            Kind = "document",
            Uri = RepoUri.Parse(uri),
            ArtifactId = artifact.Id,
            Props = new System.Text.Json.Nodes.JsonObject(),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        database.IndexArtifact(documentNode.Uri, new ParsedArtifact
        {
            Artifact = artifact,
            DocumentNode = documentNode,
            Children = [],
            Spans = [],
            Edges = []
        });

        return documentNode;
    }

    private sealed class FixedModelEmbeddingProvider(string model, int dimension) : IEmbeddingProvider
    {
        public bool Enabled => true;
        public string Model => model;
        public int Dimension => dimension;

        public Task<float[]?> EmbedQueryAsync(string text, CancellationToken cancellationToken = default)
            => Task.FromResult<float[]?>(null);

        public Task<float[]?> EmbedPassageAsync(string text, CancellationToken cancellationToken = default)
            => Task.FromResult<float[]?>(null);

        public Task<float[]?[]> EmbedQueryBatchAsync(IReadOnlyList<string>? texts, CancellationToken cancellationToken = default)
            => Task.FromResult(texts?.Select(_ => (float[]?)null).ToArray() ?? []);

        public Task<float[]?[]> EmbedPassageBatchAsync(IReadOnlyList<string>? texts, CancellationToken cancellationToken = default)
            => Task.FromResult(texts?.Select(_ => (float[]?)null).ToArray() ?? []);

        public Task<float[]?[]> EmbedPassageBatchAsync(IReadOnlyList<string>? texts, BatchEmbeddingProgress progress, CancellationToken cancellationToken = default)
            => Task.FromResult(texts?.Select(_ => (float[]?)null).ToArray() ?? []);
    }

    private sealed class ThrowingContextualEmbeddingProvider : IContextualEmbeddingProvider
    {
        public string Model => "unknown";
        public int Dimension => 0;
        public bool Enabled => true;

        public Task InitializeAsync(CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("cloud service unavailable");

        public void SetUseCaseHint(string useCase)
        {
        }

        public Task<ContextualEmbeddingResult> EmbedChunksAsync(IReadOnlyList<DocumentChunkGroup> groups, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<float[]?> EmbedQueryAsync(string text, CancellationToken cancellationToken = default)
            => Task.FromResult<float[]?>(null);
    }

    private sealed class FixedCloudAuthStatusProvider(bool isAuthenticated, bool isPayingCustomer) : ICloudAuthStatusProvider
    {
        public ValueTask<CloudAuthStatus> GetStatusAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new CloudAuthStatus(
                IsAuthenticated: isAuthenticated,
                IsPayingCustomer: isPayingCustomer,
                AccessMethod: isAuthenticated ? CloudAccessMethod.Session : CloudAccessMethod.None,
                OrganizationId: isPayingCustomer ? "org_test" : null));
    }
}
